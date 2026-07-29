using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Repairable;
using Content.Shared.Tools.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Engineer repair capability: find damaged <see cref="RepairableComponent"/> and weld it.
/// Cadence: plan-time. Worst-case: typed lookup in RepairRange.
/// </summary>
public sealed class NPCRepairSystem : EntitySystem
{
    public const float DefaultRepairRange = 8f;

    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;

    private EntityQuery<DamageableComponent> _damageQuery;
    private EntityQuery<RepairableComponent> _repairQuery;

    public override void Initialize()
    {
        base.Initialize();
        _damageQuery = GetEntityQuery<DamageableComponent>();
        _repairQuery = GetEntityQuery<RepairableComponent>();
    }

    public bool TryFindOwnedRepairTool(EntityUid owner, string quality, [NotNullWhen(true)] out EntityUid? tool)
    {
        tool = null;
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (_tools.HasQuality(held, quality))
            {
                tool = held;
                return true;
            }
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var slots))
            return false;

        while (slots.MoveNext(out var slot))
        {
            foreach (var ent in slot.ContainedEntities)
            {
                if (!_tools.HasQuality(ent, quality))
                    continue;
                tool = ent;
                return true;
            }
        }

        return false;
    }

    public bool TrySelectRepairTarget(EntityUid owner, NPCBlackboard blackboard, float range = DefaultRepairRange)
    {
        if (!TryComp(owner, out TransformComponent? xform))
            return false;

        EntityUid? best = null;
        EntityUid? bestTool = null;
        var bestDamage = 0f;
        var mapCoords = _transform.GetMapCoordinates(owner, xform);

        foreach (var ent in _lookup.GetEntitiesInRange<RepairableComponent>(mapCoords, range))
        {
            if (!_damageQuery.TryGetComponent(ent, out var damage) || damage.TotalDamage <= 0)
                continue;

            if (!_repairQuery.TryGetComponent(ent, out var repairable))
                continue;

            if (!TryFindOwnedRepairTool(owner, repairable.QualityNeeded, out var tool))
                continue;

            var total = damage.TotalDamage.Float();
            if (total <= bestDamage)
                continue;

            bestDamage = total;
            best = ent;
            bestTool = tool;
        }

        if (best == null || bestTool == null)
            return false;

        blackboard.SetValue(NPCBlackboard.RepairTarget, best.Value);
        blackboard.SetValue(NPCBlackboard.RepairTool, bestTool.Value);
        blackboard.SetValue(NPCBlackboard.Target, best.Value);
        if (TryComp(best.Value, out TransformComponent? tx))
            blackboard.SetValue(NPCBlackboard.TargetCoordinates, tx.Coordinates);
        return true;
    }

    public bool TryStartRepair(EntityUid owner, EntityUid target, EntityUid tool)
    {
        if (!_repairQuery.TryGetComponent(target, out var repairable))
            return false;

        if (!_damageQuery.TryGetComponent(target, out var damage) || damage.TotalDamage <= 0)
            return false;

        if (!_ammo.TryObtainInHand(owner, tool))
            return false;

        var delay = (float) repairable.DoAfterDelay;
        if (owner == target)
        {
            if (!repairable.AllowSelfRepair)
                return false;
            delay *= repairable.SelfRepairPenalty;
        }

        return _tools.UseTool(tool, owner, target, delay, repairable.QualityNeeded, new RepairFinishedEvent(), repairable.FuelCost);
    }
}
