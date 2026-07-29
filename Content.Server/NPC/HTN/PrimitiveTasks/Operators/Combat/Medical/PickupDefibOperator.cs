using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Picks up a floor defibrillator or opens a wall cabinet and takes the defib inside.
/// </summary>
public sealed partial class PickupDefibOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public string DefibItemKey = NPCBlackboard.DefibItem;

    [DataField]
    public string IsCabinetKey = "DefibSourceIsCabinet";

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (medical.TryGetOwnedDefib(owner, out var owned) && owned != null)
        {
            blackboard.SetValue(DefibItemKey, owned.Value);
            return HTNOperatorStatus.Finished;
        }

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var source, _entManager))
            return HTNOperatorStatus.Failed;

        var isCabinet = blackboard.TryGetValue<bool>(IsCabinetKey, out var cab, _entManager) && cab;
        if (!medical.TryObtainDefib(owner, source, isCabinet))
            return HTNOperatorStatus.Failed;

        if (!medical.TryGetOwnedDefib(owner, out owned) || owned == null)
            return HTNOperatorStatus.Failed;

        blackboard.SetValue(DefibItemKey, owned.Value);
        return HTNOperatorStatus.Finished;
    }
}
