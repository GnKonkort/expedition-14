using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._CitadelStation.Signboard;

/// <summary>One animation frame. Pixels are Deflate-compressed RGBA of (Width×Height).</summary>
[DataDefinition, Serializable, NetSerializable]
public sealed partial class SignboardFrameData
{
    [DataField]
    public string Name = "Frame";

    [DataField]
    public byte[] PixelData = Array.Empty<byte>();
}

[Prototype("signboardFrame")]
public sealed partial class SignboardFramePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    /// <summary>RSI for frame segments. States: solo, left, mid, right.</summary>
    [DataField(required: true)]
    public SpriteSpecifier.Rsi Sprite = default!;
}
