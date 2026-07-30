using System.Threading;
using System.Threading.Tasks;
using Content.Server.DoAfter;
using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;
using Content.Shared.NPC;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Finds a blocking door and opens / C4s / emags it (fixed priority, role-gated).
/// C4 and emag use a short progress-bar DoAfter + SFX illusion.
/// </summary>
public sealed partial class BypassDoorOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedDoAfterSystem _doAfter = default!;
    private SharedAudioSystem _audio = default!;
    private NPCSteeringSystem _steering = default!;

    private const string CurrentDoAfterKey = "CurrentBypassDoAfter";
    private const string PendingResultKey = "PendingBypassResult";

    private static readonly SoundSpecifier BreachSound = new SoundPathSpecifier("/Audio/Weapons/Guns/Triggers/empty.ogg");
    private static readonly SoundSpecifier HackSound = new SoundPathSpecifier("/Audio/Machines/terminal_insert_disc.ogg");

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _doAfter = sysManager.GetEntitySystem<SharedDoAfterSystem>();
        _audio = sysManager.GetEntitySystem<SharedAudioSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var doors = _entManager.System<NPCAccessBypassSystem>();
        if (!doors.TryFindBlockingDoor(owner, out var door))
            return (false, null);

        if (doors.Evaluate(owner, door) == NPCAccessBypassSystem.DoorHandleResult.None)
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { NPCBlackboard.BypassDoorTarget, door },
        });
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        blackboard.Remove<ushort>(CurrentDoAfterKey);
        blackboard.Remove<byte>(PendingResultKey);
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        blackboard.Remove<ushort>(CurrentDoAfterKey);
        blackboard.Remove<byte>(PendingResultKey);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var doors = _entManager.System<NPCAccessBypassSystem>();
        _steering.Unregister(owner);

        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                DoAfterStatus.Finished => FinishPending(blackboard, doors, owner),
                _ => HTNOperatorStatus.Failed,
            };
        }

        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.BypassDoorTarget, out var door, _entManager))
        {
            if (!doors.TrySelectBypassDoor(owner, blackboard))
                return HTNOperatorStatus.Failed;
            door = blackboard.GetValue<EntityUid>(NPCBlackboard.BypassDoorTarget);
        }

        var result = doors.Evaluate(owner, door);
        if (result == NPCAccessBypassSystem.DoorHandleResult.None)
            return HTNOperatorStatus.Failed;

        // Instant open when allowed.
        if (result == NPCAccessBypassSystem.DoorHandleResult.OpenAccess)
            return doors.TryBypass(owner, door) ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;

        // C4 / emag: progress bar + SFX, then apply.
        var duration = result == NPCAccessBypassSystem.DoorHandleResult.BreachExplosive ? 2.5f : 1.75f;
        var sound = result == NPCAccessBypassSystem.DoorHandleResult.BreachExplosive ? BreachSound : HackSound;
        _audio.PlayPvs(sound, owner);

        ushort nextId = 0;
        DoAfterComponent? doAfterComp = null;
        if (_entManager.TryGetComponent(owner, out doAfterComp))
            nextId = doAfterComp.NextId;

        var args = new DoAfterArgs(_entManager, owner, duration, new NpcSimpleDoTaskDoAfterEvent(), owner, target: door)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (!_doAfter.TryStartDoAfter(args))
            return HTNOperatorStatus.Failed;

        blackboard.SetValue(PendingResultKey, (byte) result);

        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            return HTNOperatorStatus.Continuing;
        }

        return FinishPending(blackboard, doors, owner);
    }

    private HTNOperatorStatus FinishPending(NPCBlackboard blackboard, NPCAccessBypassSystem doors, EntityUid owner)
    {
        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.BypassDoorTarget, out var door, _entManager))
            return HTNOperatorStatus.Failed;

        if (!blackboard.TryGetValue<byte>(PendingResultKey, out var resultByte, _entManager))
            return HTNOperatorStatus.Failed;

        var result = (NPCAccessBypassSystem.DoorHandleResult) resultByte;
        blackboard.Remove<ushort>(CurrentDoAfterKey);
        blackboard.Remove<byte>(PendingResultKey);

        return doors.TryHandle(owner, door, result, out _)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
