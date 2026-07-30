using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Illusion reload: Hidden DoAfter + SFX, then auto-refill / close bolt. No ammo loot.
/// </summary>
public sealed partial class ReloadHeldGunOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SharedDoAfterSystem _doAfter = default!;
    private NPCSteeringSystem _steering = default!;

    private const string CurrentDoAfterKey = "CurrentIllusionReload";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _doAfter = sysManager.GetEntitySystem<SharedDoAfterSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return (false, null);

        if (ammo.IsIllusionReloading(owner))
            return (true, null);

        var ev = new GetAmmoCountEvent();
        _entManager.EventBus.RaiseLocalEvent(gun, ref ev);
        // Plan succeeds when empty / nearly empty so combat tree can refill.
        return (ev.Capacity > 0 && ev.Count <= 0, null);
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
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _steering.Unregister(owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return HTNOperatorStatus.Failed;

        if (blackboard.TryGetValue<ushort>(CurrentDoAfterKey, out var trackedId, _entManager))
        {
            return _doAfter.GetStatus(owner, trackedId, null) switch
            {
                DoAfterStatus.Running => HTNOperatorStatus.Continuing,
                DoAfterStatus.Finished => HTNOperatorStatus.Finished,
                _ => HTNOperatorStatus.Failed,
            };
        }

        var ev = new GetAmmoCountEvent();
        _entManager.EventBus.RaiseLocalEvent(gun, ref ev);
        if (ev.Count > 0)
            return HTNOperatorStatus.Finished;

        if (ammo.IsIllusionReloading(owner) && ammo.TryGetActiveIllusionReload(owner, out var runningId))
        {
            blackboard.SetValue(CurrentDoAfterKey, runningId);
            return HTNOperatorStatus.Continuing;
        }

        ushort nextId = 0;
        DoAfterComponent? doAfterComp = null;
        if (_entManager.TryGetComponent(owner, out doAfterComp))
            nextId = doAfterComp.NextId;

        if (!ammo.TryStartIllusionReload(owner, gun))
            return HTNOperatorStatus.Failed;

        if (doAfterComp != null && nextId != doAfterComp.NextId)
        {
            blackboard.SetValue(CurrentDoAfterKey, nextId);
            return HTNOperatorStatus.Continuing;
        }

        // Instant DoAfter (tag / zero delay) — Apply already ran via event.
        var done = new GetAmmoCountEvent();
        _entManager.EventBus.RaiseLocalEvent(gun, ref done);
        return done.Count > 0 ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
