using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Audio;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

public sealed partial class GunOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField("shutdownState")]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    /// <summary>
    /// Key that contains the target entity.
    /// </summary>
    [DataField("targetKey", required: true)]
    public string TargetKey = default!;

    /// <summary>
    /// Minimum damage state that the target has to be in for us to consider attacking.
    /// </summary>
    [DataField("targetState")]
    public MobState TargetState = MobState.Alive;

    /// <summary>
    /// Do we require line of sight of the target before failing.
    /// </summary>
    [DataField("requireLOS")]
    public bool RequireLOS = false;

    /// <summary>
    /// If true, only opaque objects will block line of sight.
    /// </summary>
    [DataField("opaqueKey")]
    public bool UseOpaqueForLOSChecks = false;

    /// <summary>
    /// Hold soft cover: stand still until the enemy enters melee range or the cover is destroyed.
    /// </summary>
    [DataField]
    public bool HoldCover;

    [DataField]
    public string CoverEntityKey = NPCCoverSystem.CoverEntityKey;

    [DataField]
    public string CoverCoordinatesKey = NPCCoverSystem.CoverCoordinatesKey;

    [DataField]
    public string MeleeRangeKey = "MeleeRange";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        // Don't attack if they're already as wounded as we want them.
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
        {
            return (false, null);
        }

        if (_entManager.TryGetComponent<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState > TargetState)
        {
            return (false, null);
        }

        return (true, null);
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var ammo = _entManager.System<NPCGunAmmoSystem>();
        ammo.NoteCombatActivity(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            // Clear charge-wait when resuming fire with a full gun (Plan cannot mutate read-only BB).
            ammo.TryClearEnergyChargeWaitIfReady(gun, blackboard);
            ammo.TryEnsureWielded(owner, gun);
        }

        var ranged = _entManager.EnsureComponent<NPCRangedCombatComponent>(owner);
        ranged.Target = blackboard.GetValue<EntityUid>(TargetKey);
        ranged.UseOpaqueForLOSChecks = UseOpaqueForLOSChecks;
        ranged.StayPut = HoldCover;

        if (blackboard.TryGetValue<float>(NPCBlackboard.RotateSpeed, out var rotSpeed, _entManager))
        {
            ranged.RotationSpeed = new Angle(rotSpeed);
        }

        if (blackboard.TryGetValue<SoundSpecifier>("SoundTargetInLOS", out var losSound, _entManager))
        {
            ranged.SoundTargetInLOS = losSound;
        }
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _entManager.System<SharedCombatModeSystem>().SetInCombatMode(owner, false);
        _entManager.RemoveComponent<NPCRangedCombatComponent>(owner);
        blackboard.Remove<EntityUid>(TargetKey);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        HTNOperatorStatus status;

        if (_entManager.TryGetComponent<NPCRangedCombatComponent>(owner, out var combat) &&
            blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
        {
            combat.Target = target;
            combat.StayPut = HoldCover;
            _entManager.System<NPCGunAmmoSystem>().NoteCombatActivity(blackboard);

            if (HoldCover && ShouldLeaveCover(blackboard, owner, target))
            {
                status = HTNOperatorStatus.Failed;
            }
            // Success
            else if (_entManager.TryGetComponent<MobStateComponent>(combat.Target, out var mobState) &&
                mobState.CurrentState > TargetState)
            {
                status = HTNOperatorStatus.Finished;
            }
            else
            {
                switch (combat.Status)
                {
                    case CombatStatus.TargetUnreachable:
                        status = HTNOperatorStatus.Failed;
                        break;
                    case CombatStatus.NotInSight:
                        if (RequireLOS)
                            status = HTNOperatorStatus.Failed;
                        else
                            status = HTNOperatorStatus.Continuing;
                        break;
                    case CombatStatus.Normal:
                        status = HTNOperatorStatus.Continuing;
                        break;
                    default:
                        status = HTNOperatorStatus.Failed;
                        break;
                }
            }
        }
        else
        {
            status = HTNOperatorStatus.Failed;
        }

        // Mark it as finished to continue the plan.
        if (status == HTNOperatorStatus.Continuing && ShutdownState == HTNPlanState.PlanFinished)
        {
            status = HTNOperatorStatus.Finished;
        }

        return status;
    }

    private bool ShouldLeaveCover(NPCBlackboard blackboard, EntityUid owner, EntityUid target)
    {
        var coverSys = _entManager.System<NPCCoverSystem>();

        if (!blackboard.TryGetValue<EntityUid>(CoverEntityKey, out var cover, _entManager))
            return true;

        var meleeRange = blackboard.GetValueOrDefault<float>(MeleeRangeKey, _entManager);
        if (meleeRange <= 0f)
            meleeRange = 1f;

        if (coverSys.ShouldAbandonCover(owner, target, cover, meleeRange))
            return true;

        // Drifted off cover (face side / far from stand) — replan.
        if (blackboard.TryGetValue<EntityCoordinates>(CoverCoordinatesKey, out var stand, _entManager) &&
            !coverSys.IsInCoverPosition(owner, cover, stand))
            return true;

        return false;
    }
}
