using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;
using Content.Shared.NPC;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using System.IO;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Visible DoAfter, then remove <see cref="NPCMedicalSystem.IllusionHealAmount"/> damage from HealTarget.
/// </summary>
public sealed partial class IllusionHealOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedAudioSystem _audio = default!;
    private SharedDoAfterSystem _doAfter = default!;
    private NPCSteeringSystem _steering = default!;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public float Duration = 2f;

    [DataField]
    public SoundSpecifier? Sound = new SoundPathSpecifier("/Audio/Items/Medical/brutepack_begin.ogg");

    /// <summary>When true, only corpses that still need prep for defib.</summary>
    [DataField]
    public bool CorpsePrep;

    private const string CurrentDoAfterKey = "CurrentIllusionHeal";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _audio = sysManager.GetEntitySystem<SharedAudioSystem>();
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
        _steering.Unregister(owner);

        // Healing does not need the defib in-hand — keep it bagged.
        medical.TryStowHeldDefib(owner);

        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return HTNOperatorStatus.Failed;

        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                // Heal applied by NPCMedicalSystem on DoAfter event.
                DoAfterStatus.Finished => HTNOperatorStatus.Finished,
                DoAfterStatus.Invalid => HTNOperatorStatus.Finished,
                _ => HTNOperatorStatus.Failed,
            };
        }

        if (CorpsePrep)
        {
            if (!medical.NeedsCorpseHeal(owner, patient))
                return HTNOperatorStatus.Finished;
        }
        else if (!medical.NeedsLivingHeal(owner, patient))
        {
            return HTNOperatorStatus.Finished;
        }

        if (Sound != null)
        {
            try
            {
                _audio.PlayPvs(Sound, owner);
            }
            catch (FileNotFoundException)
            {
            }
        }

        EnsureCompDoAfter(owner, out var doAfterComp, out var nextId);

        var args = new DoAfterArgs(_entManager, owner, Duration, new NpcIllusionHealDoAfterEvent(), owner, target: patient)
        {
            Broadcast = true,
            Hidden = false,
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            CancelDuplicate = true,
            DuplicateCondition = DuplicateConditions.SameEvent,
        };

        if (!_doAfter.TryStartDoAfter(args))
            return HTNOperatorStatus.Failed;

        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            return HTNOperatorStatus.Continuing;
        }

        // Instant DoAfter — event already applied heal.
        return HTNOperatorStatus.Finished;
    }

    private void EnsureCompDoAfter(EntityUid owner, out DoAfterComponent? comp, out ushort nextId)
    {
        nextId = 0;
        comp = null;
        if (_entManager.TryGetComponent(owner, out DoAfterComponent? existing))
        {
            comp = existing;
            nextId = existing.NextId;
            return;
        }

        comp = _entManager.EnsureComponent<DoAfterComponent>(owner);
        nextId = comp.NextId;
    }
}
