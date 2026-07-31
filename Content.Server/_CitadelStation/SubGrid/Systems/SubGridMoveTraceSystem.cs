using Content.Shared.Mobs.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared._CitadelStation.SubGrid.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Authoritative SubGrid move trace for players.
/// Collide/Prevent for barriers+hull already subscribed by SubGridSystem/MovementSystem —
/// here we only add free MobState StartCollide (player vs barrier/hull/grid).
/// </summary>
public sealed class SubGridMoveTraceSystem : SharedSubGridMoveTraceSystem
{
    [Dependency] private readonly SubGridDebugLog _dbg = default!;

    protected override SharedSubGridDebugLogSystem Dbg => _dbg;

    public override void Initialize()
    {
        base.Initialize();
        // Only subscription not already taken on server.
        SubscribeLocalEvent<MobStateComponent, StartCollideEvent>(OnMobStart);
    }

    protected override void CollectTraceTargets(List<EntityUid> into)
    {
        var players = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (players.MoveNext(out var uid, out _, out _, out var xform))
        {
            var grid = xform.GridUid;
            if (grid != null && HasComp<SubGridComponent>(grid.Value))
                into.Add(uid);
            else if (HasComp<SubGridComponent>(xform.ParentUid))
                into.Add(uid);
        }
    }

    protected override bool ShouldLogOther(EntityUid other)
    {
        return HasComp<ActorComponent>(other) || HasComp<MobStateComponent>(other) || HasComp<MapGridComponent>(other);
    }

    protected override bool ShouldLogMob(EntityUid mob)
    {
        return HasComp<ActorComponent>(mob);
    }
}
