using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Drops a used / empty recognized medipen from inventory.
/// </summary>
public sealed partial class DiscardEmptyMedipenOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!medical.TryFindEmptyMedipen(owner, out var item) || item == null)
            return HTNOperatorStatus.Failed;

        return medical.TryDiscardEmptyMedipen(owner, item.Value)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
