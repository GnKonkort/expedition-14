using Content.Shared._CitadelStation.Painting;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CitadelStation.Painting.UI;

[UsedImplicitly]
public sealed class EaselBoundUserInterface : BoundUserInterface
{
    private EaselWindow? _window;

    public EaselBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<EaselWindow>();
        _window.OnSelectSlot += index => SendMessage(new EaselSelectSlotMessage(index));
        _window.OnEjectSlot += index => SendMessage(new EaselEjectSlotMessage(index));
        _window.OnOpenCanvas += index => SendMessage(new EaselOpenCanvasMessage(index));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is EaselBoundUserInterfaceState easelState)
            _window?.UpdateState(easelState);
    }
}
