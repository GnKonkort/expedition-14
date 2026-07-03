using Robust.Client.UserInterface;
using Content.Shared._CitadelStation.HumanoidGenetics.UI;

namespace Content.Client._CitadelStation.HumanoidGenetics.UI;

public sealed class GeneticPodBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey) {

    private GeneticPod? _window;

    protected override void Open() {
        base.Open();

        if (_window is null)
            _window = this.CreateWindow<GeneticPod>();

    }

     protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is GeneticPodBoundUserInterfaceState s)
            _window?.UpdateState(s);
    }
}
