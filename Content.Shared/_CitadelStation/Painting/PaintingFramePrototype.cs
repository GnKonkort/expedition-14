using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._CitadelStation.Painting;

[Prototype("paintingFrame")]
public sealed partial class PaintingFramePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    /// <summary>
    /// Sprite used for the frame layer on items and wallmounts.
    /// </summary>
    [DataField(required: true)]
    public SpriteSpecifier.Rsi Sprite = default!;
}
