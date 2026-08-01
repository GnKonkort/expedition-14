using System.Numerics;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Keeps a SubGrid riding the MapGrid underneath it (content-only, no engine changes).
/// Pose = host ∘ local; velocity = host point velocity (+ ω×r) + relative.
/// Hard snap after solve. Client never CaptureFromWorld — only predicted snap from networked fields.
/// </summary>
public abstract class SharedSubGridHostFollowSystem : VirtualController
{
    [Dependency] protected readonly SharedTransformSystem TransformSystem = default!;
    [Dependency] protected readonly SharedPhysicsSystem Physics = default!;
    [Dependency] protected readonly SharedJointSystem Joints = default!;
    [Dependency] protected readonly IMapManager MapManager = default!;
    [Dependency] private readonly INetManager _net = default!;

    /// <summary>Pulls relative thruster velocity back toward the host (world damping is disabled while attached).</summary>
    protected const float RelativeDamping = 0.4f;

    private List<Entity<MapGridComponent>> _gridsBuffer = new();

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        var query = EntityQueryEnumerator<SubGridComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sub, out var body, out var xform))
        {
            if (sub.HostGrid is not { } host || !sub.HostPoseValid || Deleted(host))
                continue;
            if (xform.MapID == MapId.Nullspace)
                continue;

            EnsureRideBodyType(uid, sub, body);
            ApplyVelocity(uid, sub, body, host);
        }
    }

    public override void UpdateAfterSolve(bool prediction, float frameTime)
    {
        base.UpdateAfterSolve(prediction, frameTime);

        // Client: never FollowHost / CaptureFromWorld — that baked lag into HostLocal and fought the server.
        // Only snap from networked HostLocal ∘ (predicted) host.
        if (_net.IsClient || prediction)
        {
            ApplyPredictedAttachments(frameTime);
            return;
        }

        var query = EntityQueryEnumerator<SubGridComponent, PhysicsComponent, TransformComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var sub, out var body, out var xform, out var grid))
        {
            if (xform.MapID == MapId.Nullspace)
            {
                ClearHost(uid, sub);
                continue;
            }

            if (!TryGetHostUnder(uid, grid, xform, out var host))
            {
                if (sub.HostGrid != null)
                    ClearHost(uid, sub);
                continue;
            }

            FollowHost(uid, sub, body, xform, host, frameTime);
        }
    }

    /// <summary>
    /// Client / prediction: keep pad locked to host∘HostLocal for the physics tick and match velocity.
    /// Client does not integrate HostLocal (server-authoritative); FrameUpdate re-snaps to lerped host.
    /// Skipping the tick snap left a large FrameUpdate correction that jolted the rider.
    /// </summary>
    private void ApplyPredictedAttachments(float frameTime)
    {
        var query = EntityQueryEnumerator<SubGridComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sub, out var body, out var xform))
        {
            if (sub.HostGrid is not { } host || !sub.HostPoseValid || Deleted(host))
                continue;
            if (xform.MapID == MapId.Nullspace)
                continue;

            // Client: do not integrate HostLocal / thruster-relative here.
            // Mutating HostLocal locally fights the networked server value and causes
            // alternating frame snaps (padErr ~ v·dt) while the host moves.
            // Pose comes from networked HostLocal ∘ (rendered) host; velocity from networked relative.
            if (!_net.IsClient && sub.DriveEnabled)
            {
                if (sub.HostMotionSampleValid)
                {
                    ApplyThrusterRelativeDelta(sub, body);
                    DampRelative(sub, frameTime);
                    IntegrateLocalFromRelative(sub, frameTime);
                }
            }
            else if (!sub.DriveEnabled)
            {
                sub.RelativeLinearVelocity = Vector2.Zero;
                sub.RelativeAngularVelocity = 0f;
            }

            SuppressWorldDamping(uid, body);
            EnsureRideBodyType(uid, sub, body);
            // Tick-lock to host so the rider's predicted physics matches the pad.
            ApplyAttachment(uid, sub, body, xform, host);
            SampleHost(sub, host);
        }
    }

    /// <summary>Call when drive toggles so parked / relative state is recaptured cleanly.</summary>
    public void RecaptureHostPose(EntityUid gridUid, SubGridComponent sub)
    {
        if (_net.IsClient)
            return;

        var xform = Transform(gridUid);
        if (xform.MapID == MapId.Nullspace || !TryComp(gridUid, out MapGridComponent? grid))
        {
            ClearHost(gridUid, sub);
            return;
        }

        if (!TryGetHostUnder(gridUid, grid, xform, out var host))
        {
            ClearHost(gridUid, sub);
            return;
        }

        if (!TryComp(gridUid, out PhysicsComponent? body))
        {
            ClearHost(gridUid, sub);
            return;
        }

        CaptureFromWorld(gridUid, sub, body, host, freezeRelative: !sub.DriveEnabled);
        SuppressWorldDamping(gridUid, body);
        SuppressShuttleDamping(gridUid);
        EnsureRideBodyType(gridUid, sub, body);
        ApplyAttachment(gridUid, sub, body, xform, host);
        SampleHost(sub, host);
        sub.HostGrid = host;
        ClearWeldState(gridUid, sub);
        Dirty(gridUid, sub);
        OnHostAttached(gridUid, sub);
    }

    /// <summary>No-op kept for wall-separation callers; hard snap uses HostLocal directly.</summary>
    public void RefreshHostWeldAnchors(EntityUid uid, SubGridComponent sub)
    {
        Dirty(uid, sub);
    }

    /// <summary>
    /// Snap a SubGrid to host∘local for rendering (client FrameUpdate) without running physics side-effects.
    /// </summary>
    public bool TrySnapToHost(EntityUid uid, SubGridComponent sub)
    {
        if (sub.HostGrid is not { } host || !sub.HostPoseValid || Deleted(host))
            return false;
        if (!TryComp(uid, out PhysicsComponent? body) || !TryComp(uid, out TransformComponent? xform))
            return false;
        if (xform.MapID == MapId.Nullspace)
            return false;

        ApplyAttachment(uid, sub, body, xform, host);
        return true;
    }

    private void FollowHost(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        TransformComponent xform,
        EntityUid host,
        float frameTime)
    {
        var hostChanged = sub.HostGrid != host;

        if (hostChanged || !sub.HostPoseValid || !sub.HostMotionSampleValid)
        {
            CaptureFromWorld(uid, sub, body, host, freezeRelative: !sub.DriveEnabled);
            SuppressWorldDamping(uid, body);
            SuppressShuttleDamping(uid);
            EnsureRideBodyType(uid, sub, body);
            ApplyAttachment(uid, sub, body, xform, host);
            SampleHost(sub, host);
            sub.HostGrid = host;
            ClearWeldState(uid, sub);
            Dirty(uid, sub);
            OnHostAttached(uid, sub);
            return;
        }

        if (sub.DriveEnabled)
        {
            ApplyThrusterRelativeDelta(sub, body);
            DampRelative(sub, frameTime);
            IntegrateLocalFromRelative(sub, frameTime);
        }
        else
        {
            sub.RelativeLinearVelocity = Vector2.Zero;
            sub.RelativeAngularVelocity = 0f;
        }

        SuppressWorldDamping(uid, body);
        SuppressShuttleDamping(uid);
        EnsureRideBodyType(uid, sub, body);
        ApplyAttachment(uid, sub, body, xform, host);
        SampleHost(sub, host);
        sub.HostGrid = host;
        OnHostFollowTick(uid, sub, host);

        if (sub.DriveEnabled)
            Dirty(uid, sub);
    }

    private void EnsureRideBodyType(EntityUid uid, SubGridComponent sub, PhysicsComponent body)
    {
        var want = sub.DriveEnabled ? BodyType.Dynamic : BodyType.Kinematic;
        if (body.BodyType == want)
            return;

        Physics.SetBodyType(uid, want, body: body);
        if (want == BodyType.Kinematic)
            Physics.SetFixedRotation(uid, true, body: body);
    }

    private void ClearWeldState(EntityUid uid, SubGridComponent sub)
    {
        if (!sub.HostWelded && sub.HostWeldJointId == null)
            return;

        if (sub.HostWeldJointId != null)
        {
            Joints.RemoveJoint(uid, sub.HostWeldJointId);
            sub.HostWeldJointId = null;
        }

        sub.HostWelded = false;
    }

    protected virtual void OnHostAttached(EntityUid uid, SubGridComponent sub)
    {
    }

    /// <summary>Server hook each follow tick while attached (e.g. force-park if host moves).</summary>
    protected virtual void OnHostFollowTick(EntityUid uid, SubGridComponent sub, EntityUid host)
    {
    }

    protected virtual void SuppressShuttleDamping(EntityUid uid)
    {
    }

    protected virtual void RestoreShuttleDamping(EntityUid uid, SubGridComponent sub)
    {
    }

    /// <summary>Client: cancel transform interpolation that fights the hard snap.</summary>
    protected virtual void OnAttachmentSnapped(EntityUid uid, TransformComponent xform)
    {
    }

    private static void ApplyThrusterRelativeDelta(SubGridComponent sub, PhysicsComponent body)
    {
        var prevRot = sub.LastHostWorldRotation;
        var composed = sub.LastHostLinearVelocity + prevRot.RotateVec(sub.RelativeLinearVelocity);
        var thrusterWorld = body.LinearVelocity - composed;
        sub.RelativeLinearVelocity += (-prevRot).RotateVec(thrusterWorld);

        var composedAng = sub.LastHostAngularVelocity + sub.RelativeAngularVelocity;
        sub.RelativeAngularVelocity += body.AngularVelocity - composedAng;
    }

    private static void IntegrateLocalFromRelative(SubGridComponent sub, float frameTime)
    {
        if (frameTime <= 0f)
            return;

        sub.HostLocalPosition += sub.RelativeLinearVelocity * frameTime;
        sub.HostLocalRotation += new Angle(sub.RelativeAngularVelocity * frameTime);
    }

    private static void DampRelative(SubGridComponent sub, float frameTime)
    {
        var factor = MathF.Max(0f, 1f - RelativeDamping * frameTime);
        sub.RelativeLinearVelocity *= factor;
        sub.RelativeAngularVelocity *= factor;
    }

    private void SuppressWorldDamping(EntityUid uid, PhysicsComponent body)
    {
        if (body.LinearDamping != 0f)
            Physics.SetLinearDamping(uid, body, 0f);
        if (body.AngularDamping != 0f)
            Physics.SetAngularDamping(uid, body, 0f);
    }

    private void CaptureFromWorld(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        EntityUid host,
        bool freezeRelative)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = TransformSystem.GetWorldPositionRotation(hostXform);
        var (subPos, subRot) = TransformSystem.GetWorldPositionRotation(uid);

        sub.HostLocalPosition = (-hostRot).RotateVec(subPos - hostPos);
        sub.HostLocalRotation = subRot - hostRot;
        sub.HostPoseValid = true;

        if (freezeRelative)
        {
            sub.RelativeLinearVelocity = Vector2.Zero;
            sub.RelativeAngularVelocity = 0f;
            return;
        }

        TryComp(host, out PhysicsComponent? hostBody);
        var hostVel = hostBody?.LinearVelocity ?? Vector2.Zero;
        var hostAng = hostBody?.AngularVelocity ?? 0f;
        var localCenter = hostBody?.LocalCenter ?? Vector2.Zero;
        var hostPointVel = ComposeAttachmentVelocity(
            hostVel, hostAng, hostRot, sub.HostLocalPosition, localCenter, Vector2.Zero);
        sub.RelativeLinearVelocity = (-hostRot).RotateVec(body.LinearVelocity - hostPointVel);
        sub.RelativeAngularVelocity = body.AngularVelocity - hostAng;
    }

    protected void ApplyAttachment(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        TransformComponent xform,
        EntityUid host)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = TransformSystem.GetWorldPositionRotation(hostXform);

        var worldPos = hostPos + hostRot.RotateVec(sub.HostLocalPosition);
        var worldRot = hostRot + sub.HostLocalRotation;

        // Prefer NoLerp setters: client SetLocalPositionRotation activates transform lerp and fights snaps.
        if (xform.MapUid is { } mapUid && xform.ParentUid == mapUid)
        {
            var mapXform = Transform(mapUid);
            var (_, mapRot) = TransformSystem.GetWorldPositionRotation(mapXform);
            var localPos = Vector2.Transform(worldPos, TransformSystem.GetInvWorldMatrix(mapXform));
            var localRot = worldRot - mapRot;
            TransformSystem.SetLocalPositionNoLerp(uid, localPos, xform);
            TransformSystem.SetLocalRotationNoLerp(uid, localRot, xform);
        }
        else
        {
            TransformSystem.SetWorldPositionRotation(uid, worldPos, worldRot, xform);
        }

        ApplyVelocity(uid, sub, body, host);
        OnAttachmentSnapped(uid, xform);
    }

    protected void ApplyVelocity(EntityUid uid, SubGridComponent sub, PhysicsComponent body, EntityUid host)
    {
        var hostXform = Transform(host);
        var (_, hostRot) = TransformSystem.GetWorldPositionRotation(hostXform);
        TryComp(host, out PhysicsComponent? hostBody);
        var hostVel = hostBody?.LinearVelocity ?? Vector2.Zero;
        var hostAng = hostBody?.AngularVelocity ?? 0f;
        var localCenter = hostBody?.LocalCenter ?? Vector2.Zero;

        var worldVel = ComposeAttachmentVelocity(
            hostVel, hostAng, hostRot, sub.HostLocalPosition, localCenter, sub.RelativeLinearVelocity);
        Physics.SetLinearVelocity(uid, worldVel, body: body);
        Physics.SetAngularVelocity(uid, hostAng + sub.RelativeAngularVelocity, body: body);
    }

    private static Vector2 ComposeAttachmentVelocity(
        Vector2 hostVel,
        float hostAng,
        Angle hostRot,
        Vector2 hostLocalPosition,
        Vector2 hostLocalCenter,
        Vector2 relativeLocal)
    {
        var r = hostRot.RotateVec(hostLocalPosition - hostLocalCenter);
        var tangential = new Vector2(-hostAng * r.Y, hostAng * r.X);
        return hostVel + tangential + hostRot.RotateVec(relativeLocal);
    }

    private void SampleHost(SubGridComponent sub, EntityUid host)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = TransformSystem.GetWorldPositionRotation(hostXform);
        TryComp(host, out PhysicsComponent? hostBody);
        var hostVel = hostBody?.LinearVelocity ?? Vector2.Zero;
        var hostAng = hostBody?.AngularVelocity ?? 0f;
        var localCenter = hostBody?.LocalCenter ?? Vector2.Zero;

        sub.LastHostWorldPosition = hostPos;
        sub.LastHostWorldRotation = hostRot;
        sub.LastHostLinearVelocity = ComposeAttachmentVelocity(
            hostVel, hostAng, hostRot, sub.HostLocalPosition, localCenter, Vector2.Zero);
        sub.LastHostAngularVelocity = hostAng;
        sub.HostMotionSampleValid = true;
    }

    protected static void ClearHostFields(SubGridComponent sub)
    {
        sub.HostGrid = null;
        sub.HostPoseValid = false;
        sub.HostMotionSampleValid = false;
        sub.RelativeLinearVelocity = Vector2.Zero;
        sub.RelativeAngularVelocity = 0f;
        sub.HostWelded = false;
        sub.HostWeldJointId = null;
    }

    protected void ClearHost(EntityUid uid, SubGridComponent sub)
    {
        ClearWeldState(uid, sub);
        ClearHostFields(sub);
        Dirty(uid, sub);
        RestoreShuttleDamping(uid, sub);
        RestoreParkedBodyType(uid, sub);
    }

    private void RestoreParkedBodyType(EntityUid uid, SubGridComponent sub)
    {
        if (sub.DriveEnabled)
            return;
        if (!TryComp(uid, out PhysicsComponent? body))
            return;
        if (body.BodyType == BodyType.Kinematic)
            return;

        Physics.SetBodyType(uid, BodyType.Kinematic, body: body);
        Physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
        Physics.SetAngularVelocity(uid, 0f, body: body);
        Physics.SetFixedRotation(uid, true, body: body);
    }

    protected bool TryGetHostUnder(
        EntityUid self,
        MapGridComponent selfGrid,
        TransformComponent selfXform,
        out EntityUid host)
    {
        host = default;
        var mapId = selfXform.MapID;
        if (mapId == MapId.Nullspace)
            return false;

        var worldAabb = TransformSystem.GetWorldMatrix(selfXform).TransformBox(selfGrid.LocalAABB);
        var box = worldAabb.Enlarged(0.25f);

        _gridsBuffer.Clear();
        MapManager.FindGridsIntersecting(mapId, box, ref _gridsBuffer);

        EntityUid? best = null;
        var bestArea = float.MaxValue;

        foreach (var other in _gridsBuffer)
        {
            if (other.Owner == self)
                continue;
            if (HasComp<SubGridComponent>(other.Owner))
                continue;

            var area = MathF.Max(0.01f, other.Comp.LocalAABB.Width * other.Comp.LocalAABB.Height);
            if (best != null && area >= bestArea)
                continue;

            best = other.Owner;
            bestArea = area;
        }

        if (best == null)
            return false;

        host = best.Value;
        return true;
    }
}
