using Robust.Shared.Utility;

namespace Content.Client._Arcane.ERP.Clothing;

/// <summary>
/// Jumpsuit stretch: static body layer + separate breast-cloth overlay for bounce.
/// </summary>
[RegisterComponent]
public sealed partial class ErpJumpsuitStretchComponent : Component
{
    public const string BounceLayerKey = "erp_jumpsuit_breasts";

    /// <summary>Static jumpsuit body layer keys (never bounced).</summary>
    public List<string> LayerKeys = [];

    /// <summary>Breast cloth overlay key, if present.</summary>
    public string? BounceLayerKeyActive;

    public int BreastSize;
    public string? SourceRsiPath;
    public string? SourceState;

    /// <summary>Generated RSI with states equipped (body) + breast (overlay).</summary>
    public ResPath? GeneratedRsiPath;
}
