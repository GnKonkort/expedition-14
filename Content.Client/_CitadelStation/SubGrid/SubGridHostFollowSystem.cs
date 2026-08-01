using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared._CitadelStation.SubGrid.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Physics;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client._CitadelStation.SubGrid;

/// <summary>
/// Client host-follow for SubGrid ride smoothness:
/// tick-snap pad to host for prediction, FrameUpdate re-snap to lerped host,
/// and suppress local-player transform lerp while aboard (that was jolting the rider).
/// Host MapGrid is NOT predicted — predicting it fought server lerp and produced
/// alternating frame snaps (~v·dt) on the pad.
/// </summary>
public sealed class SubGridHostFollowSystem : SharedSubGridHostFollowSystem
{
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private EntityUid? _predictedSubGrid;

    public override void Initialize()
    {
        base.Initialize();

        // After host transform lerp so the pad tracks the rendered host pose.
        UpdatesAfter.Add(typeof(TransformSystem));

        SubscribeLocalEvent<SubGridComponent, UpdateIsPredictedEvent>(OnSubGridUpdatePredicted);

        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<MobStateComponent, EntParentChangedMessage>(OnLocalParentChanged);
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent args)
    {
        RefreshSubGridPrediction();
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent args)
    {
        ClearPredictedRide();
    }

    private void OnLocalParentChanged(EntityUid uid, MobStateComponent mob, ref EntParentChangedMessage args)
    {
        if (_players.LocalEntity != uid)
            return;

        RefreshSubGridPrediction();
    }

    public void RefreshSubGridPrediction()
    {
        ClearPredictedRide();

        if (_players.LocalEntity is not { } player)
            return;

        Physics.UpdateIsPredicted(player);

        var xform = Transform(player);
        if (xform.GridUid is not { } grid || !TryComp(grid, out SubGridComponent? sub))
            return;

        _predictedSubGrid = grid;
        Physics.UpdateIsPredicted(grid);
        // Host stays unpredicted: pad snaps to its server-lerped render pose.
    }

    private void ClearPredictedRide()
    {
        if (_predictedSubGrid is { } oldSub)
            Physics.UpdateIsPredicted(oldSub);

        _predictedSubGrid = null;
    }

    private void OnSubGridUpdatePredicted(Entity<SubGridComponent> ent, ref UpdateIsPredictedEvent args)
    {
        if (_players.LocalEntity is not { } player)
            return;

        var xform = Transform(player);
        if (xform.GridUid == ent.Owner || xform.ParentUid == ent.Owner)
            args.IsPredicted = true;
    }

    protected override void OnAttachmentSnapped(EntityUid uid, TransformComponent xform)
    {
        xform.ActivelyLerping = false;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_timing.ApplyingState)
            return;

        EntityUid? aboardGrid = null;
        if (_players.LocalEntity is { } player)
        {
            var px = Transform(player);
            if (px.GridUid is { } grid && HasComp<SubGridComponent>(grid))
            {
                aboardGrid = grid;
                if (_predictedSubGrid != grid)
                    RefreshSubGridPrediction();
            }
        }

        // Pad pose from post-lerp host (small delta over the tick snap).
        var query = EntityQueryEnumerator<SubGridComponent>();
        while (query.MoveNext(out var uid, out var sub))
        {
            if (!sub.HostPoseValid || sub.HostGrid == null)
                continue;

            TrySnapToHost(uid, sub);
            // Network / prediction may re-arm lerp between frames — keep it off after snap.
            var padXform = Transform(uid);
            if (padXform.ActivelyLerping)
                padXform.ActivelyLerping = false;
        }

        // Rider transform lerp is what still jolts the camera after the pad itself looks stable.
        if (aboardGrid != null &&
            _players.LocalEntity is { } local &&
            TryComp(aboardGrid.Value, out SubGridComponent? ride) &&
            ride.HostPoseValid)
        {
            SuppressRiderLerp(local);
        }
    }

    private void SuppressRiderLerp(EntityUid rider)
    {
        var xform = Transform(rider);
        if (xform.ActivelyLerping)
            xform.ActivelyLerping = false;

        // Re-assert local pose without the client SetLocalPosition→ActivateLerp path.
        TransformSystem.SetLocalPositionNoLerp(rider, xform.LocalPosition, xform);
        TransformSystem.SetLocalRotationNoLerp(rider, xform.LocalRotation, xform);
        xform.ActivelyLerping = false;

        // Relative-move visual lerp on InputMover also wobbles the view on a moving pad.
        if (TryComp(rider, out InputMoverComponent? mover))
            mover.RelativeRotation = mover.TargetRelativeRotation;
    }
}
