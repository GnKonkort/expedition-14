using Robust.Shared.Serialization;

namespace Content.Shared._CitadelStation.Painting;

[Serializable, NetSerializable]
public sealed class CanvasBoundUserInterfaceState : BoundUserInterfaceState
{
    public int Size;
    public List<CanvasLayerData> Layers;
    public int ActiveLayer;
    public string? FrameId;
    public string Title;
    public string Signature;
    public bool Locked;
    public bool CanEdit;

    public CanvasBoundUserInterfaceState(
        int size,
        List<CanvasLayerData> layers,
        int activeLayer,
        string? frameId,
        string title,
        string signature,
        bool locked,
        bool canEdit)
    {
        Size = size;
        Layers = layers;
        ActiveLayer = activeLayer;
        FrameId = frameId;
        Title = title;
        Signature = signature;
        Locked = locked;
        CanEdit = canEdit;
    }
}

/// <summary>Save all layers + active index (client → server).</summary>
[Serializable, NetSerializable]
public sealed class CanvasUpdateLayersMessage : BoundUserInterfaceMessage
{
    public readonly List<CanvasLayerData> Layers;
    public readonly int ActiveLayer;

    public CanvasUpdateLayersMessage(List<CanvasLayerData> layers, int activeLayer)
    {
        Layers = layers;
        ActiveLayer = activeLayer;
    }
}

[Serializable, NetSerializable]
public sealed class CanvasSetSizeMessage : BoundUserInterfaceMessage
{
    public readonly int Size;

    public CanvasSetSizeMessage(int size)
    {
        Size = size;
    }
}

[Serializable, NetSerializable]
public sealed class CanvasSetFrameMessage : BoundUserInterfaceMessage
{
    public readonly string? FrameId;

    public CanvasSetFrameMessage(string? frameId)
    {
        FrameId = frameId;
    }
}

[Serializable, NetSerializable]
public sealed class CanvasSetTitleMessage : BoundUserInterfaceMessage
{
    public readonly string Title;

    public CanvasSetTitleMessage(string title)
    {
        Title = title;
    }
}

[Serializable, NetSerializable]
public sealed class CanvasLockMessage : BoundUserInterfaceMessage
{
    public readonly bool Sign;
    public readonly string Title;

    public CanvasLockMessage(bool sign, string title)
    {
        Sign = sign;
        Title = title;
    }
}

[Serializable, NetSerializable]
public sealed class EaselBoundUserInterfaceState : BoundUserInterfaceState
{
    public NetEntity[] Slots;
    public int ActiveIndex;
    public string[] SlotNames;

    public EaselBoundUserInterfaceState(NetEntity[] slots, int activeIndex, string[] slotNames)
    {
        Slots = slots;
        ActiveIndex = activeIndex;
        SlotNames = slotNames;
    }
}

[Serializable, NetSerializable]
public sealed class EaselSelectSlotMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public EaselSelectSlotMessage(int index)
    {
        Index = index;
    }
}

[Serializable, NetSerializable]
public sealed class EaselEjectSlotMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public EaselEjectSlotMessage(int index)
    {
        Index = index;
    }
}

[Serializable, NetSerializable]
public sealed class EaselOpenCanvasMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public EaselOpenCanvasMessage(int index)
    {
        Index = index;
    }
}
