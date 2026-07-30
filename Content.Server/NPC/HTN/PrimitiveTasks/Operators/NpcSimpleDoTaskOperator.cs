using System.IO;
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
/// Generic role task: stand still, play SFX, run a DoAfter progress bar, then finish.
/// Concrete effects (defib/door/repair) are handled by specialized operators; this is the illusion shell.
/// </summary>
public sealed partial class NpcSimpleDoTaskOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedAudioSystem _audio = default!;
    private SharedDoAfterSystem _doAfter = default!;
    private NPCSteeringSystem _steering = default!;

    [DataField]
    public float Duration = 2.5f;

    [DataField]
    public SoundSpecifier? Sound = new SoundPathSpecifier("/Audio/Items/drill_use.ogg");

    private const string CurrentDoAfterKey = "CurrentSimpleDoTask";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _audio = sysManager.GetEntitySystem<SharedAudioSystem>();
        _doAfter = sysManager.GetEntitySystem<SharedDoAfterSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        return (true, null);
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
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _steering.Unregister(owner);

        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                DoAfterStatus.Finished => HTNOperatorStatus.Finished,
                _ => HTNOperatorStatus.Failed,
            };
        }

        if (Sound != null)
        {
            try
            {
                _audio.PlayPvs(Sound, owner);
            }
            catch (FileNotFoundException)
            {
                // Missing audio metadata must not kill the server mid-HTN.
            }
        }

        var args = new DoAfterArgs(_entManager, owner, Duration, new NpcSimpleDoTaskDoAfterEvent(), owner)
        {
            Hidden = false,
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
        };

        ushort nextId = 0;
        if (_entManager.TryGetComponent(owner, out DoAfterComponent? doAfterComp))
            nextId = doAfterComp.NextId;

        if (!_doAfter.TryStartDoAfter(args))
            return HTNOperatorStatus.Failed;

        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            return HTNOperatorStatus.Continuing;
        }

        return HTNOperatorStatus.Finished;
    }
}
