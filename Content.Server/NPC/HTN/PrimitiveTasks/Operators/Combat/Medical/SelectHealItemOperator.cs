using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Picks the best owned kit/medipen for HealTarget (or self) and stores it as HealItem.
/// </summary>
public sealed partial class SelectHealItemOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Self = true;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string HealItemKey = NPCBlackboard.HealItem;

    [DataField]
    public bool AllowKits = true;

    [DataField]
    public bool AllowMedipens = true;

    /// <summary>
    /// When true, only Emergency-style pens that require a critical ally are considered.
    /// </summary>
    [DataField]
    public bool CritAllyPensOnly;

    [DataField]
    public bool AllowDead;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        EntityUid patient = owner;
        if (!Self)
        {
            if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out patient, _entManager))
                return (false, null);
        }

        if (!medical.TrySelectBestOwnedHeal(owner, patient, AllowKits, AllowMedipens, out var item, CritAllyPensOnly, AllowDead))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { HealItemKey, item.Value },
            { HealTargetKey, patient },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
