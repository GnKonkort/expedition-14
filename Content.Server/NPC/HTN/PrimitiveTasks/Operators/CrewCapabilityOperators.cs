using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

public sealed partial class PickRepairTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var repair = _entManager.System<NPCRepairSystem>();
        if (!repair.TrySelectRepairTarget(owner, blackboard))
            return (false, null);

        return (true, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCRepairSystem>().TrySelectRepairTarget(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}

public sealed partial class RepairTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.RepairTarget, out var target, _entManager) ||
            !blackboard.TryGetValue<EntityUid>(NPCBlackboard.RepairTool, out var tool, _entManager))
            return HTNOperatorStatus.Failed;

        return _entManager.System<NPCRepairSystem>().TryStartRepair(owner, target, tool)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}

public sealed partial class PickTriagePatientOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCTriageSystem>().TrySelectTriagePatient(owner, blackboard)
            ? (true, null)
            : (false, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCTriageSystem>().TrySelectTriagePatient(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}

public sealed partial class PickArrestTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCArrestSystem>().TrySelectArrestTarget(owner, blackboard)
            ? (true, null)
            : (false, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCArrestSystem>().TrySelectArrestTarget(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}

public sealed partial class ArrestTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.ArrestTarget, out var target, _entManager) ||
            !blackboard.TryGetValue<EntityUid>(NPCBlackboard.ArrestCuffs, out var cuffs, _entManager))
            return HTNOperatorStatus.Failed;

        return _entManager.System<NPCArrestSystem>().TryStartArrest(owner, target, cuffs)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
