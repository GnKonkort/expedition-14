using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using System.Globalization;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Shared SubGrid collision rules so client prediction matches the server:
/// phase through foreign MapGrid hulls while on a pad, ignore boarding barriers
/// when whitelisted / standing at that SubGrid's stairs, and isolate entities
/// under a SubGrid from entities on it (except projectiles and thrown items).
/// </summary>
public sealed class SharedSubGridCollisionSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedSubGridDebugLogSystem _dbg = default!;
    [Dependency] private readonly TagSystem _tags = default!;

    private const float BoardingPointRange = 0.55f;
    private static readonly TimeSpan PreventLogInterval = TimeSpan.FromMilliseconds(50);
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly ProtoId<TagPrototype> WindowTag = "Window";

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<SubGridComponent> _subQuery;
    private EntityQuery<SubGridPerimeterComponent> _periQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<ThrownItemComponent> _thrownQuery;

    public override void Initialize()
    {
        base.Initialize();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _subQuery = GetEntityQuery<SubGridComponent>();
        _periQuery = GetEntityQuery<SubGridPerimeterComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _thrownQuery = GetEntityQuery<ThrownItemComponent>();

        SubscribeLocalEvent<SubGridComponent, PreventCollideEvent>(OnHullPreventCollide);
        SubscribeLocalEvent<SubGridComponent, AfterAutoHandleStateEvent>(OnSubGridHandleState);
        SubscribeLocalEvent<SubGridPerimeterComponent, PreventCollideEvent>(OnPerimeterPreventCollide);
        SubscribeLocalEvent<MobStateComponent, PreventCollideEvent>(OnMobPreventCollide);
        // Broad isolation: entities under a SubGrid must not push/block entities on it.
        SubscribeLocalEvent<PhysicsComponent, PreventCollideEvent>(OnPhysicsIsolate);
    }

    /// <summary>True if <paramref name="entity"/> may pass this SubGrid's boarding barriers.</summary>
    public bool HasBarrierPass(EntityUid subGrid, EntityUid entity)
    {
        if (_subQuery.TryComp(subGrid, out var sg) && sg.BarrierPassThrough.Contains(entity))
            return true;

        // Soft-pass: approaching via this SubGrid's stairs/dock before whitelist replicates.
        if (!IsAtBoardingPoint(entity, out var hit) || hit == null)
            return false;

        return Transform(hit.Value).GridUid == subGrid;
    }

    private bool IsAtBoardingPoint(EntityUid mob, out EntityUid? accessHit)
    {
        accessHit = null;
        var coords = _transform.GetMoverCoordinates(mob);
        foreach (var ent in _lookup.GetEntitiesInRange(coords, BoardingPointRange))
        {
            if (!HasComp<SubGridAccessComponent>(ent))
                continue;

            accessHit = ent;
            return true;
        }

        return false;
    }

    private void OnSubGridHandleState(EntityUid uid, SubGridComponent comp, ref AfterAutoHandleStateEvent args)
    {
        // Drop stale barrier contacts after whitelist replicates to the client.
        foreach (var mob in comp.BarrierPassThrough)
        {
            if (TryComp(mob, out PhysicsComponent? body))
                _physics.RegenerateContacts((mob, body));
        }
    }

    private void OnHullPreventCollide(EntityUid uid, SubGridComponent comp, ref PreventCollideEvent args)
    {
        if (!_gridQuery.HasComp(args.OtherEntity))
            return;

        args.Cancelled = true;
        _dbg.WriteThrottle(
            "hull.pc:" + uid + ":" + args.OtherEntity,
            PreventLogInterval,
            "hull.prevent",
            string.Format(
                CultureInfo.InvariantCulture,
                "CANCEL SubGrid vs MapGrid us={0} other={1}",
                ToPrettyString(uid),
                ToPrettyString(args.OtherEntity)));
    }

    private void OnPerimeterPreventCollide(EntityUid uid, SubGridPerimeterComponent comp, ref PreventCollideEvent args)
    {
        if (_gridQuery.HasComp(args.OtherEntity))
        {
            args.Cancelled = true;
            LogPreventBarrier(uid, comp, args.OtherEntity, cancelled: true, reason: "vs MapGrid");
            return;
        }

        if (!_mobQuery.HasComp(args.OtherEntity))
        {
            args.Cancelled = true;
            return;
        }

        var owner = ResolveOwner(uid, comp);
        if (owner != default && HasBarrierPass(owner, args.OtherEntity))
        {
            args.Cancelled = true;
            LogPreventBarrier(uid, comp, args.OtherEntity, cancelled: true, reason: "whitelist/soft");
            return;
        }

        LogPreventBarrier(uid, comp, args.OtherEntity, cancelled: false, reason: "block outsider");
    }

    private void OnMobPreventCollide(EntityUid uid, MobStateComponent mob, ref PreventCollideEvent args)
    {
        var xform = Transform(uid);
        var ourGrid = xform.GridUid;
        var onSub = (ourGrid != null && _subQuery.HasComp(ourGrid.Value))
                    || _subQuery.HasComp(xform.ParentUid);

        // Phase through foreign MapGrid hulls while standing on a SubGrid.
        if (onSub && _gridQuery.HasComp(args.OtherEntity) && args.OtherEntity != ourGrid)
        {
            args.Cancelled = true;
            _dbg.WriteThrottle(
                "mob.pc.grid:" + uid + ":" + args.OtherEntity,
                PreventLogInterval,
                "prevent.mob",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "CANCEL mob vs MapGrid mob={0} grid={1} parent={2} other={3}",
                    ToPrettyString(uid),
                    ourGrid,
                    xform.ParentUid,
                    ToPrettyString(args.OtherEntity)));
            return;
        }

        if (!_periQuery.TryComp(args.OtherEntity, out var peri))
            return;

        var owner = ResolveOwner(args.OtherEntity, peri);
        if (owner == default || !HasBarrierPass(owner, uid))
        {
            _dbg.WriteThrottle(
                "prevent.mob:" + uid + ":" + args.OtherEntity + ":False",
                PreventLogInterval,
                "prevent.mob",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ALLOW mob={0} grid={1} parent={2} other={3} peri=True cancelled=False",
                    ToPrettyString(uid),
                    xform.GridUid,
                    xform.ParentUid,
                    ToPrettyString(args.OtherEntity)));
            return;
        }

        args.Cancelled = true;
        _dbg.WriteThrottle(
            "prevent.mob:" + uid + ":" + args.OtherEntity + ":True",
            PreventLogInterval,
            "prevent.mob",
            string.Format(
                CultureInfo.InvariantCulture,
                "CANCEL whitelist mob={0} grid={1} parent={2} other={3} peri=True cancelled=True",
                ToPrettyString(uid),
                xform.GridUid,
                xform.ParentUid,
                ToPrettyString(args.OtherEntity)));
    }

    /// <summary>
    /// Cancel collisions between entities on a SubGrid and entities that are not on that same SubGrid.
    /// Exceptions: projectiles, thrown items, MapGrid hulls, and boarding barriers (handled elsewhere).
    /// </summary>
    private void OnPhysicsIsolate(EntityUid uid, PhysicsComponent body, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (!ShouldIsolate(uid, args.OtherEntity))
            return;

        args.Cancelled = true;
        _dbg.WriteThrottle(
            "iso.pc:" + uid + ":" + args.OtherEntity,
            PreventLogInterval,
            "prevent.isolate",
            string.Format(
                CultureInfo.InvariantCulture,
                "CANCEL isolate a={0} b={1}",
                ToPrettyString(uid),
                ToPrettyString(args.OtherEntity)));
    }

    private bool ShouldIsolate(EntityUid a, EntityUid b)
    {
        // Projectiles / thrown items may hit or land on the SubGrid.
        if (_projectileQuery.HasComp(a) || _projectileQuery.HasComp(b))
            return false;
        if (_thrownQuery.HasComp(a) || _thrownQuery.HasComp(b))
            return false;

        // Hull phase-through stays on the SubGrid MapGrid handler.
        if (_gridQuery.HasComp(a) || _gridQuery.HasComp(b))
            return false;

        // Boarding barriers keep their own pass/block rules.
        if (_periQuery.HasComp(a) || _periQuery.HasComp(b))
            return false;

        // Walls/windows must always be able to stop the pad bumper.
        if (_tags.HasTag(a, WallTag) || _tags.HasTag(a, WindowTag) ||
            _tags.HasTag(b, WallTag) || _tags.HasTag(b, WindowTag))
            return false;

        var ga = GetOccupyingGrid(a);
        var gb = GetOccupyingGrid(b);

        var aOnSub = ga != null && _subQuery.HasComp(ga.Value);
        var bOnSub = gb != null && _subQuery.HasComp(gb.Value);

        if (aOnSub && bOnSub)
            return ga != gb;

        // Exactly one side occupies a SubGrid — isolate from under/foreign entities.
        return aOnSub != bOnSub;
    }

    private EntityUid? GetOccupyingGrid(EntityUid uid)
    {
        var xform = Transform(uid);
        if (xform.GridUid is { } grid)
            return grid;

        // Parent may be the SubGrid MapGrid entity itself during brief reparents.
        if (_subQuery.HasComp(xform.ParentUid) || _gridQuery.HasComp(xform.ParentUid))
            return xform.ParentUid;

        return null;
    }

    private EntityUid ResolveOwner(EntityUid barrier, SubGridPerimeterComponent peri)
    {
        if (peri.OwnerGrid != default)
            return peri.OwnerGrid;

        var xform = Transform(barrier);
        return xform.GridUid ?? xform.ParentUid;
    }

    private void LogPreventBarrier(
        EntityUid barrier,
        SubGridPerimeterComponent comp,
        EntityUid other,
        bool cancelled,
        string reason)
    {
        var ox = Transform(other);
        _dbg.WriteThrottle(
            "prevent.bar:" + barrier + ":" + other + ":" + cancelled,
            PreventLogInterval,
            "prevent.barrier",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} barrier={1} tile={2} owner={3} other={4} otherGrid={5} otherParent={6} cancelled={7} reason={8}",
                cancelled ? "CANCEL" : "ALLOW",
                ToPrettyString(barrier),
                comp.Tile,
                comp.OwnerGrid,
                ToPrettyString(other),
                ox.GridUid,
                ox.ParentUid,
                cancelled,
                reason));
    }
}
