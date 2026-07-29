using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Selects self or the worst-hurt nearby faction ally as HealTarget.
/// </summary>
public sealed partial class PickHealTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Self;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string RangeKey = NPCMedicalSystem.MedSearchRangeKey;

    /// <summary>
    /// When true, only pick critical allies (for EmergencyMedipen).
    /// </summary>
    [DataField]
    public bool CritOnly;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (Self)
        {
            if (!medical.NeedsHeal(owner, owner))
                return (false, null);

            return (true, new Dictionary<string, object>
            {
                { HealTargetKey, owner },
            });
        }

        var range = medical.GetMedSearchRange(blackboard);
        if (!medical.TryPickHealAlly(owner, range, out var ally, CritOnly))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { HealTargetKey, ally.Value },
            { TargetCoordinatesKey, new EntityCoordinates(ally.Value, Vector2.Zero) },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
