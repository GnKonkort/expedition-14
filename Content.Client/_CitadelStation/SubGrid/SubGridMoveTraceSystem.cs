using Content.Shared.Mobs.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared._CitadelStation.SubGrid.Systems;
using Robust.Client.Player;
using Robust.Client.Timing;
using Robust.Shared.Physics.Events;
using System.Globalization;

namespace Content.Client._CitadelStation.SubGrid;

/// <summary>Predicted SubGrid move/collide trace for the local player.</summary>
public sealed class SubGridMoveTraceSystem : SharedSubGridMoveTraceSystem
{
    [Dependency] private readonly SubGridDebugLog _dbg = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IClientGameTiming _clientTiming = default!;

    private EntityUid? _local;

    protected override SharedSubGridDebugLogSystem Dbg => _dbg;

    public override void Initialize()
    {
        base.Initialize();

        // PreventCollide is owned by SharedSubGridCollisionSystem on both sides.
        // Client only logs Start/End collide for the local player.
        SubscribeLocalEvent<SubGridPerimeterComponent, StartCollideEvent>(OnBarrierStart);
        SubscribeLocalEvent<SubGridPerimeterComponent, EndCollideEvent>(OnBarrierEnd);
        SubscribeLocalEvent<SubGridComponent, StartCollideEvent>(OnHullStart);
        SubscribeLocalEvent<MobStateComponent, StartCollideEvent>(OnMobStart);
    }

    protected override void CollectTraceTargets(List<EntityUid> into)
    {
        _local = _players.LocalEntity;
        if (_local is { } uid && Exists(uid))
            into.Add(uid);
    }

    protected override string AppendTimingExtras()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            " lastReal={0} lastProc={1} cur={2}",
            _clientTiming.LastRealTick.Value,
            _clientTiming.LastProcessedTick.Value,
            _clientTiming.CurTick.Value);
    }

    protected override bool ShouldLogOther(EntityUid other)
    {
        return _local != null && (other == _local || other == _players.LocalEntity);
    }

    protected override bool ShouldLogMob(EntityUid mob)
    {
        return _local != null && mob == _local;
    }
}
