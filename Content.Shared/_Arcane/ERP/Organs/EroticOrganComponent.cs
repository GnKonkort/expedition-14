namespace Content.Shared._Arcane.ERP.Organs;

/// <summary>
/// Editor metadata for an erotic organ prototype. No gameplay / body spawn behaviour.
/// </summary>
[RegisterComponent]
public sealed partial class EroticOrganComponent : Component
{
    /// <summary>Whether this organ should be configurable in the character editor ERP tab.</summary>
    [DataField]
    public bool EditorVisible;

    /// <summary>Visual variants available for this organ in the character editor.</summary>
    [DataField]
    public List<string> EditorVariants = [];

    /// <summary>Default visual variant used when the player has no saved preference.</summary>
    [DataField]
    public string EditorDefaultVariant = "human";

    /// <summary>Maximum character editor size index. Values below 2 hide the size control.</summary>
    [DataField]
    public int EditorMaxSize = 1;

    /// <summary>Whether this organ can use a custom tint instead of skin color.</summary>
    [DataField]
    public bool EditorAllowColor = true;
}
