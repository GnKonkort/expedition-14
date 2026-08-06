using Robust.Shared.Prototypes;

namespace Content.Shared._Arcane.ERP.Organs;

/// <summary>
/// Editor / visual definition source for ERP organ slots on a species.
/// Does not spawn body organs — used only for preferences UI and visual layer population.
/// </summary>
[RegisterComponent]
public sealed partial class EroticOrgansComponent : Component
{
    [DataField]
    public List<EroticOrganEntry> GroinCommon = [];

    [DataField]
    public List<EroticOrganEntry> GroinMale = [];

    [DataField]
    public List<EroticOrganEntry> GroinFemale = [];

    [DataField]
    public List<EroticOrganEntry> ChestFemale = [];

    /// <summary>
    /// Organ slots hidden when not aroused. Visual layer only shows during arousal.
    /// </summary>
    [DataField]
    public HashSet<string> HideWhenFlaccid = [];

    /// <summary>
    /// Default visual variant per organ slot when the player has no saved preference.
    /// </summary>
    [DataField]
    public Dictionary<string, string> DefaultVariants = [];
}

[DataDefinition]
public sealed partial class EroticOrganEntry
{
    [DataField(required: true)]
    public EntProtoId Proto = default!;

    [DataField(required: true)]
    public string Slot = default!;
}
