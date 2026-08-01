using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;
using System.Numerics;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Shared SubGrid collision rules so client prediction matches the server:
/// phase through foreign MapGrid hulls while on a pad, ignore boarding barriers
/// while GridUid/parent is that SubGrid (or standing at its stairs), and isolate
/// entities under a SubGrid from entities on it (except projectiles and thrown items).
/// </summary>
public sealed class SharedSubGridCollisionSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tags = default!;

    /// <summary>
    /// Slightly over half-tile so boarding still works while the mother grid is moving
    /// (mob / SubGrid samples can be a frame apart).
    /// </summary>
    public const float BoardingPointRange = 1.1f;
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
        SubscribeLocalEvent<SubGridPerimeterComponent, PreventCollideEvent>(OnPerimeterPreventCollide);
        SubscribeLocalEvent<MobStateComponent, PreventCollideEvent>(OnMobPreventCollide);
        // Broad isolation: entities under a SubGrid must not push/block entities on it.
        SubscribeLocalEvent<PhysicsComponent, PreventCollideEvent>(OnPhysicsIsolate);
    }

    /// <summary>
    /// True if <paramref name="entity"/> may pass this SubGrid's boarding barriers:
    /// standing on / parented to the SubGrid, or near one of its registered boarding tiles.
    /// </summary>
    public bool HasBarrierPass(EntityUid subGrid, EntityUid entity)
    {
        var xform = Transform(entity);
        if (xform.GridUid == subGrid || xform.ParentUid == subGrid)
            return true;

        // Soft-pass from BoardingTiles (SubGrid transform), not stair entity pose.
        return IsNearBoardingTile(entity, subGrid, out _, out _);
    }

    /// <summary>
    /// Near a SubGrid boarding tile center computed from the SubGrid world matrix.
    /// Independent of whether the stair entity has drifted onto the host.
    /// </summary>
    public bool IsNearBoardingTile(
        EntityUid mob,
        EntityUid? onlySubGrid,
        out EntityUid? subGridHit,
        out Vector2i tile,
        float range = BoardingPointRange)
    {
        subGridHit = null;
        tile = default;

        var mobXform = Transform(mob);
        if (mobXform.MapID == MapId.Nullspace)
            return false;

        var mobPos = _transform.GetWorldPosition(mobXform);
        var rangeSq = range * range;
        var query = EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var gridUid, out var sub, out var grid, out var xform))
        {
            if (onlySubGrid != null && gridUid != onlySubGrid.Value)
                continue;

            if (xform.MapID != mobXform.MapID || sub.BoardingTiles.Count == 0)
                continue;

            var worldMatrix = _transform.GetWorldMatrix(xform);
            var tileSize = grid.TileSize;
            foreach (var indices in sub.BoardingTiles)
            {
                var local = new Vector2((indices.X + 0.5f) * tileSize, (indices.Y + 0.5f) * tileSize);
                var world = Vector2.Transform(local, worldMatrix);
                if ((world - mobPos).LengthSquared() > rangeSq)
                    continue;

                subGridHit = gridUid;
                tile = indices;
                return true;
            }
        }

        return false;
    }

    private void OnHullPreventCollide(EntityUid uid, SubGridComponent comp, ref PreventCollideEvent args)
    {
        if (!_gridQuery.HasComp(args.OtherEntity))
            return;

        args.Cancelled = true;
    }

    private void OnPerimeterPreventCollide(EntityUid uid, SubGridPerimeterComponent comp, ref PreventCollideEvent args)
    {
        if (_gridQuery.HasComp(args.OtherEntity))
        {
            args.Cancelled = true;
            return;
        }

        if (!_mobQuery.HasComp(args.OtherEntity))
        {
            args.Cancelled = true;
            return;
        }

        var owner = ResolveOwner(uid, comp);
        if (owner != default && HasBarrierPass(owner, args.OtherEntity))
            args.Cancelled = true;
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
            return;
        }

        if (!_periQuery.TryComp(args.OtherEntity, out var peri))
            return;

        var owner = ResolveOwner(args.OtherEntity, peri);
        if (owner == default || !HasBarrierPass(owner, uid))
            return;

        args.Cancelled = true;
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
}
