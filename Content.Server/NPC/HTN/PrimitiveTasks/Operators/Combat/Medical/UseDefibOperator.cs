using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Powers on a defibrillator and starts a zap do-after on HealTarget.
/// </summary>
public sealed partial class UseDefibOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedDoAfterSystem _doAfter = default!;
    private NPCSteeringSystem _steering = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string DefibItemKey = NPCBlackboard.DefibItem;

    private const string CurrentDoAfterKey = "CurrentDefibDoAfter";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _doAfter = sysManager.GetEntitySystem<SharedDoAfterSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        blackboard.Remove<ushort>(CurrentDoAfterKey);
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        blackboard.Remove<ushort>(CurrentDoAfterKey);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            _steering.Unregister(owner);
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                DoAfterStatus.Finished => HTNOperatorStatus.Finished,
                _ => HTNOperatorStatus.Failed,
            };
        }

        if (medical.TryGetActiveDefibDoAfter(owner, out var existingId))
        {
            _steering.Unregister(owner);
            blackboard.SetValue(CurrentDoAfterKey, existingId);
            return HTNOperatorStatus.Continuing;
        }

        if (!blackboard.TryGetValue<EntityUid>(DefibItemKey, out var defib, _entManager))
        {
            if (!medical.TryGetOwnedDefib(owner, out var owned) || owned == null)
                return HTNOperatorStatus.Failed;
            defib = owned.Value;
            blackboard.SetValue(DefibItemKey, defib);
        }

        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return HTNOperatorStatus.Failed;

        ushort nextId = 0;
        DoAfterComponent? doAfterComp = null;
        if (_entManager.TryGetComponent(owner, out doAfterComp))
            nextId = doAfterComp.NextId;

        _steering.Unregister(owner);

        if (!medical.TryStartDefibZap(owner, patient, defib))
            return HTNOperatorStatus.Failed;

        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            return HTNOperatorStatus.Continuing;
        }

        if (medical.TryGetActiveDefibDoAfter(owner, out var startedId))
        {
            blackboard.SetValue(CurrentDoAfterKey, startedId);
            return HTNOperatorStatus.Continuing;
        }

        return HTNOperatorStatus.Finished;
    }
}
