using System.Numerics;
using Content.Client.Inventory;
using Content.Shared._Arcane.ERP;
using Content.Shared._Arcane.ERP.Clothing;
using Content.Shared._Arcane.ERP.Organs;
using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Arcane.ERP.OrgansAppearance;

public sealed class ErpOrganVisualsSystem : EntitySystem
{
    private const string BreastLayerKey = "erp_breasts";
    private const string BreastLeftLayerKey = "erp_breasts_L";
    private const string BreastRightLayerKey = "erp_breasts_R";

    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;

    private readonly Dictionary<(string slot, string species), ErpOrganVisualPrototype> _speciesLookup = new();
    private readonly Dictionary<string, ErpOrganVisualPrototype> _fallbackLookup = new();
    private readonly Dictionary<string, string> _slotToLayerKey = new();
    private readonly Dictionary<string, int> _slotDrawOrder = new();
    private readonly List<string> _orderedSlots = new();
    private readonly Dictionary<EntityUid, ErpOrganPreferences> _previewPrefs = new();

    public override void Initialize()
    {
        base.Initialize();

        BuildLookupTables();
        _proto.PrototypesReloaded += _ => BuildLookupTables();

        SubscribeLocalEvent<ErpOrganVisualsComponent, AfterAutoHandleStateEvent>(OnOrganState);
        SubscribeLocalEvent<ErpOrganVisualsComponent, ComponentShutdown>(OnOrganShutdown);
        SubscribeLocalEvent<HumanoidAppearanceComponent, HumanoidAppearanceUpdatedEvent>(OnHumanoidUpdated);
        SubscribeLocalEvent<HumanoidAppearanceComponent, HumanoidProfileLoadedEvent>(OnPreviewProfileLoaded);
    }

    private void BuildLookupTables()
    {
        _speciesLookup.Clear();
        _fallbackLookup.Clear();
        _slotToLayerKey.Clear();
        _slotDrawOrder.Clear();
        _orderedSlots.Clear();

        foreach (var proto in _proto.EnumeratePrototypes<ErpOrganVisualPrototype>())
        {
            _slotToLayerKey.TryAdd(proto.Slot, proto.LayerKey);
            _slotDrawOrder.TryAdd(proto.Slot, proto.DrawOrder);
            if (!_orderedSlots.Contains(proto.Slot))
                _orderedSlots.Add(proto.Slot);

            if (proto.Species.Count == 0)
                _fallbackLookup[proto.Slot] = proto;
            else
            {
                foreach (var species in proto.Species)
                    _speciesLookup[(proto.Slot, species)] = proto;
            }
        }

        _orderedSlots.Sort(CompareSlotsByDrawOrder);
    }

    private ErpOrganVisualPrototype? GetProto(string slot, string species)
    {
        if (_speciesLookup.TryGetValue((slot, species), out var proto))
            return proto;

        _fallbackLookup.TryGetValue(slot, out var fallback);
        return fallback;
    }

    public void RefreshPreview(EntityUid uid, ErpOrganPreferences prefs, ArousalPhase phase = ArousalPhase.Calm)
    {
        if (!IsClientSide(uid))
            return;

        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        _previewPrefs[uid] = prefs;

        var humanoid = CompOrNull<HumanoidAppearanceComponent>(uid);
        var visuals = EnsureComp<ErpOrganVisualsComponent>(uid);
        visuals.Organs = ErpOrganVisualsHelpers.BuildOrgansFromPrefs(
            prefs,
            humanoid?.Species,
            humanoid?.Sex ?? Sex.Male,
            _proto,
            _componentFactory);
        visuals.BreastBounce = BreastBouncePreferences.Normalize(prefs.BreastBounce);
        visuals.CoveredSlots = GetPreviewCoveredSlots(uid);
        visuals.HideWhenFlaccid = GetPreviewHideWhenFlaccid(uid);

        ApplyOrganLayers((uid, visuals), humanoid, sprite, phase);
    }

