using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Finds a blocking door and attempts access / hack / pry / breach.
/// Fails closed in planning when no bypass method is available.
/// </summary>
public sealed partial class BypassDoorOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var doors = _entManager.System<NPCAccessBypassSystem>();
        if (!doors.TryFindBlockingDoor(owner, out var door))
            return (false, null);

        if (doors.Evaluate(owner, door) == NPCAccessBypassSystem.DoorHandleResult.None)
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { NPCBlackboard.BypassDoorTarget, door },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var doors = _entManager.System<NPCAccessBypassSystem>();

        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.BypassDoorTarget, out var door, _entManager))
        {
            if (!doors.TrySelectBypassDoor(owner, blackboard))
                return HTNOperatorStatus.Failed;
            door = blackboard.GetValue<EntityUid>(NPCBlackboard.BypassDoorTarget);
        }

        return doors.TryBypass(owner, door) ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
