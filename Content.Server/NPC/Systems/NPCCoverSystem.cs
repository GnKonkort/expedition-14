using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Cover;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Picks and reserves soft-cover stand tiles / hold-range positions for ranged NPCs.
/// </summary>
public sealed class NPCCoverSystem : EntitySystem
{
    public const float DefaultCoverSearchRange = 14f;
    public const float MinHoldRange = 6f;
    public const float MaxHoldRange = 10f;
    public const float PreferredHoldRange = 8f;
    /// <summary>
    /// How close the NPC must get to the stand point (cade / table stand).
    /// </summary>
    public const float CoverArriveRange = 0.35f;

    /// <summary>
    /// Distance from stand-tile center toward the table.
    /// Keep well under 0.5 so the point stays clear of the table fixture (else MoveTo NoPath).
    /// </summary>
    public const float TableStandEdgeNudge = 0.3f;

    /// <summary>
    /// Push the barricade stand further into the rear tile, away from the thin cade strip
    /// on the shared edge (cade fixture sits on the cover tile's face side).
    /// </summary>
    public const float BarricadeStandNudge = 0.28f;
    public const string CoverCoordinatesKey = "CoverCoordinates";
    public const string CoverApproachCoordinatesKey = "CoverApproachCoordinates";
    public const string CoverRearCoordinatesKey = "CoverRearCoordinates";
    public const string CoverEntityKey = "CoverEntity";
    public const string CoverSlotKey = "CoverSlot";
    public const string PreferredRangedRangeKey = "PreferredRangedRange";

    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedCoverSystem _cover = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private readonly Dictionary<CoverSlotId, EntityUid> _reservations = new();
    private EntityQuery<DirectionalCoverComponent> _directionalQuery;
    private EntityQuery<ProbabilisticCoverComponent> _probQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private bool _debug;

    public bool DebugEnabled => _debug;

    public override void Initialize()
    {
        base.Initialize();
        _directionalQuery = GetEntityQuery<DirectionalCoverComponent>();
        _probQuery = GetEntityQuery<ProbabilisticCoverComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        _debug = _cfg.GetCVar(CCVars.NPCDebugCover);
        Subs.CVar(_cfg, CCVars.NPCDebugCover, v =>
        {
            _debug = v;
            Debug($"npc.debug_cover={(v ? "ON" : "OFF")}");
        });
    }

    public readonly record struct CoverSlotId(EntityUid? CoverEntity, EntityUid Grid, Vector2i Tile);

    public void Debug(string message)
    {
        if (!_debug)
            return;

        Log.Info($"[npc.cover] {message}");
    }

    public void ReleaseAll(EntityUid npc)
    {
        List<CoverSlotId>? remove = null;
        foreach (var (slot, owner) in _reservations)
        {
            if (owner != npc)
                continue;
            remove ??= new List<CoverSlotId>();
            remove.Add(slot);
        }

        if (remove == null)
            return;

        foreach (var slot in remove)
        {
            _reservations.Remove(slot);
            Debug($"RELEASE {ToPrettyString(npc)} slot={DescribeSlot(slot)}");
        }
    }

    public bool IsSlotFree(CoverSlotId slot, EntityUid npc)
    {
        if (!_reservations.TryGetValue(slot, out var owner))
            return true;

        if (owner == npc)
            return true;

        if (!Exists(owner))
        {
            _reservations.Remove(slot);
            return true;
        }

        return false;
    }

    public bool TryReserve(CoverSlotId slot, EntityUid npc)
    {
        if (!IsSlotFree(slot, npc))
        {
            Debug($"RESERVE FAIL {ToPrettyString(npc)} slot={DescribeSlot(slot)} reason=slot-taken");
            return false;
        }

        // One NPC per barricade entity.
        if (slot.CoverEntity is { } coverEnt)
        {
            foreach (var (existing, owner) in _reservations)
            {
                if (existing.CoverEntity != coverEnt || owner == npc)
                    continue;
                if (Exists(owner))
                {
                    Debug($"RESERVE FAIL {ToPrettyString(npc)} cover={ToPrettyString(coverEnt)} reason=cover-owned-by {ToPrettyString(owner)}");
                    return false;
                }
            }
        }

        _reservations[slot] = npc;
        Debug($"RESERVE OK {ToPrettyString(npc)} slot={DescribeSlot(slot)}");
        return true;
    }

