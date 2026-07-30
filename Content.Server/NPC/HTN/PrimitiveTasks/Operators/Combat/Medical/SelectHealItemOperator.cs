using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Selects the best owned kit/medipen for HealTarget.
/// </summary>
public sealed partial class SelectHealItemOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string HealItemKey = NPCBlackboard.HealItem;

    [DataField]
    public bool AllowKits = true;

    [DataField]
    public bool AllowMedipens = true;

    [DataField]
    public bool CritAllyPensOnly;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return (false, null);

        if (!medical.TrySelectBestOwnedHeal(
                owner,
                patient,
                AllowKits,
                AllowMedipens,
                out var item,
                CritAllyPensOnly) ||
            item == null)
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { HealItemKey, item.Value },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
