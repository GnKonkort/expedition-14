using Robust.Shared.Serialization;

namespace Content.Shared._CitadelStation.Signboard;

[Serializable, NetSerializable]
public sealed class SignboardBoundUserInterfaceState : BoundUserInterfaceState
{
    public int Size;
    public int GridWidth;
    public int GridHeight;
    public List<SignboardFrameData> Frames;
    public int ActiveEditFrame;
    public int PlaybackFrame;
    public float FrameDelay;
    public bool Neon;
    public bool Powered;
    public bool Playing;
    public string? FrameStyleId;
    public bool CanEdit;

    public SignboardBoundUserInterfaceState(
        int size,
        int gridWidth,
        int gridHeight,
        List<SignboardFrameData> frames,
        int activeEditFrame,
        int playbackFrame,
        float frameDelay,
        bool neon,
        bool powered,
        bool playing,
        string? frameStyleId,
        bool canEdit)
    {
        Size = size;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Frames = frames;
        ActiveEditFrame = activeEditFrame;
        PlaybackFrame = playbackFrame;
        FrameDelay = frameDelay;
        Neon = neon;
        Powered = powered;
        Playing = playing;
        FrameStyleId = frameStyleId;
        CanEdit = canEdit;
    }
}

[Serializable, NetSerializable]
public sealed class SignboardUpdateFramesMessage : BoundUserInterfaceMessage
{
    public readonly List<SignboardFrameData> Frames;
    public readonly int ActiveEditFrame;

    public SignboardUpdateFramesMessage(List<SignboardFrameData> frames, int activeEditFrame)
    {
        Frames = frames;
        ActiveEditFrame = activeEditFrame;
    }
}

[Serializable, NetSerializable]
public sealed class SignboardSetSizeMessage : BoundUserInterfaceMessage
{
    public readonly int Size;
    public SignboardSetSizeMessage(int size) => Size = size;
}

[Serializable, NetSerializable]
public sealed class SignboardSetDelayMessage : BoundUserInterfaceMessage
{
    public readonly float Delay;
    public SignboardSetDelayMessage(float delay) => Delay = delay;
}

[Serializable, NetSerializable]
public sealed class SignboardSetNeonMessage : BoundUserInterfaceMessage
{
    public readonly bool Neon;
    public SignboardSetNeonMessage(bool neon) => Neon = neon;
}

[Serializable, NetSerializable]
public sealed class SignboardSetPlayingMessage : BoundUserInterfaceMessage
{
    public readonly bool Playing;
    public SignboardSetPlayingMessage(bool playing) => Playing = playing;
}

[Serializable, NetSerializable]
public sealed class SignboardSetFrameStyleMessage : BoundUserInterfaceMessage
{
    public readonly string? FrameStyleId;
    public SignboardSetFrameStyleMessage(string? frameStyleId) => FrameStyleId = frameStyleId;
}

[Serializable, NetSerializable]
public sealed class SignboardSetPoweredMessage : BoundUserInterfaceMessage
{
    public readonly bool Powered;
    public SignboardSetPoweredMessage(bool powered) => Powered = powered;
}
