using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when inventory logistics should run (useful loot, excess, bag upgrade, missing defib).
/// </summary>
public sealed partial class InventoryManageNeededPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCInventoryManagerSystem>().NeedsInventoryManage(owner, blackboard);
    }
}
