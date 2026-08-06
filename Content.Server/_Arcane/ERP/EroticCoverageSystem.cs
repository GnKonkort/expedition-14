using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;

namespace Content.Server._Arcane.ERP;

/// <summary>
/// Updates CoveredSlots on ErpOrganVisualsComponent from equipped HideLayerClothing.
/// Prefs-only: does not touch body organs.
/// </summary>
public sealed class EroticCoverageSystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HumanoidAppearanceComponent, ClothingDidEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<HumanoidAppearanceComponent, ClothingDidUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<HumanoidAppearanceComponent, WearerMaskToggledEvent>(OnMaskToggled);
        SubscribeLocalEvent<ErpOrganVisualsComponent, ComponentStartup>(OnVisualsStartup);
    }

    private void OnEquipped(Entity<HumanoidAppearanceComponent> ent, ref ClothingDidEquippedEvent args)
        => RefreshCoverage(ent);

    private void OnUnequipped(Entity<HumanoidAppearanceComponent> ent, ref ClothingDidUnequippedEvent args)
        => RefreshCoverage(ent);

    private void OnMaskToggled(Entity<HumanoidAppearanceComponent> ent, ref WearerMaskToggledEvent args)
        => RefreshCoverage(ent);

    private void OnVisualsStartup(Entity<ErpOrganVisualsComponent> ent, ref ComponentStartup args)
        => RefreshCoverage(ent);

    public void RefreshCoverage(EntityUid uid)
    {
        if (!TryComp<ErpOrganVisualsComponent>(uid, out var visuals))
            return;

        var coveredLayers = GetCoveredVisualLayers(uid);
        var newCovered = ErpOrganVisualsHelpers.GetCoveredSlotsFromLayers(coveredLayers);

        if (visuals.CoveredSlots.SetEquals(newCovered))
            return;

        visuals.CoveredSlots = newCovered;
        Dirty(uid, visuals);
    }

    private HashSet<HumanoidVisualLayers> GetCoveredVisualLayers(EntityUid uid)
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

        return coveredLayers;
    }

    private bool IsHideLayerEnabled(EntityUid uid, HideLayerClothingComponent hideLayer)
    {
        if (!hideLayer.HideOnToggle)
            return true;

        if (!TryComp<MaskComponent>(uid, out var mask))
            return true;

        return !mask.IsToggled;
    }
}
