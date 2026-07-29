using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Clothing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Medical;
using Content.Shared.Medical.Healing;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.Prying.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Priority inventory manager: score usable loot, understand nested containers,
/// drop low-value gear for space, upgrade backpacks, gate chemistry containers.
/// Cadence: HTN-driven (one action per operator tick). Cheap component checks only.
/// </summary>
public sealed class NPCInventoryManagerSystem : EntitySystem
{
    private const float LootRange = 4f;
    private const float BackpackScanRange = 5f;
    private const int NestedScoreDepth = 1;

    private static readonly ProtoId<TagPrototype> CrowbarTag = "Crowbar";

    public enum ItemClass : byte
    {
        None,
        Forbidden,
        Weapon,
        Ammo,
        MedKit,
        Medipen,
        Defib,
        Tool,
        Food,
        Drink,
        Internals,
        SurvivalKit,
        Backpack,
        MiscUsable,
    }

    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HungerSystem _hunger = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly NPCChemOntologySystem _chem = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;
    [Dependency] private readonly NPCInventoryPolicySystem _policy = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tag = default!;

    private EntityQuery<ItemComponent> _itemQuery;
    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<StorageComponent> _storageQuery;
    private EntityQuery<ClothingComponent> _clothingQuery;
    private EntityQuery<DefibrillatorComponent> _defibQuery;
    private EntityQuery<GasTankComponent> _tankQuery;
    private EntityQuery<MaskComponent> _maskQuery;
    private EntityQuery<FoodComponent> _foodQuery;
    private EntityQuery<DrinkComponent> _drinkQuery;
    private EntityQuery<SolutionContainerManagerComponent> _solutionQuery;
    private EntityQuery<HealingComponent> _healingQuery;
    private EntityQuery<HyposprayComponent> _hypoQuery;
    private EntityQuery<PryingComponent> _pryQuery;
    private EntityQuery<ToolComponent> _toolQuery;
    private EntityQuery<MeleeWeaponComponent> _meleeQuery;
    private EntityQuery<BallisticAmmoProviderComponent> _ballisticQuery;
    private EntityQuery<CartridgeAmmoComponent> _cartridgeQuery;

    public override void Initialize()
    {
        base.Initialize();
        _itemQuery = GetEntityQuery<ItemComponent>();
        _gunQuery = GetEntityQuery<GunComponent>();
        _storageQuery = GetEntityQuery<StorageComponent>();
        _clothingQuery = GetEntityQuery<ClothingComponent>();
        _defibQuery = GetEntityQuery<DefibrillatorComponent>();
        _tankQuery = GetEntityQuery<GasTankComponent>();
        _maskQuery = GetEntityQuery<MaskComponent>();
        _foodQuery = GetEntityQuery<FoodComponent>();
        _drinkQuery = GetEntityQuery<DrinkComponent>();
        _solutionQuery = GetEntityQuery<SolutionContainerManagerComponent>();
        _healingQuery = GetEntityQuery<HealingComponent>();
        _hypoQuery = GetEntityQuery<HyposprayComponent>();
        _pryQuery = GetEntityQuery<PryingComponent>();
        _toolQuery = GetEntityQuery<ToolComponent>();
        _meleeQuery = GetEntityQuery<MeleeWeaponComponent>();
        _ballisticQuery = GetEntityQuery<BallisticAmmoProviderComponent>();
        _cartridgeQuery = GetEntityQuery<CartridgeAmmoComponent>();
    }

    public bool NeedsInventoryManage(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!_policy.TryGetPolicy(owner, out var policy))
            return false;

        if (blackboard.TryGetValue<bool>(NPCBlackboard.NeedHasHostile, out var hostile, EntityManager) && hostile)
            return HasForbiddenOrExcess(owner, policy);

        if (policy.PreferDefib && !OwnsDefib(owner))
            return true;

        if (policy.PreferLargerBackpack && TryFindBetterBackpack(owner, BackpackScanRange, out _))
            return true;

        if (HasForbiddenOrExcess(owner, policy))
            return true;

