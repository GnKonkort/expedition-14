using Content.Shared._CitadelStation.Painting;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._CitadelStation.Painting.UI;

[UsedImplicitly]
public sealed class CanvasBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private CanvasWindow? _window;

    public CanvasBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<CanvasWindow>();
        _window.PopulateFrames(_prototypes.EnumeratePrototypes<PaintingFramePrototype>());
        _window.OnLayersChanged += (layers, active) =>
            SendMessage(new CanvasUpdateLayersMessage(layers, active));
        _window.OnSizeChanged += size => SendMessage(new CanvasSetSizeMessage(size));
        _window.OnFrameSelected += frameId => SendMessage(new CanvasSetFrameMessage(frameId));
        _window.OnTitleChanged += title => SendMessage(new CanvasSetTitleMessage(title));
        _window.OnLockRequested += (sign, title) => SendMessage(new CanvasLockMessage(sign, title));
        _window.OnClose += () => _window?.FlushLayers();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is CanvasBoundUserInterfaceState canvasState)
            _window?.UpdateState(canvasState);
    }
}
