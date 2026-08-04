using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CitadelStation.Painting;

/// <summary>
/// A single paint layer. Pixels are Deflate-compressed RGBA of the canvas Size×Size.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public sealed partial class CanvasLayerData
{
    [DataField]
    public string Name = "Layer";

    [DataField]
    public byte[] PixelData = Array.Empty<byte>();

    [DataField]
    public bool Visible = true;

    /// <summary>0–1 layer opacity multiplier.</summary>
    [DataField]
    public float Opacity = 1f;
}

/// <summary>
/// Player-paintable canvas with variable resolution and layers.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class CanvasComponent : Component
{
    public static readonly int[] AllowedSizes = [64, 128, 256];

    /// <summary>World/sprite display edge in pixels (always the same).</summary>
    public const int DisplaySize = 32;

    /// <summary>
    /// Artwork size on the easel sprite. Slightly larger than the frame opening
    /// so the painting reads as a canvas resting on the ledge.
    /// </summary>
    public const int EaselArtworkDisplaySize = 15;

    /// <summary>
    /// Offset of easel artwork from sprite center, in tiles (1 tile = 32 px).
    /// Positive Y is up for layer offset. +13 px from the -9 shelf baseline.
    /// </summary>
    public const float EaselArtworkOffsetY = 4f / 32f;

    /// <summary>Editor control edge in UI pixels (always the same).</summary>
    public const int EditorDisplaySize = 256;

    public const int MaxLayers = 8;
    public const int MaxTitleLength = 64;
    public const int DefaultSize = 64;

    [DataField, AutoNetworkedField]
    public int Size = DefaultSize;

    [DataField, AutoNetworkedField]
    public List<CanvasLayerData> Layers = new();

    [DataField, AutoNetworkedField]
    public int ActiveLayer;

    [DataField, AutoNetworkedField]
    public ProtoId<PaintingFramePrototype>? FrameId;

    [DataField, AutoNetworkedField]
    public string Title = string.Empty;

    [DataField, AutoNetworkedField]
    public string Signature = string.Empty;

    [DataField, AutoNetworkedField]
    public bool Locked;

    [DataField, AutoNetworkedField]
    public bool Mounted;

    public int RawByteLength => Size * Size * 4;
}

[Serializable, NetSerializable]
public enum CanvasUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum EaselUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum CanvasVisualLayers : byte
{
    Frame,
    Artwork
}

[Serializable, NetSerializable]
public enum EaselVisualLayers : byte
{
    Base,
    Artwork
}