    private void OnPreviewProfileLoaded(Entity<HumanoidAppearanceComponent> ent, ref HumanoidProfileLoadedEvent args)
    {
        if (!IsClientSide(ent))
            return;

        // Editor definitions come from SpeciesPrototype.Prototype (playable mob), not the doll.
        var organPrefs = _previewPrefs.GetValueOrDefault(ent, args.Profile.ErpOrgans);
        var visuals = EnsureComp<ErpOrganVisualsComponent>(ent);
        visuals.Organs = ErpOrganVisualsHelpers.BuildOrgansFromPrefs(
            organPrefs,
            ent.Comp.Species,
            ent.Comp.Sex,
            _proto,
            _componentFactory);
        visuals.BreastBounce = BreastBouncePreferences.Normalize(organPrefs.BreastBounce);
        visuals.CoveredSlots = GetPreviewCoveredSlots(ent);
        visuals.HideWhenFlaccid = GetPreviewHideWhenFlaccid(ent);

        if (TryComp<SpriteComponent>(ent, out var sprite))
            ApplyOrganLayers((ent, visuals), ent.Comp, sprite);
    }

    private HashSet<string> GetPreviewHideWhenFlaccid(EntityUid uid)
        => TryComp<EroticOrgansComponent>(uid, out var organs) ? new HashSet<string>(organs.HideWhenFlaccid) : [];

    private HashSet<string> GetPreviewCoveredSlots(EntityUid uid)
    {
        var coveredLayers = new HashSet<HumanoidVisualLayers>();
        var enumerator = _inventory.GetSlotEnumerator(uid);
        while (enumerator.NextItem(out var item))
        {
            if (!TryComp<HideLayerClothingComponent>(item, out var hideLayer) ||
                !TryComp<ClothingComponent>(item, out var clothing))
            {
                continue;
            }

            ErpOrganVisualsHelpers.CollectCoveredLayersFromClothing(
                hideLayer,
                clothing,
                IsHideLayerEnabled(item, hideLayer),
                coveredLayers);
        }

        return ErpOrganVisualsHelpers.GetCoveredSlotsFromLayers(coveredLayers);
    }

    private bool IsHideLayerEnabled(EntityUid uid, HideLayerClothingComponent hideLayer)
    {
        if (!hideLayer.HideOnToggle)
            return true;

        if (!TryComp<MaskComponent>(uid, out var mask))
            return true;

        return !mask.IsToggled;
    }

    private void OnOrganState(Entity<ErpOrganVisualsComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        ApplyOrganLayers(ent, CompOrNull<HumanoidAppearanceComponent>(ent), sprite);
    }

    private void OnHumanoidUpdated(Entity<HumanoidAppearanceComponent> ent, ref HumanoidAppearanceUpdatedEvent args)
    {
        if (!TryComp<ErpOrganVisualsComponent>(ent, out var visuals))
            return;

        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        ApplyOrganLayers((ent, visuals), ent.Comp, sprite);
    }

    private void OnOrganShutdown(Entity<ErpOrganVisualsComponent> ent, ref ComponentShutdown args)
    {
        _previewPrefs.Remove(ent);

        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        RemoveOrganLayers(ent, sprite);
    }

