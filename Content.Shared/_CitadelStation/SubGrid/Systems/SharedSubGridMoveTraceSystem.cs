using System.Globalization;
using System.Numerics;
using Content.Shared.Mobs.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Logs exact passenger transforms / velocities while on a SubGrid, plus barrier &amp; hull collisions.
/// Server traces attached players; client traces the local player (predicted + reconciled).
/// </summary>
public abstract class SharedSubGridMoveTraceSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<SubGridComponent> _subQuery;
    private EntityQuery<SubGridPerimeterComponent> _periQuery;

    /// <summary>Sample interval for continuous move lines (collisions always log).</summary>
    protected virtual TimeSpan MoveSampleInterval => TimeSpan.FromMilliseconds(33);

    protected abstract SharedSubGridDebugLogSystem Dbg { get; }

    /// <summary>Fill with entities that should be position-traced this tick.</summary>
    protected abstract void CollectTraceTargets(List<EntityUid> into);

    public override void Initialize()
    {
        base.Initialize();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _subQuery = GetEntityQuery<SubGridComponent>();
        _periQuery = GetEntityQuery<SubGridPerimeterComponent>();

        // Collision subscriptions are side-specific: server already owns most
        // (SubGridSystem / MovementSystem). Client registers them in SubGridMoveTraceSystem.
        Log.Info("SubGridMoveTrace initialized -> {Path}", Dbg.Path);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Client: only log the first predicted pass (skip input-replay duplicates).
        if (_timing.IsFirstTimePredicted == false && _timing.InPrediction)
            return;

        var targets = new List<EntityUid>(4);
        CollectTraceTargets(targets);
        foreach (var uid in targets)
            TraceEntity(uid);
    }

    private void TraceEntity(EntityUid uid)
    {
        if (!Exists(uid) || !TryComp(uid, out TransformComponent? xform))
            return;

        var gridUid = xform.GridUid;
        var onSub = gridUid != null && _subQuery.HasComp(gridUid.Value);
        var parentIsSub = _subQuery.HasComp(xform.ParentUid);
        if (!onSub && !parentIsSub)
            return;

        var world = _transform.GetWorldPosition(xform);
        var local = Vector2.Zero;
        var tileStr = "n/a";
        if (gridUid != null && _gridQuery.TryComp(gridUid.Value, out var grid))
        {
            local = Vector2.Transform(world, _transform.GetInvWorldMatrix(Transform(gridUid.Value)));
            tileStr = _map.WorldToTile(gridUid.Value, grid, world).ToString();
        }

        var vel = Vector2.Zero;
        var ang = 0f;
        if (_physicsQuery.TryComp(uid, out var body))
        {
            vel = body.LinearVelocity;
            ang = body.AngularVelocity;
        }

        var gridStr = gridUid == null ? "null" : ToPrettyString(gridUid.Value).ToString();
        // string.Format only — client sandbox blocks StringBuilder.Append(IFormatProvider, …).
        var msg = string.Format(
            CultureInfo.InvariantCulture,
            "ent={0} world=({1:F3},{2:F3}) local=({3:F3},{4:F3}) tile={5} grid={6} parent={7} vel=({8:F3},{9:F3}) speed={10:F3} ang={11:F3}{12}",
            ToPrettyString(uid),
            world.X,
            world.Y,
            local.X,
            local.Y,
            tileStr,
            gridStr,
            ToPrettyString(xform.ParentUid),
            vel.X,
            vel.Y,
            vel.Length(),
            ang,
            AppendTimingExtras());

        Dbg.WriteThrottle("move:" + uid, MoveSampleInterval, "move", msg);
    }

    /// <summary>Optional client-only timing fields (LastRealTick etc.).</summary>
    protected virtual string AppendTimingExtras()
    {
        return string.Format(CultureInfo.InvariantCulture, " cur={0}", _timing.CurTick.Value);
    }

    protected void OnBarrierStart(EntityUid uid, SubGridPerimeterComponent comp, ref StartCollideEvent args)
    {
        if (!ShouldLogOther(args.OtherEntity))
            return;

        var ox = Transform(args.OtherEntity);
        Dbg.Write("collide+",
            string.Format(
                CultureInfo.InvariantCulture,
                "barrier={0} tile={1} owner={2} other={3} otherGrid={4} otherParent={5} ourFix={6} otherFix={7}",
                ToPrettyString(uid),
                comp.Tile,
                comp.OwnerGrid,
                ToPrettyString(args.OtherEntity),
                ox.GridUid,
                ox.ParentUid,
                args.OurFixtureId,
                args.OtherFixtureId));
    }

    protected void OnBarrierEnd(EntityUid uid, SubGridPerimeterComponent comp, ref EndCollideEvent args)
    {
        if (!ShouldLogOther(args.OtherEntity))
            return;

        Dbg.WriteThrottle(
            "collide-:" + uid + ":" + args.OtherEntity,
            TimeSpan.FromMilliseconds(100),
            "collide-",
            string.Format(
                CultureInfo.InvariantCulture,
                "barrier={0} tile={1} other={2}",
                ToPrettyString(uid),
                comp.Tile,
                ToPrettyString(args.OtherEntity)));
    }

    protected void OnBarrierPrevent(EntityUid uid, SubGridPerimeterComponent comp, ref PreventCollideEvent args)
    {
        if (!ShouldLogOther(args.OtherEntity))
            return;

        var ox = Transform(args.OtherEntity);
        Dbg.WriteThrottle(
            "prevent.bar:" + uid + ":" + args.OtherEntity + ":" + args.Cancelled,
            TimeSpan.FromMilliseconds(50),
            "prevent.barrier",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} barrier={1} tile={2} owner={3} other={4} otherGrid={5} otherParent={6} cancelled={7}",
                args.Cancelled ? "CANCEL" : "ALLOW",
                ToPrettyString(uid),
                comp.Tile,
                comp.OwnerGrid,
                ToPrettyString(args.OtherEntity),
                ox.GridUid,
                ox.ParentUid,
                args.Cancelled));
    }

    protected void OnHullStart(EntityUid uid, SubGridComponent comp, ref StartCollideEvent args)
    {
        if (!ShouldLogOther(args.OtherEntity))
            return;

        var ox = Transform(args.OtherEntity);
        Dbg.Write("collide.hull+",
            string.Format(
                CultureInfo.InvariantCulture,
                "hull={0} other={1} otherGrid={2} ourFix={3} otherFix={4}",
                ToPrettyString(uid),
                ToPrettyString(args.OtherEntity),
                ox.GridUid,
                args.OurFixtureId,
                args.OtherFixtureId));
    }

    protected void OnHullPrevent(EntityUid uid, SubGridComponent comp, ref PreventCollideEvent args)
    {
        if (!ShouldLogOther(args.OtherEntity))
            return;

        Dbg.WriteThrottle(
            "prevent.hull:" + uid + ":" + args.OtherEntity + ":" + args.Cancelled,
            TimeSpan.FromMilliseconds(50),
            "prevent.hull",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} hull={1} other={2} cancelled={3}",
                args.Cancelled ? "CANCEL" : "ALLOW",
                ToPrettyString(uid),
                ToPrettyString(args.OtherEntity),
                args.Cancelled));
    }

    protected void OnMobStart(EntityUid uid, MobStateComponent mob, ref StartCollideEvent args)
    {
        if (!ShouldLogMob(uid))
            return;

        var other = args.OtherEntity;
        if (!_periQuery.HasComp(other) && !_subQuery.HasComp(other) && !_gridQuery.HasComp(other))
            return;

        var xform = Transform(uid);
        Dbg.Write("collide.mob+",
            string.Format(
                CultureInfo.InvariantCulture,
                "mob={0} grid={1} parent={2} other={3} peri={4} sub={5} mapGrid={6} ourFix={7} otherFix={8}",
                ToPrettyString(uid),
                xform.GridUid,
                xform.ParentUid,
                ToPrettyString(other),
                _periQuery.HasComp(other),
                _subQuery.HasComp(other),
                _gridQuery.HasComp(other),
                args.OurFixtureId,
                args.OtherFixtureId));
    }

    protected void OnMobPrevent(EntityUid uid, MobStateComponent mob, ref PreventCollideEvent args)
    {
        if (!ShouldLogMob(uid))
            return;

        var other = args.OtherEntity;
        if (!_periQuery.HasComp(other) && !_subQuery.HasComp(other) && !_gridQuery.HasComp(other))
            return;

        var xform = Transform(uid);
        Dbg.WriteThrottle(
            "prevent.mob:" + uid + ":" + other + ":" + args.Cancelled,
            TimeSpan.FromMilliseconds(50),
            "prevent.mob",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} mob={1} grid={2} parent={3} other={4} peri={5} cancelled={6}",
                args.Cancelled ? "CANCEL" : "ALLOW",
                ToPrettyString(uid),
                xform.GridUid,
                xform.ParentUid,
                ToPrettyString(other),
                _periQuery.HasComp(other),
                args.Cancelled));
    }

    /// <summary>Whether this other entity is interesting for collide logs (override to filter).</summary>
    protected virtual bool ShouldLogOther(EntityUid other) => true;

    /// <summary>Whether this mob's collisions should be logged.</summary>
    protected virtual bool ShouldLogMob(EntityUid mob) => true;
}
