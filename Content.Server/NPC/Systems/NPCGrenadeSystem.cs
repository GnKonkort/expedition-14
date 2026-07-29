using Content.Shared.Explosion.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Handles grenade discovery, throw chance, activation and throw cooldowns for NPCs.
/// </summary>
public sealed class NPCGrenadeSystem : EntitySystem
{
    private static readonly ProtoId<TagPrototype> HandGrenadeTag = "HandGrenade";
    private static readonly TimeSpan MinWarmup = TimeSpan.FromSeconds(5f);

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;
    [Dependency] private readonly NPCSquadSystem _squads = default!;
    [Dependency] private readonly Content.Shared.NPC.Systems.NpcFactionSystem _faction = default!;

    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();
        _xformQuery = GetEntityQuery<TransformComponent>();
    }

    public bool IsGrenade(EntityUid uid)
    {
        return _tags.HasTag(uid, HandGrenadeTag) &&
               HasComp<OnUseTimerTriggerComponent>(uid) &&
               !HasComp<ActiveTimerTriggerComponent>(uid);
    }

    public void CollectNearbyGrenades(EntityUid owner, float range, HashSet<EntityUid> output)
    {
        var coords = _transform.GetMapCoordinates(owner);

        foreach (var ent in _lookup.GetEntitiesInRange(coords, range))
        {
            if (ent == owner)
                continue;

            if (!HasComp<ItemComponent>(ent))
                continue;

            if (!IsGrenade(ent))
                continue;

            output.Add(ent);
        }
    }

    public bool TryFindOwnedGrenade(EntityUid owner, out EntityUid grenade)
    {
        grenade = default;

        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (!IsGrenade(held))
                continue;

            grenade = held;
            return true;
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var slots))
            return false;

        while (slots.MoveNext(out var slot))
        {
            foreach (var ent in slot.ContainedEntities)
            {
                if (TryFindGrenadeRecursive(ent, out grenade))
                    return true;
            }
        }

        return false;
    }

    private bool TryFindGrenadeRecursive(EntityUid uid, out EntityUid grenade)
    {
        grenade = default;

        if (IsGrenade(uid))
        {
            grenade = uid;
            return true;
        }

        if (!_xformQuery.TryGetComponent(uid, out var xform))
            return false;

        var enumerator = xform.ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (TryFindGrenadeRecursive(child, out grenade))
                return true;
        }

        return false;
    }

    public bool IsThrowOpportunityReady(EntityUid owner, EntityUid target, NPCBlackboard blackboard)
    {
        if (!blackboard.TryGetValue<EntityUid>(NPCBlackboard.GrenadeCombatTarget, out var trackedTarget, EntityManager) ||
            trackedTarget != target)
        {
            blackboard.SetValue(NPCBlackboard.GrenadeCombatTarget, target);
            blackboard.SetValue(NPCBlackboard.GrenadeCombatStart, _timing.CurTime);
            return false;
        }

        if (!blackboard.TryGetValue<TimeSpan>(NPCBlackboard.GrenadeCombatStart, out var combatStart, EntityManager))
        {
            blackboard.SetValue(NPCBlackboard.GrenadeCombatStart, _timing.CurTime);
            return false;
        }

        return _timing.CurTime - combatStart >= MinWarmup;
    }

    public bool ShouldAttemptThrow(EntityUid owner, NPCBlackboard blackboard)
    {
        var now = _timing.CurTime;
        var progress = GetCooldownProgress(owner, blackboard, now);

        if (progress < 0.33f)
            return false;

        var scaled = (progress - 0.33f) / 0.67f;
        var chance = 0.12f + Math.Clamp(scaled, 0f, 1f) * 0.26f;
        return _random.Prob(chance);
    }

    public bool TryPrimeAndThrow(EntityUid owner, EntityUid grenade, EntityUid target)
    {
        if (!IsThrowSafe(owner, target))
            return false;

        if (!_ammo.TryObtainInHand(owner, grenade))
            return false;

        // Most hand grenades prime via UseInHand.
        _interaction.UseInHandInteraction(owner, grenade, checkCanUse: false, checkCanInteract: false, checkUseDelay: false);

        if (!HasComp<ActiveTimerTriggerComponent>(grenade))
            return false;

        if (!_hands.TryDrop(owner, grenade, checkActionBlocker: false, doDropInteraction: false))
            return false;

        if (!_xformQuery.TryGetComponent(target, out var targetXform))
            return false;

        _throwing.TryThrow(grenade, targetXform.Coordinates, baseThrowSpeed: 11.5f, user: owner, compensateFriction: true, recoil: false);
        return true;
    }

    /// <summary>
    /// Friendly-fire + minimum range gate before priming a grenade.
    /// </summary>
    public bool IsThrowSafe(EntityUid owner, EntityUid target, float blastRadius = 3.5f, float minRange = 3f)
    {
        if (!_xformQuery.TryGetComponent(owner, out var ownerXform) ||
            !_xformQuery.TryGetComponent(target, out var targetXform))
            return false;

        var ownerMap = _transform.GetMapCoordinates(owner, ownerXform);
        var targetMap = _transform.GetMapCoordinates(target, targetXform);
        if (ownerMap.MapId != targetMap.MapId)
            return false;

        var dist = (targetMap.Position - ownerMap.Position).Length();
        if (dist < minRange)
            return false;

        foreach (var friendly in _faction.GetNearbyFriendlies(owner, dist + blastRadius))
        {
            if (friendly == owner)
                continue;

            if (!_xformQuery.TryGetComponent(friendly, out var fx))
                continue;

            var fMap = _transform.GetMapCoordinates(friendly, fx);
            if (fMap.MapId != targetMap.MapId)
                continue;

            if ((fMap.Position - targetMap.Position).Length() <= blastRadius)
                return false;
        }

        return true;
    }

    public void ApplyThrowCooldown(EntityUid owner, NPCBlackboard blackboard)
    {
        var now = _timing.CurTime;
        var duration = TimeSpan.FromSeconds(_random.NextFloat(40f, 80f));
        var readyAt = now + duration;

        blackboard.SetValue(NPCBlackboard.GrenadeCooldownStart, now);
        blackboard.SetValue(NPCBlackboard.GrenadeCooldownEnd, readyAt);
        _squads.ApplySquadGrenadeCooldown(owner, readyAt);
    }

    private float GetCooldownProgress(EntityUid owner, NPCBlackboard blackboard, TimeSpan now)
    {
        var hasStart = blackboard.TryGetValue<TimeSpan>(NPCBlackboard.GrenadeCooldownStart, out var start, EntityManager);
        var hasEnd = blackboard.TryGetValue<TimeSpan>(NPCBlackboard.GrenadeCooldownEnd, out var end, EntityManager);
        var hasPersonal = hasStart && hasEnd;

        if (_squads.TryGetSquadGrenadeCooldown(owner, out var squadReadyAt) && squadReadyAt > TimeSpan.Zero)
        {
            if (!hasPersonal || squadReadyAt > end)
            {
                // Squad-shared CD without a personal window: block until ready.
                if (now < squadReadyAt)
                    return 0f;

                if (!hasPersonal)
                    return 1f;

                end = squadReadyAt;
            }
        }
        else if (!hasPersonal)
        {
            return 1f;
        }

        if (end <= start)
            return 1f;

        if (now >= end)
            return 1f;

        if (now <= start)
            return 0f;

        return (float) ((now - start).TotalSeconds / (end - start).TotalSeconds);
    }
}
