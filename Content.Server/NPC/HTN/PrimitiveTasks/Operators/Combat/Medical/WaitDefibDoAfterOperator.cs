using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Stands still until the active defibrillator zap do-after finishes.
/// </summary>
public sealed partial class WaitDefibDoAfterOperator : HTNOperator
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

        if (!medical.IsDefibDoAfterRunning(owner))
            return HTNOperatorStatus.Finished;

        return HTNOperatorStatus.Continuing;
    }
}
