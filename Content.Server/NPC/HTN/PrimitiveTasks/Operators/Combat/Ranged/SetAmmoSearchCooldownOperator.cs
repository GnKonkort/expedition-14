using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Sets the ammo-search cooldown on the blackboard (used after failed/finished loot branches).
/// </summary>
public sealed partial class SetAmmoSearchCooldownOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        _entManager.System<NPCGunAmmoSystem>().SetAmmoSearchCooldown(blackboard);
        return HTNOperatorStatus.Finished;
    }
}
