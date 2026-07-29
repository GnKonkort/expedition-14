using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Sets TargetCoordinates to HealTarget (e.g. after ObtainDefib overwrote them).
/// </summary>
public sealed partial class RetargetHealPatientOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return (false, null);

        if (!_entManager.EntityExists(patient))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetCoordinatesKey, new EntityCoordinates(patient, Vector2.Zero) },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return HTNOperatorStatus.Failed;

        if (!_entManager.EntityExists(patient))
            return HTNOperatorStatus.Failed;

        blackboard.SetValue(TargetCoordinatesKey, new EntityCoordinates(patient, Vector2.Zero));
        return HTNOperatorStatus.Finished;
    }
}
