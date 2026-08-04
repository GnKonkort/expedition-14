using Content.Shared.DeviceLinking;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CitadelStation.Signboard;

/// <summary>
/// Electronic wallmount signboard with up to 10 animation frames.
/// Top-left panel in a filled rectangular grid (max 3×3) is the master and owns pixel data.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class SignboardComponent : Component
{
    public static readonly int[] AllowedSizes = [64, 128, 256];
    public const int DefaultSize = 64;
    public const int MaxFrames = 10;
    public const int MaxGrid = 3;
    public const int DisplaySize = 32;
    /// <summary>Editor canvas size at 100% zoom (pixels per panel side).</summary>
    public const int EditorDisplayHeight = 256;
    public const float DefaultFrameDelaySeconds = 0.5f;

    [DataField, AutoNetworkedField]
    public int Size = DefaultSize;

    /// <summary>Animation frames owned by the master. Slaves keep this empty.</summary>
    [DataField, AutoNetworkedField]
    public List<SignboardFrameData> Frames = new();

    [DataField, AutoNetworkedField]
    public int ActiveEditFrame;

    [DataField, AutoNetworkedField]
    public int PlaybackFrame;

    [DataField, AutoNetworkedField]
    public float FrameDelay = DefaultFrameDelaySeconds;

    [DataField, AutoNetworkedField]
    public bool Neon = true;

    [DataField, AutoNetworkedField]
    public bool Powered = true;

    [DataField, AutoNetworkedField]
    public bool Playing = true;

    [DataField, AutoNetworkedField]
    public ProtoId<SignboardFramePrototype>? FrameStyleId;

    /// <summary>Column in the grid (0 = left).</summary>
    [DataField, AutoNetworkedField]
    public int PanelX;

    /// <summary>Row in the grid (0 = top of the image / north-most tile).</summary>
    [DataField, AutoNetworkedField]
    public int PanelY;

    [DataField, AutoNetworkedField]
    public int GridWidth = 1;

    [DataField, AutoNetworkedField]
    public int GridHeight = 1;

    /// <summary>For slaves: the master entity. Null on master.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? Master;

    [DataField]
    public ProtoId<SinkPortPrototype> OnPort = "On";

    [DataField]
    public ProtoId<SinkPortPrototype> OffPort = "Off";

    [DataField]
    public ProtoId<SinkPortPrototype> NextFramePort = "SignboardNextFrame";

    /// <summary>Server-only: next time to advance playback.</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextFrameAt;

    public int PanelCount => Math.Clamp(GridWidth, 1, MaxGrid) * Math.Clamp(GridHeight, 1, MaxGrid);
    public int FullWidth => Size * Math.Clamp(GridWidth, 1, MaxGrid);
    public int FullHeight => Size * Math.Clamp(GridHeight, 1, MaxGrid);
    public int RawByteLength => FullWidth * FullHeight * 4;
}

[Serializable, NetSerializable]
public enum SignboardUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum SignboardVisualLayers : byte
{
    Frame,
    Artwork
}
