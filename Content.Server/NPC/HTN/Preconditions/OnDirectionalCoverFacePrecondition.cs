using Content.Server.NPC.Systems;
using Content.Shared.Cover;
using Robust.Shared.Map.Components;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC is on the attack face of a nearby directional barricade (climb-over path preferred).
/// </summary>
public sealed partial class OnDirectionalCoverFacePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string SearchRangeKey = "VisionRadius";

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var sharedCover = _entManager.System<SharedCoverSystem>();
        var transform = _entManager.System<SharedTransformSystem>();
        var map = _entManager.System<SharedMapSystem>();
        var lookup = _entManager.System<EntityLookupSystem>();

        if (!_entManager.TryGetComponent(owner, out TransformComponent? xform) || xform.GridUid is not { } gridUid)
            return false;

        if (!_entManager.TryGetComponent(gridUid, out MapGridComponent? grid))
            return false;

        var searchRange = blackboard.GetValueOrDefault<float>(SearchRangeKey, _entManager);
        if (searchRange <= 0f)
            searchRange = NPCCoverSystem.DefaultCoverSearchRange;

        var npcMap = transform.GetMapCoordinates(owner, xform: xform);
        var npcTile = map.CoordinatesToTile(gridUid, grid, xform.Coordinates);

        foreach (var ent in lookup.GetEntitiesInRange(npcMap, searchRange))
        {
            if (!_entManager.HasComponent<DirectionalCoverComponent>(ent))
                continue;

            if (!sharedCover.IsCoverActive(ent))
                continue;

            if (!_entManager.TryGetComponent(ent, out TransformComponent? coverXform) ||
                coverXform.GridUid != gridUid)
                continue;

            var coverTile = map.CoordinatesToTile(gridUid, grid, transform.GetMoverCoordinates(ent, coverXform));
            var behind = sharedCover.GetDefenderApproachOffset(ent);
            if (behind == Vector2i.Zero)
                continue;

            var faceTile = coverTile - behind;
            if (npcTile == faceTile || sharedCover.ShouldDirectionalBlock(ent, npcMap))
                return true;
        }

        return false;
    }
}
