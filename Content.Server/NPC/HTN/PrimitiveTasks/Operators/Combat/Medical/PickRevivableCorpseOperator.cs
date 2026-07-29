using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Selects the best nearby revivable faction corpse as HealTarget.
/// </summary>
public sealed partial class PickRevivableCorpseOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string RangeKey = NPCMedicalSystem.MedSearchRangeKey;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var range = medical.GetMedSearchRange(blackboard);

        if (!medical.TryPickRevivableCorpse(owner, range, out var corpse))
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
