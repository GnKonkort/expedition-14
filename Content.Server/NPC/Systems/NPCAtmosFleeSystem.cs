using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.HTN;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.Maps;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.NPC.Systems;

/// <summary>
/// AtmosFlee v1: sense vacuum/low pressure and pick a nearby airtight tile destination.
/// Cadence: on demand from HTN operators. BFS capped to ~64 tiles.
/// </summary>
public sealed class NPCAtmosFleeSystem : EntitySystem
{
    private const int MaxBfsTiles = 64;
    private const float SafePressure = Atmospherics.WarningLowPressure;

    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    private bool _debug;

    private static readonly Vector2i[] Neighbors =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
    };

    public override void Initialize()
    {
        base.Initialize();
        Subs.CVar(_cfg, CCVars.NPCDebugAtmos, v => _debug = v, true);
    }

    public bool TryFindSafeCoordinates(EntityUid owner, out EntityCoordinates coords)
    {
        coords = default;
        if (!TryComp(owner, out TransformComponent? xform) || xform.GridUid is not { } gridUid)
            return false;

        if (!TryComp(gridUid, out MapGridComponent? grid))
            return false;

        var origin = _map.CoordinatesToTile(gridUid, grid, xform.Coordinates);
        var visited = new HashSet<Vector2i>(MaxBfsTiles);
        var queue = new Queue<Vector2i>();
        queue.Enqueue(origin);
        visited.Add(origin);

        while (queue.Count > 0 && visited.Count < MaxBfsTiles)
        {
            var tile = queue.Dequeue();
            var mixture = _atmos.GetTileMixture(gridUid, xform.MapUid, tile);
            if (mixture != null &&
                mixture.Pressure > SafePressure &&
                _atmos.IsMixtureProbablySafe(mixture))
            {
                coords = _map.GridTileToLocal(gridUid, grid, tile);
                if (_debug)
                    Log.Info($"NPCAtmosFlee {ToPrettyString(owner)} safe → {tile}");
                return true;
            }

            foreach (var offset in Neighbors)
            {
                var next = tile + offset;
                if (!visited.Add(next))
                    continue;

                if (!_map.TryGetTileRef(gridUid, grid, next, out var tileRef) || tileRef.Tile.IsEmpty)
                    continue;

                queue.Enqueue(next);
            }
        }

        return false;
    }

    public bool TrySelectFleeTarget(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!TryFindSafeCoordinates(owner, out var coords))
            return false;

        blackboard.SetValue(NPCBlackboard.AtmosSafeCoordinates, coords);
        blackboard.SetValue(NPCBlackboard.TargetCoordinates, coords);
        return true;
    }
}
