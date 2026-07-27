using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Picks a random accessible coordinate within range of an origin blackboard key
/// (e.g. wander around a squad rally point).
/// </summary>
public sealed partial class PickNearCoordinatesOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private PathfindingSystem _pathfinding = default!;
    private SharedMapSystem _map = default!;
    private SharedTransformSystem _transform = default!;
    private TurfSystem _turf = default!;

    [DataField(required: true)]
    public string OriginKey = string.Empty;

    [DataField(required: true)]
    public string RangeKey = string.Empty;

    [DataField]
    public string TargetCoordinates = "TargetCoordinates";

    [DataField]
    public string PathfindKey = NPCBlackboard.PathfindKey;

    /// <summary>Minimum distance from the origin so NPCs do not stand still.</summary>
    [DataField]
    public float MinOffset = 0.6f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _pathfinding = sysManager.GetEntitySystem<PathfindingSystem>();
        _map = sysManager.GetEntitySystem<SharedMapSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
        _turf = sysManager.GetEntitySystem<TurfSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(OriginKey, out var origin, _entManager))
            return (false, null);

        var range = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);
        if (range <= 0f)
            range = 2.5f;

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entManager.TryGetComponent(owner, out TransformComponent? ownerXform))
            return (false, null);

        var minOffset = System.Math.Min(MinOffset, range * 0.4f);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var dist = _random.NextFloat(minOffset, range);
            var angle = _random.NextAngle();
            var candidate = origin.Offset(angle.ToVec() * dist);

            if (_transform.GetGrid(candidate) is { } gridUid &&
                _entManager.TryGetComponent(gridUid, out MapGridComponent? grid))
            {
                var tile = _map.CoordinatesToTile(gridUid, grid, candidate);
                var tileRef = _map.GetTileRef(gridUid, grid, tile);
                if (tileRef.Tile.IsEmpty || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                    continue;

                candidate = _map.GridTileToLocal(gridUid, grid, tile);
            }

            var path = await _pathfinding.GetPath(
                owner,
                ownerXform.Coordinates,
                candidate,
                range + 2f,
                cancelToken,
                flags: _pathfinding.GetFlags(blackboard));

            if (path.Result != PathResult.Path)
                continue;

            return (true, new Dictionary<string, object>
            {
                { TargetCoordinates, candidate },
                { PathfindKey, path },
            });
        }

        return (false, null);
    }
}
