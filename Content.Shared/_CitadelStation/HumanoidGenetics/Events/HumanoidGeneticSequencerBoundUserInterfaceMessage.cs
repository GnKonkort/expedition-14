using Robust.Shared.Serialization;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;

[Serializable, NetSerializable]
public sealed class HumanoidGeneticSequencerBoundUserInterfaceMessage(List<MutationClass> mutations) : BoundUserInterfaceMessage {
    public List<MutationClass> Mutations { get; } = mutations;
}
