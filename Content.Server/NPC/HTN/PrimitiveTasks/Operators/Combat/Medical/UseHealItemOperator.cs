using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Obtains HealItem and uses it on HealTarget (medipen inject or kit UseInHand / interact).
/// Waits on kit do-after; unregisters steering so BreakOnMove does not cancel healing.
/// </summary>
public sealed partial class UseHealItemOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedDoAfterSystem _doAfter = default!;
    private NPCSteeringSystem _steering = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public string HealItemKey = NPCBlackboard.HealItem;

    [DataField]
    public bool StowAfter = true;

    private const string CurrentDoAfterKey = "CurrentHealDoAfter";

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

        if (!StowAfter)
            return;

        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // Stow cancels NeedHand do-afters (BreakOnHandChange). Keep the kit out while bandaging,
        // and when ConstantlyReplan replaces us with a "better" combat plan.
        if (status == HTNOperatorStatus.BetterPlan || medical.IsHealingDoAfterRunning(owner))
            return;

        if (blackboard.TryGetValue<EntityUid>(HealItemKey, out var item, _entManager))
            medical.TryStowHealItem(owner, item);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // Already bandaging — stand still and wait (do not restart UseInHand).
        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            HoldStill(owner);
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                DoAfterStatus.Finished => HTNOperatorStatus.Finished,
                _ => HTNOperatorStatus.Failed,
            };
        }

        if (medical.TryGetActiveHealingDoAfter(owner, out var existingId))
        {
            HoldStill(owner);
            blackboard.SetValue(CurrentDoAfterKey, existingId);
            medical.Debug(owner, "UseHeal adopt existing heal doAfter");
            return HTNOperatorStatus.Continuing;
        }

        ushort nextId = 0;
        DoAfterComponent? doAfterComp = null;
        if (_entManager.TryGetComponent(owner, out doAfterComp))
            nextId = doAfterComp.NextId;

        if (!blackboard.TryGetValue<EntityUid>(HealItemKey, out var item, _entManager) ||
            !blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
        {
            medical.Debug(owner, "UseHeal FAIL missing HealItem/HealTarget");
            return HTNOperatorStatus.Failed;
        }

        // Kits cancel on move — clear steering before starting the do-after.
        HoldStill(owner);

        if (!medical.TryUseHealItem(owner, patient, item))
            return HTNOperatorStatus.Failed;

        // Store the id that was assigned (pre-increment snapshot), same as InteractWithOperator.
        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            medical.Debug(owner, $"UseHeal doAfter started id={nextId}");
            return HTNOperatorStatus.Continuing;
        }

        if (medical.TryGetActiveHealingDoAfter(owner, out var startedId))
        {
            blackboard.SetValue(CurrentDoAfterKey, startedId);
            medical.Debug(owner, $"UseHeal doAfter found id={startedId}");
            return HTNOperatorStatus.Continuing;
        }

        // Medipen / instant path
        return HTNOperatorStatus.Finished;
    }

    private void HoldStill(EntityUid owner)
    {
        _steering.Unregister(owner);
    }
}
