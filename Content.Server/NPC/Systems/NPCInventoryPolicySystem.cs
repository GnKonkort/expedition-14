using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Shared inventory policy layer — ammo/medical are clients of these loadout rules.
/// </summary>
public sealed class NPCInventoryPolicySystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;

    public bool TryGetPolicy(EntityUid owner, out NpcInventoryPolicyPrototype policy)
    {
        policy = default!;

        if (TryComp<HTNComponent>(owner, out var htn) &&
            htn.Blackboard.TryGetValue<string>(NPCBlackboard.InventoryPolicy, out var id, EntityManager) &&
            _proto.TryIndex(id, out NpcInventoryPolicyPrototype? fromBb))
        {
            policy = fromBb;
            return true;
        }

        if (TryComp<NPCRoleComponent>(owner, out var role) &&
            _proto.TryIndex(role.Profile, out NpcRoleProfilePrototype? profile) &&
            profile.InventoryPolicy is { } polId &&
            _proto.TryIndex(polId, out NpcInventoryPolicyPrototype? fromRole))
        {
            policy = fromRole;
            return true;
        }

        if (_proto.TryIndex<NpcInventoryPolicyPrototype>("CombatLoadout", out var fallback))
        {
            policy = fallback;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when PreferWeaponInHand would drop a non-gun held item under fire.
    /// </summary>
    public bool WouldEnforceHandPolicy(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!TryGetPolicy(owner, out var policy) || !policy.PreferWeaponInHand)
            return false;

        if (!blackboard.TryGetValue<bool>(NPCBlackboard.NeedHasHostile, out var hostile, EntityManager) || !hostile)
            return false;

        if (_ammo.TryGetHeldGun(owner, out _, out _))
            return false;

        if (!_ammo.TryGetOwnedGun(owner, out _, out _, blackboard))
            return false;

        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (HasComp<GunComponent>(held))
                continue;
            if (!HasComp<ItemComponent>(held))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drop non-weapon held junk when PreferWeaponInHand and a hostile is engaged.
    /// </summary>
    public bool TryEnforceHandPolicy(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!WouldEnforceHandPolicy(owner, blackboard))
            return false;

        if (!_ammo.TryGetOwnedGun(owner, out _, out _, blackboard))
            return false;

        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (HasComp<GunComponent>(held))
                continue;
            if (!HasComp<ItemComponent>(held))
                continue;

            _hands.TryDrop(owner, held, checkActionBlocker: false, doDropInteraction: false);
            return true;
        }

        return false;
    }
}
