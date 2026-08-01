using Content.Shared._CitadelStation.SubGrid;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CitadelStation.SubGrid.UI;

[UsedImplicitly]
public sealed class SubGridFabricatorBoundUserInterface : BoundUserInterface
{
    private SubGridFabricatorWindow? _window;

    public SubGridFabricatorBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<SubGridFabricatorWindow>();
        _window.OnAssemble += cells => SendMessage(new SubGridFabricatorAssembleMessage(cells));
        _window.OnClear += () => SendMessage(new SubGridFabricatorClearMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (_window == null || state is not SubGridFabricatorBoundUserInterfaceState cast)
            return;

        _window.UpdateState(cast);
    }
}
