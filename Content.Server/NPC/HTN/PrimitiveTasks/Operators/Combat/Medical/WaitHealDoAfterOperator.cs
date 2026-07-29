using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Stands still until the active kit <see cref="HealingDoAfterEvent"/> finishes.
/// Highest-priority compound uses this so ConstantlyReplan cannot yank the NPC into combat mid-bandage.
/// </summary>
public sealed partial class WaitHealDoAfterOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private NPCSteeringSystem _steering = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        _steering.Unregister(owner);

        if (!medical.TryGetActiveHealingDoAfter(owner, out _))
            return HTNOperatorStatus.Finished;

        return HTNOperatorStatus.Continuing;
    }
}
