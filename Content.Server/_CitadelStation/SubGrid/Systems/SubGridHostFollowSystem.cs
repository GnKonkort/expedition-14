using System.Numerics;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Keeps a SubGrid riding the MapGrid underneath it at all times while overlapping.
/// World pose = host pose ∘ host-local pose.
/// World velocity = host velocity + relative velocity (thrusters change relative).
/// </summary>
public sealed class SubGridHostFollowSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SubGridDebugLog _dbg = default!;

    private List<Entity<MapGridComponent>> _gridsBuffer = new();

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(SubGridMovementSystem));
        Log.Info("SubGridHostFollowSystem initialized");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SubGridComponent, PhysicsComponent, TransformComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var sub, out var body, out var xform, out _))
        {
            if (xform.MapID == MapId.Nullspace)
            {
                ClearHost(sub);
                continue;
            }

            var center = _transform.GetWorldPosition(xform);
            if (!TryGetHostUnder(uid, xform.MapID, center, out var host))
            {
                if (sub.HostGrid != null)
                {
                    _dbg.Write("host.lost", $"grid={ToPrettyString(uid)} wasHost={sub.HostGrid}");
                    ClearHost(sub);
                }

                continue;
            }

            FollowHost(uid, sub, body, xform, host);
        }
    }

    /// <summary>Call when drive toggles so parked / relative state is recaptured cleanly.</summary>
    public void RecaptureHostPose(EntityUid gridUid, SubGridComponent sub)
    {
        var xform = Transform(gridUid);
        if (xform.MapID == MapId.Nullspace)
        {
            ClearHost(sub);
            return;
        }

        var center = _transform.GetWorldPosition(xform);
        if (!TryGetHostUnder(gridUid, xform.MapID, center, out var host))
        {
            ClearHost(sub);
            return;
        }

        if (!TryComp(gridUid, out PhysicsComponent? body))
        {
            ClearHost(sub);
            return;
        }

        CaptureFromWorld(gridUid, sub, body, host, freezeRelative: !sub.DriveEnabled);
        ApplyAttachment(gridUid, sub, body, xform, host);
        SampleHost(sub, host);
        sub.HostGrid = host;
        _dbg.Write("host.recapture",
            $"grid={ToPrettyString(gridUid)} host={ToPrettyString(host)} local={sub.HostLocalPosition} relVel={sub.RelativeLinearVelocity} drive={sub.DriveEnabled}");
    }

    private void FollowHost(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        TransformComponent xform,
        EntityUid host)
    {
        var hostChanged = sub.HostGrid != host;

        if (hostChanged || !sub.HostPoseValid || !sub.HostMotionSampleValid)
        {
            CaptureFromWorld(uid, sub, body, host, freezeRelative: !sub.DriveEnabled);
            ApplyAttachment(uid, sub, body, xform, host);
            SampleHost(sub, host);
            sub.HostGrid = host;
            _dbg.Write("host.attach",
                $"grid={ToPrettyString(uid)} host={ToPrettyString(host)} local={sub.HostLocalPosition} drive={sub.DriveEnabled}");
            return;
        }

        if (sub.DriveEnabled)
        {
            // Physics/thrusters moved us in world since last tick — fold that into host-local
            // pose relative to the *previous* host sample, then re-express on the current host.
            AbsorbWorldIntoLocal(uid, sub, body);
        }
        else
        {
            // Parked: fixed local pose, no relative velocity.
            sub.RelativeLinearVelocity = Vector2.Zero;
            sub.RelativeAngularVelocity = 0f;
        }

        ApplyAttachment(uid, sub, body, xform, host);
        SampleHost(sub, host);
        sub.HostGrid = host;
    }

    /// <summary>
    /// Update host-local pose / relative velocity from current world state vs last host sample.
    /// </summary>
    private void AbsorbWorldIntoLocal(EntityUid uid, SubGridComponent sub, PhysicsComponent body)
    {
        var (subPos, subRot) = _transform.GetWorldPositionRotation(uid);
        var prevPos = sub.LastHostWorldPosition;
        var prevRot = sub.LastHostWorldRotation;

        sub.HostLocalPosition = (-prevRot).RotateVec(subPos - prevPos);
        sub.HostLocalRotation = subRot - prevRot;
        sub.HostPoseValid = true;

        // Relative velocity in previous host frame, then keep as host-local for apply.
        var relWorld = body.LinearVelocity - sub.LastHostLinearVelocity;
        sub.RelativeLinearVelocity = (-prevRot).RotateVec(relWorld);
        sub.RelativeAngularVelocity = body.AngularVelocity - sub.LastHostAngularVelocity;
    }

    private void CaptureFromWorld(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        EntityUid host,
        bool freezeRelative)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = _transform.GetWorldPositionRotation(hostXform);
        var (subPos, subRot) = _transform.GetWorldPositionRotation(uid);

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
        sub.RelativeLinearVelocity = (-hostRot).RotateVec(body.LinearVelocity - hostVel);
        sub.RelativeAngularVelocity = body.AngularVelocity - hostAng;
    }

    private void ApplyAttachment(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        TransformComponent xform,
        EntityUid host)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = _transform.GetWorldPositionRotation(hostXform);
        TryComp(host, out PhysicsComponent? hostBody);
        var hostVel = hostBody?.LinearVelocity ?? Vector2.Zero;
        var hostAng = hostBody?.AngularVelocity ?? 0f;

        var worldPos = hostPos + hostRot.RotateVec(sub.HostLocalPosition);
        var worldRot = hostRot + sub.HostLocalRotation;
        _transform.SetWorldPositionRotation(uid, worldPos, worldRot, xform);

        var worldVel = hostVel + hostRot.RotateVec(sub.RelativeLinearVelocity);
        _physics.SetLinearVelocity(uid, worldVel, body: body);
        _physics.SetAngularVelocity(uid, hostAng + sub.RelativeAngularVelocity, body: body);
    }

    private void SampleHost(SubGridComponent sub, EntityUid host)
    {
        var hostXform = Transform(host);
        var (hostPos, hostRot) = _transform.GetWorldPositionRotation(hostXform);
        TryComp(host, out PhysicsComponent? hostBody);

        sub.LastHostWorldPosition = hostPos;
        sub.LastHostWorldRotation = hostRot;
        sub.LastHostLinearVelocity = hostBody?.LinearVelocity ?? Vector2.Zero;
        sub.LastHostAngularVelocity = hostBody?.AngularVelocity ?? 0f;
        sub.HostMotionSampleValid = true;
    }

    private static void ClearHost(SubGridComponent sub)
    {
        sub.HostGrid = null;
        sub.HostPoseValid = false;
        sub.HostMotionSampleValid = false;
        sub.RelativeLinearVelocity = Vector2.Zero;
        sub.RelativeAngularVelocity = 0f;
    }

    private bool TryGetHostUnder(EntityUid self, MapId mapId, Vector2 worldPos, out EntityUid host)
    {
        host = default;
        var box = Box2.CenteredAround(worldPos, new Vector2(0.9f, 0.9f));
        _gridsBuffer.Clear();
        _mapManager.FindGridsIntersecting(mapId, box, ref _gridsBuffer);

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
