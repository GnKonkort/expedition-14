using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.HTN;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Standing;
using Content.Shared.Stunnable;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Security arrest capability: cuff downed / stunned / crit hostiles.
/// Cadence: plan-time. Worst-case: typed lookup + inventory cuff scan.
/// </summary>
public sealed class NPCArrestSystem : EntitySystem
{
    public const float DefaultArrestRange = 6f;

    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SharedCuffableSystem _cuffs = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;

    private EntityQuery<CuffableComponent> _cuffableQuery;
    private EntityQuery<MobStateComponent> _mobQuery;

    public override void Initialize()
    {
        base.Initialize();
        _cuffableQuery = GetEntityQuery<CuffableComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
    }

    public bool TryFindOwnedCuffs(EntityUid owner, [NotNullWhen(true)] out EntityUid? cuffs)
    {
        cuffs = null;
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (HasComp<HandcuffComponent>(held))
            {
                cuffs = held;
                return true;
            }
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var slots))
            return false;

        while (slots.MoveNext(out var slot))
        {
            foreach (var ent in slot.ContainedEntities)
            {
                if (!HasComp<HandcuffComponent>(ent))
                    continue;
                cuffs = ent;
                return true;
            }
        }

        return false;
    }

    public bool IsArrestable(EntityUid officer, EntityUid target)
    {
        if (officer == target)
            return false;

        if (!_cuffableQuery.TryGetComponent(target, out var cuffable))
            return false;

        if (_cuffs.IsCuffed((target, cuffable)))
            return false;

        if (!_mobQuery.HasComponent(target) || _mobState.IsDead(target))
            return false;

        // Only detain hostiles / non-friendlies.
        if (_faction.IsEntityFriendly(officer, target))
            return false;

        if (_mobState.IsCritical(target))
            return true;

        if (HasComp<StunnedComponent>(target))
            return true;

        if (TryComp<StandingStateComponent>(target, out var standing) && !standing.Standing)
            return true;

        return false;
    }

    public bool TrySelectArrestTarget(EntityUid officer, NPCBlackboard blackboard, float range = DefaultArrestRange)
    {
        if (!TryFindOwnedCuffs(officer, out var cuffs))
            return false;

        if (!TryComp(officer, out TransformComponent? xform))
            return false;

        EntityUid? best = null;
        var bestDist = float.MaxValue;
        var mapCoords = _transform.GetMapCoordinates(officer, xform);

        foreach (var ent in _lookup.GetEntitiesInRange<CuffableComponent>(mapCoords, range))
        {
            if (!IsArrestable(officer, ent.Owner))
                continue;

            var dist = (_transform.GetMapCoordinates(ent.Owner).Position - mapCoords.Position).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = ent.Owner;
        }

        if (best == null)
            return false;

        blackboard.SetValue(NPCBlackboard.ArrestTarget, best.Value);
        blackboard.SetValue(NPCBlackboard.ArrestCuffs, cuffs.Value);
        blackboard.SetValue(NPCBlackboard.Target, best.Value);
        if (TryComp(best.Value, out TransformComponent? tx))
            blackboard.SetValue(NPCBlackboard.TargetCoordinates, tx.Coordinates);
        return true;
    }

    public bool TryStartArrest(EntityUid officer, EntityUid target, EntityUid cuffs)
    {
        if (!IsArrestable(officer, target))
            return false;

        if (!_ammo.TryObtainInHand(officer, cuffs))
            return false;

        return _cuffs.TryCuffing(officer, target, cuffs);
    }
}
