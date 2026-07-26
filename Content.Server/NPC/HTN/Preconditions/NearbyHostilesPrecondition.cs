using Content.Server.NPC.Systems;
using Content.Shared.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when at least one hostile is within range (default: AggroVisionRadius).
/// </summary>
public sealed partial class NearbyHostilesPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    /// <summary>
    /// Optional blackboard float key for range. When unset, uses AggroVisionRadius.
    /// </summary>
    [DataField]
    public string? RangeKey;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        float range;

        if (!string.IsNullOrEmpty(RangeKey))
        {
            range = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);
            if (RangeKey == NPCGunAmmoSystem.AmmoLootHostileRangeKey && range <= 0f)
                range = NPCGunAmmoSystem.DefaultAmmoLootHostileRange;
        }
        else
        {
            range = blackboard.GetValueOrDefault<float>("AggroVisionRadius", _entManager);
        }

        var hasHostile = false;
        foreach (var _ in _entManager.System<NpcFactionSystem>().GetNearbyHostiles(owner, range))
        {
            hasHostile = true;
            break;
        }

        return Invert ? !hasHostile : hasHostile;
    }
}
