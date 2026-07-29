using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Applies inventory hand policy. Planning fails when there is nothing to do
/// so this branch cannot monopolize the HTN root under fire.
/// </summary>
public sealed partial class EnforceInventoryPolicyOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        // Dry-run: only accept the branch if a non-weapon hand item would be dropped.
        var inv = _entManager.System<NPCInventoryPolicySystem>();
        return inv.WouldEnforceHandPolicy(owner, blackboard) ? (true, null) : (false, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCInventoryPolicySystem>().TryEnforceHandPolicy(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
