using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Legacy no-op. Cover vaulting is handled by <see cref="MoveToOperator"/> after rubbing stuck.
/// Kept so old HTN YAML referencing this operator still loads.
/// </summary>
public sealed partial class ClimbCoverOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string CoverEntityKey = NPCCoverSystem.CoverEntityKey;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        return (blackboard.TryGetValue<EntityUid>(CoverEntityKey, out _, _entManager), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
