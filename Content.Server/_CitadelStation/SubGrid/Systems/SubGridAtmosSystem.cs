using System.Numerics;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Maps;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Proxies parent-grid atmosphere onto exposed SubGrid tiles.
/// Sealed airtight interiors keep a normal simulated GridAtmosphere.
/// </summary>
public sealed class SubGridAtmosSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(0.25);
    private static readonly AtmosDirection[] Cardinals =
    {
        AtmosDirection.North,
        AtmosDirection.South,
        AtmosDirection.East,
        AtmosDirection.West,
    };

    private TimeSpan _nextSync;
    private readonly List<Entity<MapGridComponent>> _overlapGridsBuffer = new();
    private readonly HashSet<Vector2i> _exposed = new();
    private readonly HashSet<Vector2i> _floorTiles = new();
    private readonly Queue<Vector2i> _flood = new();
    private readonly HashSet<EntityUid> _dirtyNow = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AirtightChanged>(OnAirtightChanged);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
        Log.Info("SubGridAtmosSystem initialized");
    }

    public void OnSubGridStartup(Entity<SubGridComponent> ent)
    {
        EnsureComp<GridAtmosphereComponent>(ent);
        Log.Debug("SubGrid atmos ensure GridAtmosphere on {Grid}", ToPrettyString(ent));
        _dirtyNow.Add(ent);
    }

    private void OnAirtightChanged(ref AirtightChanged ev)
    {
        if (HasComp<SubGridComponent>(ev.Position.Grid))
        {
            Log.Debug("SubGrid airtight changed on {Grid} tile={Tile}",
                ToPrettyString(ev.Position.Grid), ev.Position.Tile);
            _dirtyNow.Add(ev.Position.Grid);
        }
    }

    private void OnTileChanged(ref TileChangedEvent args)
    {
        var gridUid = args.Entity.Owner;
        if (HasComp<SubGridComponent>(gridUid))
        {
            Log.Debug("SubGrid tile changed on {Grid}", ToPrettyString(gridUid));
            _dirtyNow.Add(gridUid);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_dirtyNow.Count > 0)
        {
            foreach (var uid in _dirtyNow)
            {
                if (TryComp<SubGridComponent>(uid, out var sub) &&
                    TryComp(uid, out MapGridComponent? grid) &&
                    TryComp(uid, out TransformComponent? xform) &&
                    TryComp(uid, out GridAtmosphereComponent? atmos))
                {
                    SyncAtmos((uid, sub), grid, xform, atmos);
                }
            }

            _dirtyNow.Clear();
        }

        if (_timing.CurTime < _nextSync)
            return;

        _nextSync = _timing.CurTime + SyncInterval;

        var query = EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent, GridAtmosphereComponent>();
        while (query.MoveNext(out var uid, out var sub, out var grid, out var xform, out var atmos))
        {
            SyncAtmos((uid, sub), grid, xform, atmos);
        }
    }

    private void SyncAtmos(
        Entity<SubGridComponent> ent,
        MapGridComponent grid,
        TransformComponent xform,
        GridAtmosphereComponent atmos)
    {
        if (xform.MapUid == null || xform.MapID == MapId.Nullspace)
            return;

        CollectFloorTiles(ent, grid);
        ComputeExposed(ent, grid, atmos);

        var proxied = 0;
        var sealedCount = 0;
        float sampleMoles = 0f;
        float samplePressure = 0f;
        var sampleSpace = false;
        EntityUid? sampleParent = null;

        foreach (var indices in _floorTiles)
        {
            if (!_exposed.Contains(indices))
            {
                sealedCount++;
                EnsureSealedTile(ent, atmos, indices);
                continue;
            }

            proxied++;
            ApplyProxyTile(ent, grid, xform, atmos, indices, ref sampleMoles, ref samplePressure, ref sampleSpace, ref sampleParent);
        }

        // Open (fully proxied) pads: disable LINDA so edge tiles don't equalize into space with hissing.
        // Sealed rooms still need simulation.
        var wantSim = sealedCount > 0;
        if (atmos.Simulated != wantSim)
        {
            Log.Info("SubGrid atmos Simulated {Old} -> {New} on {Grid} (sealed={Sealed} proxied={Proxy})",
                atmos.Simulated, wantSim, ToPrettyString(ent), sealedCount, proxied);
            atmos.Simulated = wantSim;
        }

        if (!wantSim)
        {
            // Kill residual pressure processing that causes space-leak hiss on proxy tiles.
            atmos.ActiveTiles.Clear();
            atmos.HighPressureDelta.Clear();
            atmos.HotspotTiles.Clear();
            foreach (var tile in atmos.MapTiles)
            {
                tile.PressureDifference = 0f;
                tile.Excited = false;
                tile.ExcitedGroup = null;
            }
        }
        else
        {
            // Still quiet proxy tiles so they don't hiss against space shells.
            foreach (var indices in _exposed)
            {
                if (!atmos.Tiles.TryGetValue(indices, out var tile))
                    continue;

                atmos.ActiveTiles.Remove(tile);
                atmos.HighPressureDelta.Remove(tile);
                tile.PressureDifference = 0f;
                tile.Excited = false;
                tile.ExcitedGroup = null;
            }
        }

        Log.Debug(
            "SubGrid atmos sync: {Grid} floors={Floors} proxied={Proxy} sealed={Sealed} simulated={Sim} sampleMoles={Moles:F2} samplePressure={Pressure:F1} sampleSpace={Space} parent={Parent}",
            ToPrettyString(ent),
            _floorTiles.Count,
            proxied,
            sealedCount,
            atmos.Simulated,
            sampleMoles,
            samplePressure,
            sampleSpace,
            sampleParent == null ? "none" : ToPrettyString(sampleParent.Value));
    }

    private void CollectFloorTiles(EntityUid uid, MapGridComponent grid)
    {
        _floorTiles.Clear();
        var enumerator = _map.GetAllTilesEnumerator(uid, grid);
        while (enumerator.MoveNext(out var tileRefNullable))
        {
            if (tileRefNullable is not { } tileRef || tileRef.Tile.IsEmpty)
                continue;

            var def = (ContentTileDefinition) _tileDefs[tileRef.Tile.TypeId];
            if (def.MapAtmosphere)
                continue;

            _floorTiles.Add(tileRef.GridIndices);
        }
    }

    /// <summary>
    /// Flood-fill from exterior seeds through non-airtight directions. Reached floor tiles are exposed (proxy).
    /// </summary>
    private void ComputeExposed(EntityUid uid, MapGridComponent grid, GridAtmosphereComponent atmos)
    {
        _exposed.Clear();
        _flood.Clear();

        foreach (var indices in _floorTiles)
        {
            if (!IsExteriorSeed(uid, grid, atmos, indices))
                continue;

            _flood.Enqueue(indices);
            _exposed.Add(indices);
        }

        while (_flood.TryDequeue(out var current))
        {
            foreach (var dir in Cardinals)
            {
                if (_atmos.IsTileAirBlocked(uid, current, dir, grid))
                    continue;

                var neighbor = current.Offset(dir);
                if (!_floorTiles.Contains(neighbor))
                    continue;

                if (_atmos.IsTileAirBlocked(uid, neighbor, dir.GetOpposite(), grid))
                    continue;

                if (_exposed.Add(neighbor))
                    _flood.Enqueue(neighbor);
            }
        }
    }

    private bool IsExteriorSeed(EntityUid uid, MapGridComponent grid, GridAtmosphereComponent atmos, Vector2i indices)
    {
        foreach (var dir in Cardinals)
        {
            if (_atmos.IsTileAirBlocked(uid, indices, dir, grid))
                continue;

            var neighbor = indices.Offset(dir);
            if (!_floorTiles.Contains(neighbor))
                return true;

            // Neighbor tile exists as floor but is NoGridTile atmos shell — treat as exterior.
            if (atmos.Tiles.TryGetValue(neighbor, out var tile) && tile.NoGridTile)
                return true;
        }

        return false;
    }

    private void EnsureSealedTile(EntityUid uid, GridAtmosphereComponent atmos, Vector2i indices)
    {
        if (!atmos.Tiles.TryGetValue(indices, out var tile))
        {
            // Need a real simulated tile; keep Simulated true when any sealed rooms exist.
            tile = EnsureTileAtmosphere(uid, atmos, indices);
            tile.MapAtmosphere = false;
            tile.Air = new GasMixture(Atmospherics.CellVolume);
            atmos.MapTiles.Remove(tile);
            _atmos.InvalidateTile((uid, atmos), indices);
            return;
        }

        if (!tile.MapAtmosphere)
            return;

        Log.Debug("SubGrid atmos sealed (own air): {Grid} tile={Tile}", ToPrettyString(uid), indices);
        atmos.MapTiles.Remove(tile);
        tile.MapAtmosphere = false;
        tile.Air = null;
        tile.AirArchived = null;
        tile.ArchivedCycle = 0;
        tile.LastShare = 0f;
        tile.Space = false;
        _atmos.InvalidateTile((uid, atmos), indices);
    }

    private void ApplyProxyTile(
        EntityUid uid,
        MapGridComponent grid,
        TransformComponent xform,
        GridAtmosphereComponent atmos,
        Vector2i indices,
        ref float sampleMoles,
        ref float samplePressure,
        ref bool sampleSpace,
        ref EntityUid? sampleParent)
    {
        // InvalidateTile only builds TileAtmosphere while Simulated=true. Open pads set
        // Simulated=false, so create entries ourselves or breathing stays vacuum forever.
        var tile = EnsureTileAtmosphere(uid, atmos, indices);

        var tileCenter = _map.GridTileToWorld(uid, grid, indices).Position;
        if (!TryGetParentUnder(uid, xform.MapID, xform.MapUid, tileCenter, out var parentUid, out var parentGrid))
        {
            ApplyMixture(atmos, tile, GasMixture.SpaceGas, space: true);
            sampleMoles = tile.Air?.TotalMoles ?? 0f;
            samplePressure = tile.Air?.Pressure ?? 0f;
            sampleSpace = true;
            sampleParent = null;
            Log.Debug("SubGrid atmos proxy tile {Tile} on {Grid}: no parent -> SpaceGas", indices, ToPrettyString(uid));
            return;
        }

        var parentIndices = _map.WorldToTile(parentUid, parentGrid, tileCenter);
        var mix = _atmos.GetTileMixture(parentUid, xform.MapUid, parentIndices) ?? GasMixture.SpaceGas;
        var space = _atmos.IsTileSpace(parentUid, xform.MapUid, parentIndices);
        ApplyMixture(atmos, tile, mix, space);

        sampleMoles = tile.Air?.TotalMoles ?? 0f;
        samplePressure = tile.Air?.Pressure ?? 0f;
        sampleSpace = space;
        sampleParent = parentUid;
    }

    private static TileAtmosphere EnsureTileAtmosphere(EntityUid gridUid, GridAtmosphereComponent atmos, Vector2i indices)
    {
        if (atmos.Tiles.TryGetValue(indices, out var existing))
            return existing;

        var tile = new TileAtmosphere(
            gridUid,
            indices,
            new GasMixture(Atmospherics.CellVolume),
            immutable: true);
        atmos.Tiles[indices] = tile;
        return tile;
    }

    private void ApplyMixture(GridAtmosphereComponent atmos, TileAtmosphere tile, GasMixture source, bool space)
    {
        GasMixture air;
        if (source.Immutable)
        {
            air = source;
        }
        else
        {
            air = source.Clone();
            air.MarkImmutable();
        }

        var wasMap = tile.MapAtmosphere;
        tile.Air = air;
        tile.AirArchived = air.Clone();
        tile.Space = space;
        tile.MapAtmosphere = true;
        atmos.MapTiles.Add(tile);

        if (!wasMap)
        {
            Log.Debug("SubGrid atmos proxy enabled: grid={Grid} tile={Tile} space={Space}",
                ToPrettyString(tile.GridIndex), tile.GridIndices, space);
        }
    }

    private bool TryGetParentUnder(
        EntityUid self,
        MapId mapId,
        EntityUid? mapUid,
        Vector2 worldPos,
        out EntityUid parentUid,
        out MapGridComponent parentGrid)
    {
        parentUid = default;
        parentGrid = default!;

        // Tile-sized query — tiny boxes miss station grids when pad AABB barely overlaps.
        var box = Box2.CenteredAround(worldPos, new Vector2(0.9f, 0.9f));
        var grids = _overlapGridsBuffer;
        grids.Clear();
        _mapManager.FindGridsIntersecting(mapId, box, ref grids);

        EntityUid? best = null;
        MapGridComponent? bestGrid = null;
        var bestArea = float.MaxValue;

        foreach (var other in grids)
        {
            if (other.Owner == self)
                continue;

            if (HasComp<SubGridComponent>(other.Owner))
                continue;

            // Prefer the smallest overlapping non-sub grid (station piece underfoot).
            var area = MathF.Max(0.01f, other.Comp.LocalAABB.Width * other.Comp.LocalAABB.Height);
            if (best != null && area >= bestArea)
                continue;

            best = other.Owner;
            bestGrid = other.Comp;
            bestArea = area;
        }

        if (best == null || bestGrid == null)
            return false;

        parentUid = best.Value;
        parentGrid = bestGrid;
        return true;
    }
}
