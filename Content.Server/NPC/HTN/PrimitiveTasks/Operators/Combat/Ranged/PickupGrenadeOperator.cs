using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Picks grenade target and stows it into inventory.
/// </summary>
public sealed partial class PickupGrenadeOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return HTNOperatorStatus.Failed;

        var grenades = _entManager.System<NPCGrenadeSystem>();
        if (!grenades.IsGrenade(target))
            return HTNOperatorStatus.Failed;

        var ammo = _entManager.System<NPCGunAmmoSystem>();
        if (!ammo.TryObtainInHand(owner, target))
            return HTNOperatorStatus.Failed;

        return ammo.TryStowItem(owner, target) ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
