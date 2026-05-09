using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Content.Shared._CitadelStation.HumanoidGenetics.Prototypes;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Components;

[RegisterComponent]
public sealed partial class HumanoidGeneComponent : Component {
    [DataField]
    public string Name = string.Empty;

    [DataField]
    public TimeSpan LastUpdate = TimeSpan.FromSeconds(0);

    [DataField]
    public ProtoId<HumanoidMutationPrototype> Mutation = new();
}
