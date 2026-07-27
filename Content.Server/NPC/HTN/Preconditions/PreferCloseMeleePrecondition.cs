using Content.Server.NPC.Systems;
using Content.Shared.NPC.Systems;
using Robust.Shared.Random;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when a hostile is in melee range, no soft cover is usable, and a probabilistic roll prefers melee over kiting.
/// </summary>
public sealed partial class PreferCloseMeleePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    /// <summary>
    /// Chance to prefer melee when other conditions are met.
    /// </summary>
    [DataField]
    public float Chance = 0.4f;

    [DataField]
    public string RangeKey = "MeleeRange";

    [DataField]
    public string CoverSearchRangeKey = "VisionRadius";

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var meleeRange = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);
        if (meleeRange <= 0f)
            meleeRange = 1f;

        EntityUid? enemy = null;
        foreach (var hostile in _entManager.System<NpcFactionSystem>().GetNearbyHostiles(owner, meleeRange))
        {
            enemy = hostile;
            break;
        }

        if (enemy == null)
            return false;

        var searchRange = blackboard.GetValueOrDefault<float>(CoverSearchRangeKey, _entManager);
        if (searchRange <= 0f)
            searchRange = NPCCoverSystem.DefaultCoverSearchRange;

        if (_entManager.System<NPCCoverSystem>().HasUsableCoverNearby(owner, enemy.Value, searchRange))
            return false;

        return _random.Prob(Chance);
    }
}
