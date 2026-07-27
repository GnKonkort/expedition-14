using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Copies CoverCoordinates onto TargetCoordinates for the final MoveTo onto the barricade stand.
/// </summary>
public sealed partial class SetCoverStandTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string CoverCoordinatesKey = NPCCoverSystem.CoverCoordinatesKey;

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(CoverCoordinatesKey, out var stand, _entManager))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetCoordinatesKey, stand },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(CoverCoordinatesKey, out var stand, _entManager))
            return HTNOperatorStatus.Failed;

        blackboard.SetValue(TargetCoordinatesKey, stand);
        // Drop the approach path so MoveTo requests a fresh path from the flank to the rear stand.
        blackboard.Remove<Content.Server.NPC.Pathfinding.PathResultEvent>("TargetPathfind");

        return HTNOperatorStatus.Finished;
    }
}