    public bool TrySelectCover(
        EntityUid npc,
        EntityUid enemy,
        float searchRange,
        out EntityUid coverEntity,
        out EntityCoordinates standCoords,
        out EntityCoordinates approachCoords,
        out CoverSlotId slot)
    {
        coverEntity = default;
        standCoords = default;
        approachCoords = default;
        slot = default;

        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            !_xformQuery.TryGetComponent(enemy, out var enemyXform))
        {
            Debug($"SELECT FAIL {ToPrettyString(npc)} vs {ToPrettyString(enemy)} reason=missing-xform");
            return false;
        }

        var npcMap = _transform.GetMapCoordinates(npc, xform: npcXform);
        var enemyMap = _transform.GetMapCoordinates(enemy, xform: enemyXform);
        if (npcMap.MapId != enemyMap.MapId)
        {
            Debug($"SELECT FAIL {ToPrettyString(npc)} vs {ToPrettyString(enemy)} reason=map-mismatch");
            return false;
        }

        EntityUid? bestCover = null;
        EntityCoordinates bestStand = default;
        EntityCoordinates bestApproach = default;
        CoverSlotId bestSlot = default;
        var bestScore = float.MinValue;
        var scanned = 0;
        var directionalOk = 0;
        var tableOk = 0;
        var skipInactive = 0;
        var skipNoStand = 0;
        var skipTaken = 0;
        var skipFace = 0;

        foreach (var ent in _lookup.GetEntitiesInRange(npcMap, searchRange))
        {
            if (!_cover.IsCoverActive(ent))
            {
                if (_directionalQuery.HasComponent(ent) || _probQuery.HasComponent(ent))
                    skipInactive++;
                continue;
            }

            if (!_xformQuery.TryGetComponent(ent, out var coverXform) || coverXform.GridUid is not { } gridUid)
                continue;

            if (!TryComp(gridUid, out MapGridComponent? grid))
                continue;

            scanned++;

            if (_directionalQuery.HasComponent(ent))
            {
                if (!TryGetDirectionalStand(ent, gridUid, grid, enemyMap, out var stand, out var coverSlot))
                {
                    skipNoStand++;
                    continue;
                }

                if (!IsSlotFree(coverSlot, npc))
                {
                    skipTaken++;
                    continue;
                }

                // Enemy must be on the attack face for the stand to be useful.
                if (!_cover.ShouldDirectionalBlock(ent, enemyMap))
                {
                    skipFace++;
                    continue;
                }

                if (!TryGetDirectionalApproach(ent, gridUid, grid, npcMap, stand, out var approach))
                {
                    skipNoStand++;
                    continue;
                }

                var score = ScoreCandidate(npcMap, enemyMap, _transform.ToMapCoordinates(stand), approach);
                directionalOk++;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestCover = ent;
                bestStand = stand;
                bestApproach = approach;
                bestSlot = coverSlot;
            }
            else if (_probQuery.HasComponent(ent))
            {
                if (!TryGetTableStand(ent, gridUid, grid, enemyMap, npc, out var stand, out var coverSlot))
                {
                    skipNoStand++;
                    continue;
                }

                if (!IsSlotFree(coverSlot, npc))
                {
                    skipTaken++;
                    continue;
                }

                var score = ScoreCandidate(npcMap, enemyMap, _transform.ToMapCoordinates(stand), stand);
                tableOk++;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestCover = ent;
                bestStand = stand;
                bestApproach = stand;
                bestSlot = coverSlot;
            }
        }

        if (bestCover == null)
        {
            Debug(
                $"SELECT FAIL {ToPrettyString(npc)} vs {ToPrettyString(enemy)} range={searchRange:F1} scanned={scanned} dirOk={directionalOk} tableOk={tableOk} skipInactive={skipInactive} skipNoStand={skipNoStand} skipTaken={skipTaken} skipFace={skipFace}");
            return false;
        }

