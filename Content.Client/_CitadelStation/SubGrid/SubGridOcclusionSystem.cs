using System.Numerics;
using Content.Client.Markers;
using Content.Shared.Projectiles;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Throwing;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._CitadelStation.SubGrid;

/// <summary>
/// Covers host-grid sprites under a SubGrid pad by lowering their draw depth so
/// <see cref="Overlays.SubGridTileOverlay"/> floor tiles paint over them.
/// Does NOT hard-hide sprites — parts outside floor tiles stay visible (true overlap).
/// </summary>
public sealed class SubGridOcclusionSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<SubGridComponent> _subQuery;
    private EntityQuery<SpriteComponent> _spriteQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<ThrownItemComponent> _thrownQuery;
    private EntityQuery<MarkerComponent> _markerQuery;

    /// <summary>Entities we pushed under the pad floor; value is original DrawDepth.</summary>
    private readonly Dictionary<EntityUid, int> _covered = new();
    private readonly HashSet<EntityUid> _shouldCover = new();
    private readonly List<EntityUid> _restoreBuffer = new();

    /// <summary>Draw depth used while covered — below SubGridTileOverlay (FloorTiles / FloorObjects).</summary>
    private const int CoveredDrawDepth = (int) DrawDepth.BelowFloor;

    /// <summary>Minimum overlap area (world units²) before treating an entity as under the pad.</summary>
    private const float MinOverlapArea = 0.08f;

    public override void Initialize()
    {
        base.Initialize();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _subQuery = GetEntityQuery<SubGridComponent>();
        _spriteQuery = GetEntityQuery<SpriteComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _thrownQuery = GetEntityQuery<ThrownItemComponent>();
        _markerQuery = GetEntityQuery<MarkerComponent>();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        _shouldCover.Clear();

        var query = EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var subUid, out _, out var grid, out var xform))
        {
            if (xform.MapID == MapId.Nullspace)
                continue;

            var worldAabb = _transform.GetWorldMatrix(xform).TransformBox(grid.LocalAABB).Enlarged(0.05f);
            foreach (var ent in _lookup.GetEntitiesIntersecting(xform.MapID, worldAabb, LookupFlags.Approximate | LookupFlags.Static | LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                if (ShouldCoverUnder(ent, subUid, grid, xform))
                    _shouldCover.Add(ent);
            }
        }

        foreach (var uid in _shouldCover)
        {
            if (_covered.ContainsKey(uid))
                continue;

            if (!_spriteQuery.TryComp(uid, out var sprite))
                continue;

            // Already at/below cover depth and not managed by us — leave alone.
            if (!sprite.Visible)
                continue;

            _covered[uid] = sprite.DrawDepth;
            // Only push down if currently above the pad floor overlay.
            if (sprite.DrawDepth > CoveredDrawDepth)
                _sprite.SetDrawDepth((uid, sprite), CoveredDrawDepth);
        }

        _restoreBuffer.Clear();
        foreach (var (uid, originalDepth) in _covered)
        {
            if (_shouldCover.Contains(uid))
                continue;

            _restoreBuffer.Add(uid);

            if (Deleted(uid))
                continue;

            if (_spriteQuery.TryComp(uid, out var sprite))
                _sprite.SetDrawDepth((uid, sprite), originalDepth);
        }

        foreach (var uid in _restoreBuffer)
            _covered.Remove(uid);
    }

    private bool ShouldCoverUnder(EntityUid ent, EntityUid subUid, MapGridComponent grid, TransformComponent subXform)
    {
        if (ent == subUid || Deleted(ent))
            return false;

        if (_markerQuery.HasComp(ent))
            return false;

        if (!_spriteQuery.HasComp(ent))
            return false;

        if (_projectileQuery.HasComp(ent) || _thrownQuery.HasComp(ent))
            return false;

        var xform = Transform(ent);

        // Aboard this SubGrid — must stay above pad floors.
        if (xform.GridUid == subUid || xform.ParentUid == subUid)
            return false;

        if (xform.GridUid is not { } otherGrid || otherGrid == subUid)
            return false;

        if (_subQuery.HasComp(otherGrid))
            return false;

        if (!_gridQuery.HasComp(otherGrid))
            return false;

        var entAabb = _lookup.GetWorldAABB(ent, xform);
        if (entAabb.IsEmpty())
        {
            var world = _transform.GetWorldPosition(xform);
            entAabb = Box2.CenteredAround(world, new Vector2(0.35f, 0.35f));
        }

        var inv = _transform.GetInvWorldMatrix(subXform);
        var localAabb = inv.TransformBox(entAabb);
        var overlapArea = 0f;
        var tileSize = grid.TileSize;
        var tileArea = tileSize * tileSize;

        foreach (var tileRef in _map.GetLocalTilesIntersecting(subUid, grid, localAabb, ignoreEmpty: false))
        {
            if (tileRef.Tile.IsEmpty)
                continue;

            var tileLocal = _lookup.GetLocalBounds(tileRef, tileSize);
            var inter = localAabb.Intersect(tileLocal);
            if (inter.IsEmpty())
                continue;

            overlapArea += inter.Width * inter.Height;
            if (overlapArea >= MinOverlapArea || overlapArea >= tileArea * 0.25f)
                return true;
        }

        return overlapArea >= MinOverlapArea;
    }
}
