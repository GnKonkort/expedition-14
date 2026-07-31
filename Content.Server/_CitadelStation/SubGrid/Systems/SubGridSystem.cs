using System.Numerics;
using Content.Shared.Physics;
using Content.Shared._CitadelStation.SubGrid;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Tiles;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Peer sub-grid hull behaviour: ignore MapGrid↔MapGrid tile collision (planet-style phase),
/// maintain wall bumpers, and keep per-tile boarding barriers in sync with the floor.
/// </summary>
public sealed class SubGridSystem : EntitySystem
{
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SubGridGravitySystem _gravity = default!;
    [Dependency] private readonly SubGridAtmosSystem _atmos = default!;
    [Dependency] private readonly SubGridDebugLog _dbg = default!;

    private static readonly TimeSpan CollideLogInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Half-extents of the per-tile boarding barrier (just under a full tile).</summary>
    private static readonly Vector2 BarrierHalf = new(0.48f, 0.48f);

    /// <summary>
    /// Boarding barrier layers: block mobs (Mid/High/Low) but do NOT include
    /// <see cref="CollisionGroup.Impassable"/> / InteractImpassable, so
    /// InRangeUnobstructed rays can reach entities aboard past the barrier
    /// (same idea as SpecialWallLayer).
    /// </summary>
    private static readonly int BarrierLayer = (int) CollisionGroup.SpecialWallLayer;
    private static readonly int BarrierMask = (int) (CollisionGroup.MobMask | CollisionGroup.SmallMobMask
        | CollisionGroup.MobLayer);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SubGridComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<SubGridComponent, ComponentShutdown>(OnShutdown);
        // PreventCollide (hull / barriers / mob) lives in SharedSubGridCollisionSystem.
        SubscribeLocalEvent<SubGridComponent, GridFixtureChangeEvent>(OnFixturesChanged);
        SubscribeLocalEvent<SubGridComponent, FloorTileAttemptEvent>(OnFloorTileAttempt);
        SubscribeLocalEvent<SubGridComponent, TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<SubGridPerimeterComponent, StartCollideEvent>(OnPerimeterStartCollide);
        SubscribeLocalEvent<SubGridPerimeterComponent, EndCollideEvent>(OnPerimeterEndCollide);
        SubscribeLocalEvent<SubGridAccessComponent, ComponentStartup>(OnAccessStartup);
        SubscribeLocalEvent<SubGridAccessComponent, ComponentShutdown>(OnAccessShutdown);
        Log.Info("SubGridSystem initialized (collision debug -> {Path})", _dbg.Path);
    }

    private void OnPerimeterStartCollide(EntityUid uid, SubGridPerimeterComponent comp, ref StartCollideEvent args)
    {
        var xOther = Transform(args.OtherEntity);
        _dbg.Write("barrier.collide+",
            $"barrier={ToPrettyString(uid)} tile={comp.Tile} owner={comp.OwnerGrid} other={ToPrettyString(args.OtherEntity)} otherGrid={xOther.GridUid} otherParent={xOther.ParentUid} ourFix={args.OurFixtureId} otherFix={args.OtherFixtureId}");
    }

    private void OnPerimeterEndCollide(EntityUid uid, SubGridPerimeterComponent comp, ref EndCollideEvent args)
    {
        _dbg.WriteThrottle(
            $"barrier.collide-:{uid}:{args.OtherEntity}",
            CollideLogInterval,
            "barrier.collide-",
            $"barrier={ToPrettyString(uid)} tile={comp.Tile} other={ToPrettyString(args.OtherEntity)}");
    }

    private void OnStartup(Entity<SubGridComponent> ent, ref ComponentStartup args)
    {
        Log.Info("SubGrid startup: {Grid} maxTiles={Max} mode={Mode} drive={Drive}",
            ToPrettyString(ent), ent.Comp.MaxTiles, ent.Comp.Mode, ent.Comp.DriveEnabled);
        RebuildBumper(ent);
        SyncAllPerimeters(ent);
        // Only one ComponentStartup subscriber allowed per component — relay here.
        _gravity.OnSubGridStartup(ent);
        _atmos.OnSubGridStartup(ent);
    }

    private void OnShutdown(Entity<SubGridComponent> ent, ref ComponentShutdown args)
    {
        Log.Info("SubGrid shutdown: {Grid}", ToPrettyString(ent));
        if (TryComp<FixturesComponent>(ent, out var fixtures))
        {
            if (fixtures.Fixtures.ContainsKey(SubGridComponent.BumperFixtureId))
            {
                _fixtures.DestroyFixture(ent, SubGridComponent.BumperFixtureId, false, manager: fixtures);
                Log.Debug("SubGrid removed bumper fixture on {Grid}", ToPrettyString(ent));
            }

            ClearDeckFixtures(ent, fixtures);
        }

        ClearAllPerimeters(ent);
        ent.Comp.BarrierPassThrough.Clear();
        Dirty(ent);
    }

    private void OnFixturesChanged(Entity<SubGridComponent> ent, ref GridFixtureChangeEvent args)
    {
        Log.Debug("SubGrid fixtures changed, rebuilding bumper: {Grid} newCount={Count}",
            ToPrettyString(ent), args.NewFixtures.Count);
        RebuildBumper(ent);
    }

    private void OnTileChanged(Entity<SubGridComponent> ent, ref TileChangedEvent args)
    {
        if (!TryComp(ent, out MapGridComponent? grid))
            return;

        foreach (var change in args.Changes)
        {
            var tile = change.GridIndices;
            var hadFloor = !change.OldTile.IsEmpty;
            var hasFloor = !change.NewTile.IsEmpty;

            if (!hadFloor && hasFloor)
            {
                EnsurePerimeterAt(ent, grid, tile);
                continue;
            }

            if (hadFloor && !hasFloor)
            {
                RemovePerimeterAt(ent, tile);
                continue;
            }

            if (hasFloor)
                EnsurePerimeterAt(ent, grid, tile);
        }
    }

    private void OnAccessStartup(EntityUid uid, SubGridAccessComponent comp, ref ComponentStartup args)
    {
        RefreshAccessTile(uid);
    }

    private void OnAccessShutdown(EntityUid uid, SubGridAccessComponent comp, ref ComponentShutdown args)
    {
        RefreshAccessTile(uid);
    }

    private void RefreshAccessTile(EntityUid accessEnt)
    {
        var xform = Transform(accessEnt);
        if (xform.GridUid is not { } gridUid || !HasComp<SubGridComponent>(gridUid))
            return;
        if (!TryComp(gridUid, out MapGridComponent? grid))
            return;

        var tile = _map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(xform));
        EnsurePerimeterAt(gridUid, grid, tile);
    }

    private void OnFloorTileAttempt(Entity<SubGridComponent> ent, ref FloorTileAttemptEvent args)
    {
        if (!TryComp(ent, out MapGridComponent? grid))
        {
            Log.Warning("SubGrid FloorTileAttempt without MapGridComponent: {Grid}", ToPrettyString(ent));
            return;
        }

        var approx = (int) MathF.Ceiling(MathF.Max(0f, grid.LocalAABB.Width) * MathF.Max(0f, grid.LocalAABB.Height));
        if (approx >= ent.Comp.MaxTiles)
        {
            Log.Warning("SubGrid tile place cancelled (max tiles): {Grid} approx={Approx} max={Max} idx={Idx}",
                ToPrettyString(ent), approx, ent.Comp.MaxTiles, args.GridIndices);
            args.Cancelled = true;
        }
        else
        {
            Log.Debug("SubGrid tile place allowed: {Grid} approx={Approx}/{Max} idx={Idx}",
                ToPrettyString(ent), approx, ent.Comp.MaxTiles, args.GridIndices);
        }
    }

    private void ClearDeckFixtures(EntityUid uid, FixturesComponent fixtures)
    {
        var toRemove = new List<string>();
        foreach (var id in fixtures.Fixtures.Keys)
        {
            if (id.StartsWith(SubGridComponent.DeckFixturePrefix, StringComparison.Ordinal))
                toRemove.Add(id);
        }

        foreach (var id in toRemove)
            _fixtures.DestroyFixture(uid, id, false, manager: fixtures);
    }

    /// <summary>
    /// Rebuilds per-floor-tile hard hull fixtures (matches real pad footprint, not LocalAABB fill).
    /// Boarding barriers are synced separately.
    /// </summary>
    public void RebuildBumper(Entity<SubGridComponent> ent)
    {
        if (!TryComp(ent, out MapGridComponent? grid) ||
            !TryComp(ent, out FixturesComponent? fixtures) ||
            !TryComp(ent, out PhysicsComponent? body))
        {
            Log.Warning("SubGrid RebuildBumper missing comps: {Grid} grid={HasGrid} fixtures={HasFix} body={HasBody}",
                ToPrettyString(ent),
                HasComp<MapGridComponent>(ent),
                HasComp<FixturesComponent>(ent),
                HasComp<PhysicsComponent>(ent));
            return;
        }

        if (fixtures.Fixtures.ContainsKey(SubGridComponent.BumperFixtureId))
            _fixtures.DestroyFixture(ent, SubGridComponent.BumperFixtureId, false, manager: fixtures);

        ClearDeckFixtures(ent, fixtures);

        // Hull hits walls/windows but not mobs (MobLayer does not intersect FullTileMask).
        // Impassable on the layer so WallLayer contacts actually generate.
        var layer = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable
            | CollisionGroup.HighImpassable | CollisionGroup.LowImpassable);
        var mask = (int) CollisionGroup.FullTileMask;

        var tileSize = grid.TileSize;
        // Slight inset (BumperEnlarge default negative) so hard contacts allow visual flush with walls.
        var half = MathF.Max(0.35f, tileSize * 0.5f + ent.Comp.BumperEnlarge);
        var created = 0;
        Span<Vector2> verts = stackalloc Vector2[4];

        var tileEnum = _map.GetAllTilesEnumerator(ent, grid);
        while (tileEnum.MoveNext(out var tileRefNullable))
        {
            if (tileRefNullable is not { } tileRef || tileRef.Tile.IsEmpty)
                continue;

            var indices = tileRef.GridIndices;
            var cx = (indices.X + 0.5f) * tileSize;
            var cy = (indices.Y + 0.5f) * tileSize;
            verts[0] = new Vector2(cx - half, cy - half);
            verts[1] = new Vector2(cx + half, cy - half);
            verts[2] = new Vector2(cx + half, cy + half);
            verts[3] = new Vector2(cx - half, cy + half);

            var shape = new PolygonShape();
            shape.Set(verts, 4);

            var id = $"{SubGridComponent.DeckFixturePrefix}{indices.X}_{indices.Y}";
            if (_fixtures.TryCreateFixture(
                    ent,
                    shape,
                    id,
                    density: 25f,
                    hard: true,
                    collisionLayer: layer,
                    collisionMask: mask,
                    manager: fixtures,
                    body: body))
            {
                created++;
            }
        }

        var usedFallback = false;
        if (created == 0)
        {
            usedFallback = true;
            var aabb = new Box2(0f, 0f, 3f, 3f);
            verts[0] = aabb.BottomLeft;
            verts[1] = aabb.BottomRight;
            verts[2] = aabb.TopRight;
            verts[3] = aabb.TopLeft;
            var bumperShape = new PolygonShape();
            bumperShape.Set(verts, 4);
            _fixtures.TryCreateFixture(
                ent,
                bumperShape,
                SubGridComponent.BumperFixtureId,
                density: 25f,
                hard: true,
                collisionLayer: layer,
                collisionMask: mask,
                manager: fixtures,
                body: body);
        }

        _physics.SetCanCollide(ent, true, manager: fixtures, body: body);
        _physics.WakeBody(ent, body: body);
        _physics.RegenerateContacts((ent.Owner, body));
        Log.Debug(
            "SubGrid bumper rebuild: {Grid} tiles={Tiles} fallback={Fallback}",
            ToPrettyString(ent), created, usedFallback);
    }

    /// <summary>
    /// Full resync: one boarding barrier per floor tile (disabled on stairs/dock).
    /// </summary>
    public void SyncAllPerimeters(EntityUid gridUid)
    {
        if (!TryComp(gridUid, out MapGridComponent? grid))
            return;

        var floors = new HashSet<Vector2i>();
        var tileEnum = _map.GetAllTilesEnumerator(gridUid, grid);
        while (tileEnum.MoveNext(out var tileRefNullable))
        {
            if (tileRefNullable is not { } tileRef || tileRef.Tile.IsEmpty)
                continue;
            floors.Add(tileRef.GridIndices);
        }

        // Remove barriers whose tile no longer exists.
        var existing = new List<(EntityUid Uid, Vector2i Tile)>();
        var query = EntityQueryEnumerator<SubGridPerimeterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var peri, out var xform))
        {
            if (xform.GridUid != gridUid && xform.ParentUid != gridUid)
                continue;
            existing.Add((uid, peri.Tile));
        }

        foreach (var (uid, tile) in existing)
        {
            if (!floors.Contains(tile))
                QueueDel(uid);
        }

        foreach (var tile in floors)
            EnsurePerimeterAt(gridUid, grid, tile);

        Log.Debug("SubGrid perimeter sync: {Grid} floors={Floors}", ToPrettyString(gridUid), floors.Count);
    }

    private void ClearAllPerimeters(EntityUid gridUid)
    {
        var toDelete = new List<EntityUid>();
        var query = EntityQueryEnumerator<SubGridPerimeterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid || xform.ParentUid == gridUid)
                toDelete.Add(uid);
        }

        foreach (var uid in toDelete)
            QueueDel(uid);
    }

    private void RemovePerimeterAt(EntityUid gridUid, Vector2i tile)
    {
        var toDelete = new List<EntityUid>();
        var query = EntityQueryEnumerator<SubGridPerimeterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var peri, out var xform))
        {
            if (peri.Tile != tile)
                continue;
            if (xform.GridUid != gridUid && xform.ParentUid != gridUid)
                continue;
            toDelete.Add(uid);
        }

        foreach (var uid in toDelete)
            QueueDel(uid);
    }

    /// <summary>
    /// Ensure a Kinematic boarding barrier exists on this floor tile and matches stairs state.
    /// </summary>
    private void EnsurePerimeterAt(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        EntityUid? existing = null;
        var query = EntityQueryEnumerator<SubGridPerimeterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var peri, out var xform))
        {
            if (peri.Tile != tile)
                continue;
            if (peri.OwnerGrid != gridUid && xform.GridUid != gridUid && xform.ParentUid != gridUid)
                continue;
            existing = uid;
            break;
        }

        var tileSize = grid.TileSize;
        var local = new Vector2((tile.X + 0.5f) * tileSize, (tile.Y + 0.5f) * tileSize);
        var boardingOpen = TileHasAccess(gridUid, grid, tile);

        if (existing == null)
        {
            var ent = Spawn(SubGridComponent.PerimeterPrototypeId, new EntityCoordinates(gridUid, local));
            _transform.Unanchor(ent);
            _transform.SetCoordinates(ent, new EntityCoordinates(gridUid, local));

            var peri = EnsureComp<SubGridPerimeterComponent>(ent);
            peri.Tile = tile;
            peri.OwnerGrid = gridUid;
            Dirty(ent, peri);

            if (!TryComp(ent, out FixturesComponent? fixMan) || !TryComp(ent, out PhysicsComponent? pBody))
            {
                QueueDel(ent);
                return;
            }

            _physics.SetBodyType(ent, BodyType.Kinematic, body: pBody);
            _physics.SetFixedRotation(ent, true, body: pBody);

            Span<Vector2> verts = stackalloc Vector2[4];
            verts[0] = new Vector2(-BarrierHalf.X, -BarrierHalf.Y);
            verts[1] = new Vector2(BarrierHalf.X, -BarrierHalf.Y);
            verts[2] = new Vector2(BarrierHalf.X, BarrierHalf.Y);
            verts[3] = new Vector2(-BarrierHalf.X, BarrierHalf.Y);
            var shape = new PolygonShape();
            shape.Set(verts, 4);

            _fixtures.TryCreateFixture(
                ent,
                shape,
                "fix1",
                density: 1f,
                hard: true,
                collisionLayer: BarrierLayer,
                collisionMask: BarrierMask,
                manager: fixMan,
                body: pBody);

            // Stairs/dock tile: barrier present but disabled.
            _physics.SetCanCollide(ent, !boardingOpen, manager: fixMan, body: pBody);
            Log.Debug("SubGrid barrier spawn: {Grid} tile={Tile} open={Open}",
                ToPrettyString(gridUid), tile, boardingOpen);
            return;
        }

        var barrier = existing.Value;
        _transform.Unanchor(barrier);
        _transform.SetCoordinates(barrier, new EntityCoordinates(gridUid, local));

        if (TryComp(barrier, out SubGridPerimeterComponent? existingPeri))
        {
            existingPeri.Tile = tile;
            existingPeri.OwnerGrid = gridUid;
            Dirty(barrier, existingPeri);
        }

        if (TryComp(barrier, out FixturesComponent? existingFix) &&
            TryComp(barrier, out PhysicsComponent? existingBody))
        {
            _physics.SetBodyType(barrier, BodyType.Kinematic, body: existingBody);
            // Keep interaction-friendly layers if an older barrier still has Impassable.
            if (existingFix.Fixtures.TryGetValue("fix1", out var fix))
            {
                _physics.SetCollisionLayer(barrier, "fix1", fix, BarrierLayer, existingFix, existingBody);
                _physics.SetCollisionMask(barrier, "fix1", fix, BarrierMask, existingFix, existingBody);
            }

            _physics.SetCanCollide(barrier, !boardingOpen, manager: existingFix, body: existingBody);
        }
    }

    private bool TileHasAccess(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        foreach (var uid in _map.GetAnchoredEntities(gridUid, grid, indices))
        {
            if (HasComp<SubGridAccessComponent>(uid))
                return true;
        }

        // Unanchored stairs / dock markers still parented to this SubGrid.
        var query = EntityQueryEnumerator<SubGridAccessComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid != gridUid && xform.ParentUid != gridUid)
                continue;
            if (_map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(xform)) == indices)
                return true;
        }

        return false;
    }
}
