using Content.Server.NPC.Components;
using Content.Shared.CombatMode;
using Content.Shared.Cover;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random; //Frontier

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;

    private EntityQuery<CombatModeComponent> _combatQuery;
    private EntityQuery<NPCSteeringComponent> _steeringQuery;
    private EntityQuery<RechargeBasicEntityAmmoComponent> _rechargeQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<DirectionalCoverComponent> _directionalCoverQuery;
    private EntityQuery<ProbabilisticCoverComponent> _probCoverQuery;

    // TODO: Don't predict for hitscan
    private const float ShootSpeed = 20f;

    /// <summary>
    /// Cooldown on raycasting to check LOS.
    /// </summary>
    public const float UnoccludedCooldown = 0.2f;

    private void InitializeRanged()
    {
        _combatQuery = GetEntityQuery<CombatModeComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _rechargeQuery = GetEntityQuery<RechargeBasicEntityAmmoComponent>();
        _steeringQuery = GetEntityQuery<NPCSteeringComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _directionalCoverQuery = GetEntityQuery<DirectionalCoverComponent>();
        _probCoverQuery = GetEntityQuery<ProbabilisticCoverComponent>();

        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentStartup>(OnRangedStartup);
        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentShutdown>(OnRangedShutdown);
    }

    private void OnRangedStartup(EntityUid uid, NPCRangedCombatComponent component, ComponentStartup args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, true, combat);
        }
        else
        {
            component.Status = CombatStatus.Unspecified;
        }
    }

    private void OnRangedShutdown(EntityUid uid, NPCRangedCombatComponent component, ComponentShutdown args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, false, combat);
        }
    }

    private void UpdateRanged(float frameTime)
    {
        var query = EntityQueryEnumerator<NPCRangedCombatComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.Status == CombatStatus.Unspecified)
                continue;

            if (!comp.StayPut &&
                _steeringQuery.TryGetComponent(uid, out var steering) &&
                steering.Status == SteeringStatus.NoPath)
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (!_xformQuery.TryGetComponent(comp.Target, out var targetXform) ||
                !_physicsQuery.TryGetComponent(comp.Target, out var targetBody))
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (targetXform.MapID != xform.MapID)
            {
                comp.Status = CombatStatus.TargetUnreachable;
                comp.ShootAccumulator = 0f;
                continue;
            }

            if (_combatQuery.TryGetComponent(uid, out var combatMode))
            {
                _combat.SetInCombatMode(uid, true, combatMode);
            }

            // Any held gun (not only active hand) — then select it so AttemptShoot works.
            EntityUid gunUid;
            GunComponent? gun;
            if (_gunAmmo.TryGetHeldGun(uid, out gunUid, out gun))
            {
                _hands.TrySelect(uid, gunUid);
            }
            else if (!_gun.TryGetGun(uid, out gunUid, out gun))
            {
                comp.Status = CombatStatus.NoWeapon;
                comp.ShootAccumulator = 0f;
                continue;
            }

            // Rifles/shotguns with GunRequiresWield need both hands occupied.
            _gunAmmo.TryEnsureWielded(uid, gunUid);

            // Chamber-mag guns open the bolt when empty — close/rack before ammo checks.
            _gunAmmo.EnsureChamberReady(gunUid, uid);

            var ammoEv = new GetAmmoCountEvent();
            RaiseLocalEvent(gunUid, ref ammoEv);

            if (ammoEv.Count == 0)
            {
                // Recharging then?
                if (_rechargeQuery.HasComponent(gunUid))
                {
                    continue;
                }

                // Empty: start/wait Hidden DoAfter illusion reload (refill + bolt on finish).
                // Keep Status=Normal so GunOperator does not Fail on Unspecified.
                if (_gunAmmo.IsIllusionReloading(uid) || _gunAmmo.TryStartIllusionReload(uid, gunUid))
                {
                    comp.ShootAccumulator = 0f;
                    continue;
                }

                comp.ShootAccumulator = 0f;
                continue;
            }

            comp.LOSAccumulator -= frameTime;

            var worldPos = _transform.GetWorldPosition(xform);
            var targetPos = _transform.GetWorldPosition(targetXform);

            // Frontier -- Ranged NPC miss chance
            if (_random.Prob(comp.MissChance))
            {
                targetPos = targetPos + _random.NextVector2(1.0f, 2.0f);
            }
            // End Frontier

            // We'll work out the projected spot of the target and shoot there instead of where they are.
            var distance = (targetPos - worldPos).Length();
            var oldInLos = comp.TargetInLOS;

            // TODO: Should be doing these raycasts in parallel
            // Ideally we'd have 2 steps, 1. to go over the normal details for shooting and then 2. to handle beep / rotate / shoot
            if (comp.LOSAccumulator < 0f)
            {
                comp.LOSAccumulator += UnoccludedCooldown;

                // For consistency with NPC steering.
                var collisionGroup = comp.UseOpaqueForLOSChecks ? CollisionGroup.Opaque : (CollisionGroup.Impassable | CollisionGroup.InteractImpassable);
                // Soft cover is always shoot-through for aim LOS (Opaque is only for bolt physics).
                // Side-dependent block chance must not prevent trying to fire.
                SharedInteractionSystem.Ignored softCoverPredicate = IsSoftCoverEntity;
                comp.TargetInLOS = _interaction.InRangeUnobstructed(
                    (uid, xform),
                    (comp.Target, targetXform),
                    distance + 0.1f,
                    collisionGroup,
                    softCoverPredicate);
            }

            if (!comp.TargetInLOS)
            {
                comp.ShootAccumulator = 0f;
                comp.Status = CombatStatus.NotInSight;

                // Holding cover: stay put instead of twitching toward the target.
                if (!comp.StayPut && _steeringQuery.TryGetComponent(uid, out var moveSteering))
                {
                    moveSteering.ForceMove = true;
                }

                continue;
            }

            if (!oldInLos && comp.SoundTargetInLOS != null)
            {
                _audio.PlayPvs(comp.SoundTargetInLOS, uid);
            }

            comp.ShootAccumulator += frameTime;

            if (comp.ShootAccumulator < comp.ShootDelay)
            {
                continue;
            }

            var mapVelocity = targetBody.LinearVelocity;
            var targetSpot = targetPos + mapVelocity * distance / ShootSpeed;

            // If we have a max rotation speed then do that.
            var goalRotation = (targetSpot - worldPos).ToWorldAngle();
            var rotationSpeed = comp.RotationSpeed;

            if (!_rotate.TryRotateTo(uid, goalRotation, frameTime, comp.AccuracyThreshold, rotationSpeed?.Theta ?? double.MaxValue, xform))
            {
                continue;
            }

            // TODO: LOS
            // TODO: Ammo checks
            // TODO: Burst fire
            // TODO: Cycling
            // Max rotation speed

            // TODO: Check if we can face

            if (!Enabled || !_gun.CanShoot(gun))
                continue;

            EntityCoordinates targetCordinates;

            if (_mapManager.TryFindGridAt(xform.MapID, targetPos, out var gridUid, out var mapGrid))
            {
                targetCordinates = new EntityCoordinates(gridUid, _map.WorldToLocal(gridUid, mapGrid, targetSpot));
            }
            else
            {
                targetCordinates = new EntityCoordinates(xform.MapUid!.Value, targetSpot);
            }

            comp.Status = CombatStatus.Normal;

            if (gun.NextFire > _timing.CurTime)
            {
                return;
            }

            // Squadmates in the shot line are ignored by projectile/hitscan FF rules
            // (NPCSquadFriendlyFireSystem + GunSystem hitscan skip) — still fire.
            _gun.AttemptShoot(uid, gunUid, gun, targetCordinates, comp.Target);
        }
    }

    /// <summary>
    /// Soft cover (cades / tables) is Opaque for projectile physics only — always ignore for aim LOS.
    /// </summary>
    private bool IsSoftCoverEntity(EntityUid hit)
    {
        return _directionalCoverQuery.HasComponent(hit) || _probCoverQuery.HasComponent(hit);
    }
}
