using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Opportunistically primes and throws a grenade at the current hostile target.
/// </summary>
public sealed partial class ThrowGrenadeAtTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField("targetKey", required: true)]
    public string TargetKey = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager) ||
            !_entManager.EntityExists(target))
            return (false, null);

        var grenades = _entManager.System<NPCGrenadeSystem>();
        if (!grenades.TryFindOwnedGrenade(owner, out _))
            return (false, null);

        if (!grenades.IsThrowOpportunityReady(owner, target, blackboard))
            return (false, null);

        if (!grenades.IsThrowSafe(owner, target))
            return (false, null);

        if (!grenades.ShouldAttemptThrow(owner, blackboard))
            return (false, null);

        return (true, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager) ||
            !_entManager.EntityExists(target))
            return HTNOperatorStatus.Failed;

        var grenades = _entManager.System<NPCGrenadeSystem>();
        if (!grenades.TryFindOwnedGrenade(owner, out var grenade))
            return HTNOperatorStatus.Failed;

        if (!grenades.TryPrimeAndThrow(owner, grenade, target))
            return HTNOperatorStatus.Failed;

        grenades.ApplyThrowCooldown(owner, blackboard);
        return HTNOperatorStatus.Finished;
    }
}