    private void ApplyOrganLayers(
        Entity<ErpOrganVisualsComponent> ent,
        HumanoidAppearanceComponent? humanoid,
        SpriteComponent sprite,
        ArousalPhase? phaseOverride = null)
    {
        var phase = phaseOverride ?? ArousalPhase.Calm;
        var species = humanoid?.Species.Id ?? string.Empty;

        if (!OrganLayerOrderMatches(ent, sprite))
            RemoveOrganLayers(ent, sprite);

        foreach (var slotId in _orderedSlots)
        {
            if (!_slotToLayerKey.TryGetValue(slotId, out var layerKey))
                continue;

            var proto = GetProto(slotId, species);
            if (proto == null)
            {
                if (_sprite.LayerMapTryGet((ent, sprite), layerKey, out var staleIdx, false))
                {
                    _sprite.LayerSetVisible((ent, sprite), staleIdx, false);
                    _sprite.LayerMapRemove((ent, sprite), layerKey);
                    _sprite.RemoveLayer((ent, sprite), staleIdx);
                }

                continue;
            }

            if (!ent.Comp.Organs.TryGetValue(slotId, out var cfg))
            {
                if (_sprite.LayerMapTryGet((ent, sprite), layerKey, out var hiddenIdx, false))
                    _sprite.LayerSetVisible((ent, sprite), hiddenIdx, false);
                continue;
            }

            var rsiPath = proto.Rsi;
            var stateName = ResolveStateName(proto, cfg, phase);
            var visible = !ent.Comp.CoveredSlots.Contains(slotId)
                          && (!ent.Comp.HideWhenFlaccid.Contains(slotId) || phase >= ArousalPhase.Aroused);
            var color = cfg.Color ?? humanoid?.SkinColor ?? Color.FromHex("#C0967F");

            if (slotId == ErpOrganSlots.Breasts)
            {
                ApplyBreastLayers(ent, sprite, slotId, rsiPath, stateName, visible, color);
                continue;
            }

            if (!_sprite.LayerMapTryGet((ent, sprite), layerKey, out var index, false))
            {
                index = _sprite.AddLayer(
                    (ent, sprite),
                    new SpriteSpecifier.Rsi(new ResPath(rsiPath), stateName),
                    GetOrganLayerInsertIndex(ent, sprite, slotId));
                _sprite.LayerMapSet((ent, sprite), layerKey, index);
            }

            _sprite.LayerSetRsi((ent, sprite), index, new ResPath(rsiPath), stateName);
            _sprite.LayerSetColor((ent, sprite), index, color);
            _sprite.LayerSetVisible((ent, sprite), index, visible);
        }

        var updated = new ErpOrganVisualsUpdatedEvent();
        RaiseLocalEvent(ent.Owner, ref updated);
    }

    private void ApplyBreastLayers(
        Entity<ErpOrganVisualsComponent> ent,
        SpriteComponent sprite,
        string slotId,
        string rsiPath,
        string stateName,
        bool visible,
        Color color)
    {
        // Single combined sprite for now. Dual L/R layers return when dedicated assets exist.
        RemoveMappedLayer(ent, sprite, BreastLeftLayerKey);
        RemoveMappedLayer(ent, sprite, BreastRightLayerKey);
        EnsureMappedLayer(ent, sprite, slotId, BreastLayerKey, new ResPath(rsiPath), stateName, color, visible, null);
    }

    private void RemoveMappedLayer(Entity<ErpOrganVisualsComponent> ent, SpriteComponent sprite, string layerKey)
    {
        if (!_sprite.LayerMapTryGet((ent, sprite), layerKey, out var idx, false))
            return;

        _sprite.LayerSetVisible((ent, sprite), idx, false);
        _sprite.LayerMapRemove((ent, sprite), layerKey);
        _sprite.RemoveLayer((ent, sprite), idx);
    }

    private void EnsureMappedLayer(
        Entity<ErpOrganVisualsComponent> ent,
        SpriteComponent sprite,
        string slotId,
        string layerKey,
        ResPath rsiPath,
        string stateName,
        Color color,
        bool visible,
        int? forcedInsertIndex)
    {
        if (!_sprite.LayerMapTryGet((ent, sprite), layerKey, out var index, false))
        {
            var insert = forcedInsertIndex ?? GetOrganLayerInsertIndex(ent, sprite, slotId);
            index = _sprite.AddLayer(
                (ent, sprite),
                new SpriteSpecifier.Rsi(rsiPath, stateName),
                insert);
            _sprite.LayerMapSet((ent, sprite), layerKey, index);
        }

        _sprite.LayerSetRsi((ent, sprite), index, rsiPath, stateName);
        _sprite.LayerSetColor((ent, sprite), index, color);
        _sprite.LayerSetVisible((ent, sprite), index, visible);
    }

    private int? GetOrganLayerInsertIndex(Entity<ErpOrganVisualsComponent> ent, SpriteComponent sprite, string slotId)
    {
        var insertIdx = GetFirstEquipmentLayerIndex(ent.Owner, sprite);

        var reachedSlot = false;
        foreach (var otherSlot in _orderedSlots)
        {
            if (otherSlot == slotId)
            {
                reachedSlot = true;
                continue;
            }

            if (!_slotToLayerKey.TryGetValue(otherSlot, out var otherLayerKey))
                continue;

            if (!_sprite.LayerMapTryGet((ent, sprite), otherLayerKey, out var otherIdx, false))
                continue;

            if (!reachedSlot)
            {
                insertIdx = otherIdx + 1;
                continue;
            }

            return insertIdx.HasValue
                ? Math.Min(insertIdx.Value, otherIdx)
                : otherIdx;
        }

        return insertIdx;
    }

