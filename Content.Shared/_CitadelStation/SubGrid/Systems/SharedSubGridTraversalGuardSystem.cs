using Content.Shared.Mobs.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map.Components;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Prevents <see cref="SharedGridTraversalSystem"/> from reparenting riders onto the host
/// MapGrid while they stand on an overlapping SubGrid (<c>TryFindGridAt</c> prefers the host).
/// Re-enables traversal only once the rider leaves solid SubGrid floor so they can exit.
/// </summary>
public sealed class SharedSubGridTraversalGuardSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!TryGetAboardSubGrid(xform, out var subUid))
            {
                if (!xform.GridTraversal)
                    xform.GridTraversal = true;
                continue;
            }

            // Keep traversal off while over any solid SubGrid tile (including stairs).
            // Enabling near boarding let TryFindGridAt prefer the host under the pad → STEAL
            // while still aboard. Exit works once the mob steps onto empty / off-pad tiles.
            var allowTraverse = !IsOnSolidSubFloor(uid, subUid);

            if (xform.GridTraversal != allowTraverse)
                xform.GridTraversal = allowTraverse;
        }
    }

    private bool TryGetAboardSubGrid(TransformComponent xform, out EntityUid subUid)
    {
        if (xform.GridUid is { } grid && HasComp<SubGridComponent>(grid))
        {
            subUid = grid;
            return true;
        }

        if (HasComp<SubGridComponent>(xform.ParentUid))
        {
            subUid = xform.ParentUid;
            return true;
        }

        subUid = default;
        return false;
    }

    private bool IsOnSolidSubFloor(EntityUid mob, EntityUid subUid)
    {
        if (!TryComp(subUid, out MapGridComponent? grid))
            return false;

        var world = _transform.GetWorldPosition(mob);
        var tile = _map.WorldToTile(subUid, grid, world);
        return _map.TryGetTileRef(subUid, grid, tile, out var tileRef) && !tileRef.Tile.IsEmpty;
    }
}
