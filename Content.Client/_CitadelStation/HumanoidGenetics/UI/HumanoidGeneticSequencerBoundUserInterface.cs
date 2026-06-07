using Robust.Client.UserInterface;

namespace Content.Client._CitadelStation.HumanoidGenetics.UI;
public sealed class HumanoidGeneticSequencerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private HumanoidGeneticSequencer? _window;
    protected override void Open()
    {
        base.Open();
        _window ??= this.CreateWindow<HumanoidGeneticSequencer>();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {

    }
}
