using Robust.Client.UserInterface;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;
using Content.Shared._CitadelStation.HumanoidGenetics.UI;
using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._CitadelStation.HumanoidGenetics.UI;

public sealed class HumanoidGeneticSequencerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private HumanoidGeneticSequencer? _window;
    [Dependency] private readonly EntityManager _entManager = default!;
    protected override void Open()
    {
        base.Open();
        _window ??= this.CreateWindow<HumanoidGeneticSequencer>();
    }

    protected override void UpdateState(BoundUserInterfaceState state) {
        if (state is not HumanoidGeneticSequencerBoundUserInterfaceState sequencerState || _window is null)
            return;

        base.UpdateState(state);

        var target = _entManager.GetEntity(sequencerState.TargetEntity);

        _window.SpriteView.SetEntity(target);
        if(sequencerState.Mutations == null || sequencerState.Mutations.Count == 0) {
            _window.AddChild(new Label(){
                Text = $"No Mutations detected"
            });
        } else {
            foreach (var mutation in sequencerState.Mutations) {
                _window.AddChild(new Label(){
                    Text = $"Mutation detected: {mutation.MutationProto}"
                });
            }
        }
    }
}
