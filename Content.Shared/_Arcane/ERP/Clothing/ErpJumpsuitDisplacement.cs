using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Robust.Shared.GameObjects;

namespace Content.Shared._Arcane.ERP.Clothing;

/// <summary>
/// Helpers for jumpsuit breast coverage (pixel stretch on the client).
/// </summary>
public static class ErpJumpsuitDisplacement
{
    public const string Slot = "jumpsuit";

    public static bool TryGetBreastSize(ErpOrganVisualsComponent? visuals, out int size)
    {
        size = 0;
        if (visuals == null)
            return false;

        if (!visuals.Organs.TryGetValue(ErpOrganSlots.Breasts, out var cfg))
            return false;

        size = Math.Clamp(cfg.Size, 1, 4);
        return true;
    }
}

/// <summary>
/// Raised after organ visuals were applied so clothing can refresh jumpsuit stretch.
/// </summary>
[ByRefEvent]
public readonly record struct ErpOrganVisualsUpdatedEvent;