    private bool OrganLayerOrderMatches(Entity<ErpOrganVisualsComponent> ent, SpriteComponent sprite)
    {
        var previousIdx = -1;
        var clothingLayer = GetFirstEquipmentLayerIndex(ent.Owner, sprite);

        foreach (var slotId in _orderedSlots)
        {
            if (!_slotToLayerKey.TryGetValue(slotId, out var layerKey))
                continue;

            if (!_sprite.LayerMapTryGet((ent, sprite), layerKey, out var index, false))
                continue;

            if (clothingLayer != null && index >= clothingLayer)
                return false;

            if (index < previousIdx)
                return false;

            previousIdx = index;
        }

        return true;
    }

    private void RemoveOrganLayers(Entity<ErpOrganVisualsComponent> ent, SpriteComponent sprite)
    {
        var toRemove = new List<(string Key, int Index)>();
        foreach (var layerKey in _slotToLayerKey.Values)
        {
            if (_sprite.LayerMapTryGet((ent, sprite), layerKey, out var index, false))
                toRemove.Add((layerKey, index));
        }

        // Legacy split breast layers from earlier dual-physics work.
        foreach (var splitKey in new[] { BreastLeftLayerKey, BreastRightLayerKey })
        {
            if (_sprite.LayerMapTryGet((ent, sprite), splitKey, out var splitIdx, false))
                toRemove.Add((splitKey, splitIdx));
        }

        toRemove.Sort(static (a, b) => b.Index.CompareTo(a.Index));
        foreach (var (key, index) in toRemove)
        {
            _sprite.LayerSetVisible((ent, sprite), index, false);
            _sprite.LayerMapRemove((ent, sprite), key);
            _sprite.RemoveLayer((ent, sprite), index);
        }
    }

    private int? GetFirstEquipmentLayerIndex(EntityUid uid, SpriteComponent sprite)
    {
        if (!TryComp<InventorySlotsComponent>(uid, out var inventorySlots))
            return null;

        int? firstIdx = null;
        foreach (var layerKeys in inventorySlots.VisualLayerKeys.Values)
        {
            foreach (var layerKey in layerKeys)
            {
                if (!_sprite.LayerMapTryGet((uid, sprite), layerKey, out var idx, false))
                    continue;

                firstIdx = firstIdx.HasValue ? Math.Min(firstIdx.Value, idx) : idx;
            }
        }

        return firstIdx;
    }

    private int CompareSlotsByDrawOrder(string left, string right)
    {
        var orderCompare = GetDrawOrder(left).CompareTo(GetDrawOrder(right));
        return orderCompare != 0
            ? orderCompare
            : string.CompareOrdinal(left, right);
    }

    private int GetDrawOrder(string slotId)
        => _slotDrawOrder.TryGetValue(slotId, out var order) ? order : 0;

    private static string ResolveStateName(ErpOrganVisualPrototype proto, ErpOrganConfig cfg, ArousalPhase phase)
    {
        return proto.StateMode switch
        {
            ErpStateMode.Fixed => proto.FixedState,

            ErpStateMode.SizeString => Math.Clamp(cfg.Size, 1, 99).ToString(),

            ErpStateMode.SizeIndexed when proto.SizeStates.Count > 0 =>
                proto.SizeStates[Math.Clamp(cfg.Size - 1, 0, proto.SizeStates.Count - 1)],

            ErpStateMode.Arousal =>
                phase >= ArousalPhase.Aroused && proto.ArousalStates.TryGetValue(phase.ToString(), out var aroused)
                    ? aroused
                    : proto.FlaccidState,

            ErpStateMode.Variant =>
                proto.VariantAliases.TryGetValue(cfg.Variant, out var alias) ? alias : cfg.Variant,

            ErpStateMode.SizeIndexedArousal when proto.SizeStates.Count > 0 =>
                phase >= ArousalPhase.Aroused
                    ? proto.SizeStates[Math.Clamp(cfg.Size - 1, 0, proto.SizeStates.Count - 1)]
                    : proto.FlaccidState,

            _ => cfg.Variant,
        };
    }
}
