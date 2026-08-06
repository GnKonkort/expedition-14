using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Humanoid;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Robust.Shared.Prototypes;

namespace Content.Shared._Arcane.ERP.OrgansAppearance;

/// <summary>
/// Shared helpers for building organ visual configs and clothing coverage sets.
/// </summary>
public static class ErpOrganVisualsHelpers
{
    public static Dictionary<string, ErpOrganConfig> BuildOrgansFromPrefs(
        ErpOrganPreferences prefs,
        string? species,
        Sex sex,
        IPrototypeManager proto,
        IComponentFactory factory)
    {
        var definitions = ErpOrganEditorDefinitions.GetForSpecies(species, sex, proto, factory);
        var normalized = ErpOrganPreferencesNormalizer.Normalize(prefs, definitions);

        foreach (var definition in definitions)
            normalized.Organs.TryAdd(definition.SlotId, ErpOrganEditorDefinitions.CreateDefaultConfig(definition));

        return FilterOrgansBySex(normalized.Organs, sex);
    }

    public static Dictionary<string, ErpOrganConfig> FilterOrgansBySex(
        Dictionary<string, ErpOrganConfig> organs,
        Sex sex)
    {
        var result = new Dictionary<string, ErpOrganConfig>();
        foreach (var (slotId, cfg) in organs)
        {
            if (ErpOrganSlots.SexFilter.TryGetValue(slotId, out var allowed) && Array.IndexOf(allowed, sex) < 0)
                continue;
            result[slotId] = cfg;
        }

        return result;
    }

    public static void AddCoveredSlotsForLayer(HashSet<string> covered, HumanoidVisualLayers layer)
    {
        if (layer == HumanoidVisualLayers.ErpGroin)
        {
            covered.Add(ErpOrganSlots.Penis);
            covered.Add(ErpOrganSlots.Testicles);
            covered.Add(ErpOrganSlots.Vagina);
            covered.Add(ErpOrganSlots.Anus);
            covered.Add(ErpOrganSlots.Butt);
        }

        if (layer == HumanoidVisualLayers.ErpChest)
            covered.Add(ErpOrganSlots.Breasts);
    }

    public static HashSet<string> GetCoveredSlotsFromLayers(HashSet<HumanoidVisualLayers> coveredLayers)
    {
        var covered = new HashSet<string>();
        foreach (var layer in coveredLayers)
            AddCoveredSlotsForLayer(covered, layer);
        return covered;
    }

    public static void CollectCoveredLayersFromClothing(
        HideLayerClothingComponent hideLayer,
        ClothingComponent clothing,
        bool hideEnabled,
        HashSet<HumanoidVisualLayers> coveredLayers)
    {
        var inSlot = clothing.InSlotFlag ?? SlotFlags.NONE;
        if (inSlot == SlotFlags.NONE || !hideEnabled)
            return;

        foreach (var (layer, validSlots) in hideLayer.Layers)
        {
            if (validSlots.HasFlag(inSlot))
                coveredLayers.Add(layer);
        }

#pragma warning disable CS0618
        if (hideLayer.Slots is { } slots && clothing.Slots.HasFlag(inSlot))
#pragma warning restore CS0618
        {
            foreach (var layer in slots)
                coveredLayers.Add(layer);
        }
    }
}