        coverEntity = bestCover.Value;
        standCoords = bestStand;
        approachCoords = bestApproach;
        slot = bestSlot;
        Debug(
            $"SELECT OK {ToPrettyString(npc)} vs {ToPrettyString(enemy)} cover={ToPrettyString(coverEntity)} stand={DescribeCoords(standCoords)} approach={DescribeCoords(approachCoords)} score={bestScore:F2} slot={DescribeSlot(slot)} scanned={scanned} dirOk={directionalOk} tableOk={tableOk}");
        return true;
    }

    public bool TrySelectHoldRange(
        EntityUid npc,
        EntityUid enemy,
        float preferredRange,
        out EntityCoordinates holdCoords)
    {
        holdCoords = default;

        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            !_xformQuery.TryGetComponent(enemy, out var enemyXform))
            return false;

        var npcMap = _transform.GetMapCoordinates(npc, xform: npcXform);
        var enemyMap = _transform.GetMapCoordinates(enemy, xform: enemyXform);
        if (npcMap.MapId != enemyMap.MapId || enemyXform.GridUid is not { } gridUid)
            return false;

        if (!TryComp(gridUid, out MapGridComponent? grid))
            return false;

        preferredRange = Math.Clamp(preferredRange, MinHoldRange, MaxHoldRange);

        var currentDist = (npcMap.Position - enemyMap.Position).Length();
        if (currentDist >= MinHoldRange && currentDist <= MaxHoldRange &&
            HasShootLos(npc, enemy, currentDist + 0.5f))
        {
            holdCoords = npcXform.Coordinates;
            Debug($"HOLD OK {ToPrettyString(npc)} stay-put dist={currentDist:F1}");
            return true;
        }

        EntityCoordinates? best = null;
        var bestScore = float.MinValue;
        const int samples = 16;

        for (var i = 0; i < samples; i++)
        {
            var angle = (MathF.Tau * i) / samples;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * preferredRange;
            var worldPos = enemyMap.Position + offset;
            var tileIndices = _map.WorldToTile(gridUid, grid, worldPos);
            var tile = _map.GetTileRef(gridUid, grid, tileIndices);
            if (tile.Tile.IsEmpty || _turf.IsTileBlocked(tile, CollisionGroup.Impassable))
                continue;

            var local = _map.GridTileToLocal(gridUid, grid, tile.GridIndices);
            var mapCoords = _transform.ToMapCoordinates(local);
            var distNpc = (mapCoords.Position - npcMap.Position).Length();
            var distEnemy = (mapCoords.Position - enemyMap.Position).Length();
            if (distEnemy < MinHoldRange - 0.5f || distEnemy > MaxHoldRange + 0.5f)
                continue;

            if (!_interaction.InRangeUnobstructed(mapCoords, enemyMap, distEnemy + 0.5f,
                    CollisionGroup.Opaque))
                continue;

            var score = -distNpc - MathF.Abs(distEnemy - preferredRange);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = local;
        }

        if (best == null)
        {
            Debug($"HOLD FAIL {ToPrettyString(npc)} vs {ToPrettyString(enemy)} preferred={preferredRange:F1}");
            return false;
        }

        holdCoords = best.Value;
        Debug($"HOLD OK {ToPrettyString(npc)} coords={DescribeCoords(holdCoords)} preferred={preferredRange:F1}");
        return true;
    }

    public bool HasUsableCoverNearby(EntityUid npc, EntityUid enemy, float searchRange = DefaultCoverSearchRange)
    {
        return TrySelectCover(npc, enemy, searchRange, out _, out _, out _, out _);
    }

    public bool IsAtCoverStand(EntityUid npc, EntityCoordinates standCoords, float range = CoverArriveRange)
    {
        if (!_xformQuery.TryGetComponent(npc, out var xform))
            return false;

        // Must actually reach the stand point (near the cover), not just enter the tile.
        if (!_transform.InRange(xform.Coordinates, standCoords, range))
            return false;

        // Guard against finishing from an adjacent flank that happens to be within range.
        if (TryGetSharedGridTiles(npc, standCoords, out _, out var npcTile, out var standTile))
            return npcTile == standTile;

        return true;
    }

    /// <summary>
    /// True when the NPC is holding the reserved cover stand.
    /// Directional: opposite the attack face (rear / cade), never the face toward the enemy.
    /// Tables: reserved adjacent tile.
    /// </summary>
    public bool IsInCoverPosition(EntityUid npc, EntityUid cover, EntityCoordinates stand, float range = CoverArriveRange)
    {
        if (!_cover.IsCoverActive(cover))
            return false;

        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            !_xformQuery.TryGetComponent(cover, out var coverXform) ||
            coverXform.GridUid is not { } gridUid)
            return false;

        if (!TryComp(gridUid, out MapGridComponent? grid))
            return false;

        // Tables: no attack face — standing on the reserved adjacent tile is enough.
        if (_probQuery.HasComponent(cover) && !_directionalQuery.HasComponent(cover))
            return IsAtCoverStand(npc, stand, range);

        var npcMap = _transform.GetMapCoordinates(npc, xform: npcXform);

        // On the attack face (same side shots are blocked from) — not in cover.
        if (_cover.ShouldDirectionalBlock(cover, npcMap))
            return false;

        var coverTile = _map.CoordinatesToTile(gridUid, grid, _transform.GetMoverCoordinates(cover, coverXform));
        var npcTile = _map.CoordinatesToTile(gridUid, grid, npcXform.Coordinates);

        // Rear stand tile, or on the cade itself after a vault.
        if (npcTile == coverTile)
            return true;

        return IsAtCoverStand(npc, stand, range);
    }

    /// <summary>
    /// Same-grid tile indices for an NPC and a target coordinate.
    /// </summary>
    public bool TryGetSharedGridTiles(
        EntityUid npc,
        EntityCoordinates target,
        out EntityUid gridUid,
        out Vector2i npcTile,
        out Vector2i targetTile)
    {
        gridUid = default;
        npcTile = default;
        targetTile = default;

        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            npcXform.GridUid is not { } npcGrid)
            return false;

        var targetGrid = _transform.GetGrid(target);
        if (targetGrid == null || targetGrid != npcGrid || !TryComp(npcGrid, out MapGridComponent? grid))
            return false;

        gridUid = npcGrid;
        npcTile = _map.CoordinatesToTile(npcGrid, grid, npcXform.Coordinates);
        targetTile = _map.CoordinatesToTile(npcGrid, grid, target);
        return true;
    }

    /// <summary>
    /// True when the NPC is on the rear-adjacent defender tile (not the attack face).
    /// </summary>
    public bool IsOnDefenderApproach(EntityUid npc, EntityUid cover)
    {
        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            !_xformQuery.TryGetComponent(cover, out var coverXform) ||
            coverXform.GridUid is not { } gridUid)
            return false;

        if (!TryComp(gridUid, out MapGridComponent? grid))
            return false;

        var coverTile = _map.CoordinatesToTile(gridUid, grid, _transform.GetMoverCoordinates(cover, coverXform));
        var npcTile = _map.CoordinatesToTile(gridUid, grid, npcXform.Coordinates);
        var delta = npcTile - coverTile;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) != 1)
            return false;

        var npcMap = _transform.GetMapCoordinates(npc, xform: npcXform);
        return !_cover.ShouldDirectionalBlock(cover, npcMap);
    }

    public bool ShouldAbandonCover(EntityUid npc, EntityUid enemy, EntityUid coverEntity, float meleeRange)
    {
        if (!_cover.IsCoverActive(coverEntity))
            return true;

        if (!_xformQuery.TryGetComponent(npc, out var npcXform) ||
            !_xformQuery.TryGetComponent(enemy, out var enemyXform))
            return true;

        return _transform.InRange(npcXform.Coordinates, enemyXform.Coordinates, meleeRange);
    }

    private bool HasShootLos(EntityUid from, EntityUid to, float range)
    {
        // Opaque only — glass/windows must not block NPC shoot LOS.
        return _interaction.InRangeUnobstructed(from, to, range, CollisionGroup.Opaque);
    }

    private float ScoreCandidate(
        MapCoordinates npc,
        MapCoordinates enemy,
        MapCoordinates stand,
        EntityCoordinates approach)
    {
        var approachMap = _transform.ToMapCoordinates(approach);
        var toApproach = (approachMap.Position - npc.Position).Length();
        var toStand = (stand.Position - npc.Position).Length();
        var toEnemy = (stand.Position - enemy.Position).Length();
        return -toApproach * 0.75f - toStand * 0.25f + Math.Clamp(toEnemy, 0f, 8f) * 0.25f;
    }

    private bool TryGetDirectionalStand(
        EntityUid cover,
        EntityUid gridUid,
        MapGridComponent grid,
        MapCoordinates enemyMap,
        out EntityCoordinates stand,
        out CoverSlotId slot)
    {
        stand = default;
        slot = default;

        if (!_xformQuery.TryGetComponent(cover, out var coverXform))
            return false;

        var coverCoords = _transform.GetMoverCoordinates(cover, coverXform);
        var coverTile = _map.CoordinatesToTile(gridUid, grid, coverCoords);

        // Pick the walkable orthogonal neighbour that is NOT on the attack face and is
        // farthest from the enemy — that is the true defender side relative to the threat.
        // Using only barricade rotation previously put NPCs on the face for some placements.
        EntityCoordinates? bestStand = null;
        Vector2i bestTile = default;
        var bestDistSq = float.MinValue;

        foreach (var offset in OrthogonalOffsets)
        {
            var candidateTile = coverTile + offset;
            var tileRef = _map.GetTileRef(gridUid, grid, candidateTile);
            if (tileRef.Tile.IsEmpty || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var center = _map.GridTileToLocal(gridUid, grid, candidateTile);
            var standMap = _transform.ToMapCoordinates(center);

            // Attack-face tile — never stand here.
            if (_cover.ShouldDirectionalBlock(cover, standMap))
                continue;

            var distSq = (standMap.Position - enemyMap.Position).LengthSquared();
            if (distSq <= bestDistSq)
                continue;

            bestDistSq = distSq;
            bestTile = candidateTile;
            bestStand = new EntityCoordinates(center.EntityId,
                center.Position + new Vector2(offset.X, offset.Y) * BarricadeStandNudge);
        }

        if (bestStand == null)
            return false;

        stand = bestStand.Value;
        slot = new CoverSlotId(cover, gridUid, bestTile);
        return true;
    }

    /// <summary>
    /// Approach equals the rear stand; pathfinding walks around the face when needed.
    /// Climb only kicks in if MoveTo gets stuck rubbing against the cade.
    /// </summary>
    private bool TryGetDirectionalApproach(
        EntityUid cover,
        EntityUid gridUid,
        MapGridComponent grid,
        MapCoordinates npcMap,
        EntityCoordinates stand,
        out EntityCoordinates approach)
    {
        approach = stand;
        return true;
    }

    private bool TryGetTableStand(
        EntityUid table,
        EntityUid gridUid,
        MapGridComponent grid,
        MapCoordinates enemyMap,
        EntityUid npc,
        out EntityCoordinates stand,
        out CoverSlotId slot)
    {
        stand = default;
        slot = default;

        var tableCoords = _transform.GetMoverCoordinates(table);
        var tableTile = _map.CoordinatesToTile(gridUid, grid, tableCoords);
        var tableMap = _transform.GetMapCoordinates(table);

        EntityCoordinates? best = null;
        CoverSlotId bestSlot = default;
        var bestScore = float.MinValue;

        foreach (var offset in OrthogonalOffsets)
        {
            var standTile = tableTile + offset;
            var tileRef = _map.GetTileRef(gridUid, grid, standTile);
            if (tileRef.Tile.IsEmpty || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var center = _map.GridTileToLocal(gridUid, grid, standTile);
            var standMap = _transform.ToMapCoordinates(center);
            var standToEnemy = enemyMap.Position - standMap.Position;
            var standToTable = tableMap.Position - standMap.Position;
            if (standToEnemy.LengthSquared() < 0.01f || standToTable.LengthSquared() < 0.01f)
                continue;

            var alignment = Vector2.Dot(standToEnemy.Normalized(), standToTable.Normalized());
            if (alignment < 0.35f)
                continue;

            if (standToEnemy.Length() <= standToTable.Length() + 0.1f)
                continue;

            var candSlot = new CoverSlotId(table, gridUid, standTile);
            if (!IsSlotFree(candSlot, npc))
                continue;

            var score = alignment;
            if (score <= bestScore)
                continue;

            // Stand at the shared edge of the adjacent tile (almost into the table).
            var towardTable = new Vector2(-offset.X, -offset.Y) * TableStandEdgeNudge;
            var nudged = new EntityCoordinates(center.EntityId, center.Position + towardTable);

            bestScore = score;
            best = nudged;
            bestSlot = candSlot;
        }

        if (best == null)
            return false;

        stand = best.Value;
        slot = bestSlot;
        return true;
    }

    private string DescribeSlot(CoverSlotId slot)
    {
        var cover = slot.CoverEntity is { } c ? ToPrettyString(c) : "none";
        return $"{cover}@{slot.Tile}";
    }

    private string DescribeCoords(EntityCoordinates coords)
    {
        var map = _transform.ToMapCoordinates(coords);
        return $"({map.Position.X:F1},{map.Position.Y:F1}) map={map.MapId}";
    }

    private static readonly Vector2i[] OrthogonalOffsets =
    [
        new(0, 1),
        new(0, -1),
        new(1, 0),
        new(-1, 0),
    ];
}
