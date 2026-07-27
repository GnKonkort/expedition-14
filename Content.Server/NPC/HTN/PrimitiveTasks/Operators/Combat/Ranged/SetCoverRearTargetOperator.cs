using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Sets TargetCoordinates to the rear (approach) tile behind a directional barricade.
/// </summary>
public sealed partial class SetCoverRearTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string CoverApproachCoordinatesKey = NPCCoverSystem.CoverApproachCoordinatesKey;

    [DataField]
    public string CoverRearCoordinatesKey = NPCCoverSystem.CoverRearCoordinatesKey;

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!TryGetRear(blackboard, out var rear))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetCoordinatesKey, rear },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!TryGetRear(blackboard, out var rear))
            return HTNOperatorStatus.Failed;

        blackboard.SetValue(TargetCoordinatesKey, rear);
        blackboard.Remove<Content.Server.NPC.Pathfinding.PathResultEvent>("TargetPathfind");

        return HTNOperatorStatus.Finished;
    }

    private bool TryGetRear(NPCBlackboard blackboard, out EntityCoordinates rear)
    {
        if (blackboard.TryGetValue<EntityCoordinates>(CoverRearCoordinatesKey, out rear, _entManager))
            return true;

        return blackboard.TryGetValue<EntityCoordinates>(CoverApproachCoordinatesKey, out rear, _entManager);
    }
}