        return TrySelectBestLoot(owner, blackboard, policy, LootRange, out _, out var score) && score > 1f;
    }

    public bool TryManageOnce(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!_policy.TryGetPolicy(owner, out var policy))
            return false;

        if (TryDropForbiddenOrExcess(owner, blackboard, policy))
            return true;

        var hostile = blackboard.TryGetValue<bool>(NPCBlackboard.NeedHasHostile, out var h, EntityManager) && h;
        if (hostile)
            return false;

        if (policy.PreferLargerBackpack && TryUpgradeBackpack(owner))
            return true;

        if (!TrySelectBestLoot(owner, blackboard, policy, LootRange, out var loot, out var lootScore))
            return false;

        if (lootScore <= 0f)
            return false;

        var lootUid = loot.Value;

        if (!_ammo.TryObtainInHand(owner, lootUid) && !_hands.IsHolding(owner, lootUid))
        {
            if (!TryMakeSpaceFor(owner, blackboard, policy, lootScore))
                return false;

            if (!_ammo.TryObtainInHand(owner, lootUid))
                return false;
        }

        if (_ammo.TryStowItem(owner, lootUid))
            return true;

        if (TryMakeSpaceFor(owner, blackboard, policy, lootScore) && _ammo.TryStowItem(owner, lootUid))
            return true;

        return _hands.IsHolding(owner, lootUid);
    }

    public bool TrySelectBestLoot(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        float range,
        [NotNullWhen(true)] out EntityUid? best,
        out float bestScore)
    {
        best = null;
        bestScore = 0f;

        if (!TryGetMapCoords(owner, out var mapCoords))
            return false;

        foreach (var ent in _lookup.GetEntitiesInRange<ItemComponent>(mapCoords, range))
        {
            if (_containers.IsEntityInContainer(ent))
                continue;

            if (ent.Owner == owner)
                continue;

            var score = ScoreItem(owner, blackboard, policy, ent.Owner, NestedScoreDepth);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = ent.Owner;
        }

        return best != null;
    }

    public float ScoreItem(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        EntityUid item,
        int nestedDepth)
    {
        var kind = Classify(owner, policy, item);
        if (kind == ItemClass.Forbidden)
            return -1000f;

        var baseScore = BaseWeight(policy, kind);
        if (kind == ItemClass.None)
            baseScore = 0f;

        if (nestedDepth > 0 && _storageQuery.HasComponent(item))
        {
            var nestedBest = 0f;
            foreach (var inner in EnumerateDirectStorage(item))
            {
                nestedBest = Math.Max(nestedBest,
                    ScoreItem(owner, blackboard, policy, inner, nestedDepth - 1) * 0.9f);
            }

            if (kind is ItemClass.SurvivalKit or ItemClass.None or ItemClass.MiscUsable)
                baseScore = Math.Max(baseScore, nestedBest);
            else
                baseScore = Math.Max(baseScore, baseScore * 0.5f + nestedBest * 0.5f);
        }

        if (baseScore <= 0f)
            return 0f;

        return baseScore * SituationMultiplier(owner, blackboard, policy, kind);
    }

    public ItemClass Classify(EntityUid owner, NpcInventoryPolicyPrototype policy, EntityUid item)
    {
        if (!_itemQuery.HasComponent(item))
            return ItemClass.None;

        if (IsReagentContainer(item) && !policy.AllowReagentContainers)
            return ItemClass.Forbidden;

        if (_gunQuery.HasComponent(item))
            return ItemClass.Weapon;

        if (_defibQuery.HasComponent(item))
            return ItemClass.Defib;

        if (_healingQuery.HasComponent(item))
            return ItemClass.MedKit;

        if (_hypoQuery.HasComponent(item))
        {
            if (!_chem.TryGetProfile(item, out _))
                return policy.AllowReagentContainers ? ItemClass.Medipen : ItemClass.Forbidden;

            if (!_chem.IsAllowedByKnowledge(owner, item))
                return ItemClass.Forbidden;

            return ItemClass.Medipen;
        }

        if (_tankQuery.HasComponent(item) || _maskQuery.HasComponent(item))
            return ItemClass.Internals;

        if (_foodQuery.HasComponent(item))
            return ItemClass.Food;

        if (_drinkQuery.HasComponent(item))
            return ItemClass.Drink;

        if (_ballisticQuery.HasComponent(item) ||
            _cartridgeQuery.HasComponent(item) ||
            HasComp<MagazineAmmoProviderComponent>(item) ||
            HasComp<PowerCellComponent>(item))
            return ItemClass.Ammo;

        if (_pryQuery.HasComponent(item) || _toolQuery.HasComponent(item) || _tag.HasTag(item, CrowbarTag))
            return ItemClass.Tool;

        if (IsBackpack(item))
            return ItemClass.Backpack;

        if (_storageQuery.HasComponent(item))
        {
            foreach (var inner in EnumerateDirectStorage(item))
            {
                var innerKind = Classify(owner, policy, inner);
                if (innerKind is ItemClass.Internals or ItemClass.Food or ItemClass.Drink
                    or ItemClass.MedKit or ItemClass.Medipen)
                    return ItemClass.SurvivalKit;
            }

            return ItemClass.MiscUsable;
        }

        if (_meleeQuery.HasComponent(item))
            return ItemClass.Tool;

        return ItemClass.None;
    }

    private static float BaseWeight(NpcInventoryPolicyPrototype policy, ItemClass kind) => kind switch
    {
        ItemClass.Weapon => policy.WeightWeapon,
        ItemClass.Ammo => policy.WeightAmmo,
        ItemClass.MedKit => policy.WeightMedKit,
        ItemClass.Medipen => policy.WeightMedipen,
        ItemClass.Defib => policy.WeightDefib,
        ItemClass.Tool => policy.WeightTool,
        ItemClass.Food => policy.WeightFood,
        ItemClass.Drink => policy.WeightDrink,
        ItemClass.Internals => policy.WeightInternals,
        ItemClass.SurvivalKit => policy.WeightSurvivalKit,
        ItemClass.Backpack => policy.WeightBackpackUpgrade,
        ItemClass.MiscUsable => 10f,
        _ => 0f,
    };

    private float SituationMultiplier(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        ItemClass kind)
    {
        var mult = 1f;
        var hostile = blackboard.TryGetValue<bool>(NPCBlackboard.NeedHasHostile, out var h, EntityManager) && h;
        var vacuum = blackboard.TryGetValue<bool>(NPCBlackboard.NeedInVacuum, out var v, EntityManager) && v;
        var lowPressure = blackboard.TryGetValue<bool>(NPCBlackboard.NeedLowPressure, out var lp, EntityManager) && lp;
        var medLow = blackboard.TryGetValue<bool>(NPCBlackboard.NeedMedStockLow, out var ml, EntityManager) && ml;
        var ammoCrit = blackboard.TryGetValue<bool>(NPCBlackboard.NeedAmmoCritical, out var ac, EntityManager) && ac;

        if (vacuum || lowPressure)
        {
            if (kind is ItemClass.Internals or ItemClass.SurvivalKit)
                mult *= 4f;
        }

        if (hostile)
        {
            if (kind is ItemClass.Weapon or ItemClass.Ammo)
                mult *= 2.5f;
            if (kind is ItemClass.Food or ItemClass.Drink or ItemClass.Backpack)
                mult *= 0.15f;
            if (kind is ItemClass.MedKit or ItemClass.Medipen)
                mult *= 1.4f;
        }

        if (medLow && kind is ItemClass.MedKit or ItemClass.Medipen)
            mult *= 2f;

        if (ammoCrit && kind == ItemClass.Ammo)
            mult *= 3f;

        if (policy.PreferDefib && kind == ItemClass.Defib && !OwnsDefib(owner))
            mult *= 3f;

        if (TryComp(owner, out HungerComponent? hunger) &&
            _hunger.GetHungerThreshold(hunger) <= HungerThreshold.Peckish &&
            kind is ItemClass.Food or ItemClass.SurvivalKit)
            mult *= 3f;

        if (TryComp(owner, out ThirstComponent? thirst) &&
            thirst.CurrentThirstThreshold <= ThirstThreshold.Thirsty &&
            kind is ItemClass.Drink or ItemClass.SurvivalKit)
            mult *= 3f;

        return mult;
    }

    private bool TryMakeSpaceFor(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        float lootScore)
    {
        if (!TryFindLowestDroppable(owner, blackboard, policy, out var drop, out var dropScore))
            return false;

        if (lootScore < dropScore + policy.ReplaceMargin)
            return false;

        return TryDropItem(owner, drop);
    }

    private bool TryFindLowestDroppable(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        out EntityUid item,
        out float score)
    {
        item = default;
        score = float.MaxValue;
        var found = false;

        foreach (var candidate in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (IsProtected(owner, policy, candidate))
                continue;

            var s = ScoreItem(owner, blackboard, policy, candidate, NestedScoreDepth);
            if (s >= score)
                continue;

            score = s;
            item = candidate;
            found = true;
        }

        return found;
    }

    private bool IsProtected(EntityUid owner, NpcInventoryPolicyPrototype policy, EntityUid item)
    {
        if (_gunQuery.HasComponent(item))
        {
            var guns = 0;
            foreach (var _ in _ammo.EnumerateCarriedGuns(owner))
            {
                guns++;
                if (guns > 1)
                    break;
            }

            if (guns <= 1)
                return true;
        }

        if (policy.PreferDefib && _defibQuery.HasComponent(item))
            return true;

        if (_inventory.TryGetContainingSlot(item, out _) && _clothingQuery.HasComponent(item))
            return true;

        return false;
    }

    private bool HasForbiddenOrExcess(EntityUid owner, NpcInventoryPolicyPrototype policy)
    {
        CountKinds(owner, policy, out var kits, out var pens, out var looseAmmo, out var forbidden);
        return forbidden ||
               kits > policy.MaxKitStacks ||
               pens > policy.MaxMedipens ||
               looseAmmo > policy.MaxLooseAmmoStacks;
    }

    private bool TryDropForbiddenOrExcess(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy)
    {
        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (Classify(owner, policy, item) != ItemClass.Forbidden)
                continue;
            if (IsProtected(owner, policy, item))
                continue;
            return TryDropItem(owner, item);
        }

        CountKinds(owner, policy, out var kits, out var pens, out var looseAmmo, out _);

        if (kits > policy.MaxKitStacks)
            return TryDropOneOfClass(owner, blackboard, policy, ItemClass.MedKit);

        if (pens > policy.MaxMedipens)
            return TryDropOneOfClass(owner, blackboard, policy, ItemClass.Medipen);

        if (looseAmmo > policy.MaxLooseAmmoStacks)
            return TryDropOneOfClass(owner, blackboard, policy, ItemClass.Ammo, looseOnly: true);

        return false;
    }

    private bool TryDropOneOfClass(
        EntityUid owner,
        NPCBlackboard blackboard,
        NpcInventoryPolicyPrototype policy,
        ItemClass kind,
        bool looseOnly = false)
    {
        EntityUid? worst = null;
        var worstScore = float.MaxValue;

        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (Classify(owner, policy, item) != kind)
                continue;
            if (looseOnly && !IsLoose(item))
                continue;
            if (IsProtected(owner, policy, item))
                continue;

            var s = ScoreItem(owner, blackboard, policy, item, 0);
            if (s >= worstScore)
                continue;
            worstScore = s;
            worst = item;
        }

        return worst != null && TryDropItem(owner, worst.Value);
    }

    private void CountKinds(
        EntityUid owner,
        NpcInventoryPolicyPrototype policy,
        out int kits,
        out int pens,
        out int looseAmmo,
        out bool forbidden)
    {
        kits = 0;
        pens = 0;
        looseAmmo = 0;
        forbidden = false;

        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            switch (Classify(owner, policy, item))
            {
                case ItemClass.Forbidden:
                    forbidden = true;
                    break;
                case ItemClass.MedKit:
                    kits++;
                    break;
                case ItemClass.Medipen:
                    pens++;
                    break;
                case ItemClass.Ammo when IsLoose(item):
                    looseAmmo++;
                    break;
            }
        }
    }

    private bool IsLoose(EntityUid item)
    {
        if (!_containers.TryGetContainingContainer(item, out var container))
            return true;

        return !_storageQuery.HasComponent(container.Owner);
    }

    private bool TryDropItem(EntityUid owner, EntityUid item)
    {
        if (_hands.IsHolding(owner, item))
            return _hands.TryDrop(owner, item, checkActionBlocker: false, doDropInteraction: false);

        if (!_ammo.TryObtainInHand(owner, item))
            return false;

        return _hands.TryDrop(owner, item, checkActionBlocker: false, doDropInteraction: false);
    }

    public bool OwnsDefib(EntityUid owner)
    {
        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (_defibQuery.HasComponent(item))
                return true;
        }

        return false;
    }

    private bool IsBackpack(EntityUid item)
    {
        return _clothingQuery.TryGetComponent(item, out var clothing) &&
               clothing.Slots.HasFlag(SlotFlags.BACK) &&
               _storageQuery.HasComponent(item);
    }

    private int GetStorageArea(EntityUid uid)
    {
        return _storageQuery.TryGetComponent(uid, out var storage) ? storage.Grid.GetArea() : 0;
    }

    public bool TryFindBetterBackpack(EntityUid owner, float range, [NotNullWhen(true)] out EntityUid? better)
    {
        better = null;
        var currentArea = 0;
        if (_inventory.TryGetSlotEntity(owner, "back", out var current) &&
            _storageQuery.HasComponent(current.Value))
        {
            currentArea = GetStorageArea(current.Value);
        }

        if (!TryGetMapCoords(owner, out var mapCoords))
            return false;

        EntityUid? best = null;
        var bestArea = currentArea;

        foreach (var ent in _lookup.GetEntitiesInRange<ClothingComponent>(mapCoords, range))
        {
            if (_containers.IsEntityInContainer(ent))
                continue;

            if (!IsBackpack(ent))
                continue;

            var area = GetStorageArea(ent);
            if (area <= bestArea)
                continue;

            bestArea = area;
            best = ent.Owner;
        }

        better = best;
        return better != null;
    }

    public bool TryUpgradeBackpack(EntityUid owner)
    {
        if (!TryFindBetterBackpack(owner, BackpackScanRange, out var better))
            return false;

        _inventory.TryGetSlotEntity(owner, "back", out var oldBack);

        if (oldBack != null &&
            !_inventory.TryUnequip(owner, "back", silent: true, force: true))
            return false;

        if (!_ammo.TryObtainInHand(owner, better.Value) && !_hands.IsHolding(owner, better.Value))
        {
            if (oldBack != null)
                _inventory.TryEquip(owner, oldBack.Value, "back", silent: true, force: true);
            return false;
        }

        if (!_inventory.TryEquip(owner, better.Value, "back", silent: true, force: true))
        {
            _ammo.TryStowItem(owner, better.Value);
            if (oldBack != null)
                _inventory.TryEquip(owner, oldBack.Value, "back", silent: true, force: true);
            return false;
        }

        if (oldBack != null && _storageQuery.HasComponent(oldBack.Value))
        {
            var toMove = new List<EntityUid>();
            foreach (var inner in EnumerateDirectStorage(oldBack.Value))
                toMove.Add(inner);

            var dropCoords = _transform.GetMoverCoordinates(owner);
            foreach (var inner in toMove)
            {
                if (_storage.Insert(better.Value, inner, out _, user: owner, playSound: false))
                    continue;

                if (_containers.TryGetContainingContainer(inner, out var cont))
                    _containers.Remove(inner, cont);

                _transform.SetCoordinates(inner, dropCoords);
            }

            if (!_ammo.TryStowItem(owner, oldBack.Value))
                TryDropItem(owner, oldBack.Value);
        }

        return true;
    }

    private bool IsReagentContainer(EntityUid item)
    {
        return _solutionQuery.HasComponent(item) &&
               !_healingQuery.HasComponent(item) &&
               !_hypoQuery.HasComponent(item) &&
               !_foodQuery.HasComponent(item) &&
               !_drinkQuery.HasComponent(item) &&
               !_tankQuery.HasComponent(item);
    }

    private IEnumerable<EntityUid> EnumerateDirectStorage(EntityUid storageUid)
    {
        if (!_storageQuery.TryGetComponent(storageUid, out var storage))
            yield break;

        var seen = new HashSet<EntityUid>();
        foreach (var uid in storage.StoredItems.Keys)
        {
            if (seen.Add(uid))
                yield return uid;
        }

        if (_containers.TryGetContainer(storageUid, StorageComponent.ContainerId, out var container))
        {
            foreach (var uid in container.ContainedEntities)
            {
                if (seen.Add(uid))
                    yield return uid;
            }
        }
    }

    private bool TryGetMapCoords(EntityUid owner, out MapCoordinates mapCoords)
    {
        mapCoords = default;
        if (!TryComp(owner, out TransformComponent? xform))
            return false;

        mapCoords = _transform.GetMapCoordinates(owner, xform);
        return mapCoords.MapId != MapId.Nullspace;
    }
}
