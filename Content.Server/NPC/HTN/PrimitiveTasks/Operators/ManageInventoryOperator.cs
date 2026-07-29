using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// One inventory logistics step: drop junk, upgrade backpack, or pick up useful loot.
/// </summary>
public sealed partial class ManageInventoryOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var inv = _entManager.System<NPCInventoryManagerSystem>();
        return (inv.NeedsInventoryManage(owner, blackboard), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var inv = _entManager.System<NPCInventoryManagerSystem>();
        return inv.TryManageOnce(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
