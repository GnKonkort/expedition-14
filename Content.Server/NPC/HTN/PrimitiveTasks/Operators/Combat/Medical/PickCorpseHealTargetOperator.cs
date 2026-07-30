using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Picks a faction corpse that still needs healing before defibrillation.
/// </summary>
public sealed partial class PickCorpseHealTargetOperator : HTNOperator
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
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var range = medical.GetMedSearchRange(blackboard);

        if (!medical.TryPickCorpseNeedingHeal(owner, range, out var corpse) || corpse == null)
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { HealTargetKey, corpse.Value },
            { TargetCoordinatesKey, new EntityCoordinates(corpse.Value, Vector2.Zero) },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
