using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Selects nearest airtight tile for AtmosFlee and stores TargetCoordinates.
/// Fails closed in planning when no safe tile exists (so combat is not starved).
/// </summary>
public sealed partial class PickAtmosFleeOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var atmos = _entManager.System<NPCAtmosFleeSystem>();
        if (!atmos.TryFindSafeCoordinates(owner, out var coords))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { NPCBlackboard.AtmosSafeCoordinates, coords },
            { NPCBlackboard.TargetCoordinates, coords },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.System<NPCAtmosFleeSystem>().TrySelectFleeTarget(owner, blackboard)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
