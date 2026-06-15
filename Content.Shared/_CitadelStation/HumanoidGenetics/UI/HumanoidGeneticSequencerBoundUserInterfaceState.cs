using Content.Shared._CitadelStation.HumanoidGenetics.Components;
using Robust.Shared.Serialization;


namespace Content.Shared._CitadelStation.HumanoidGenetics.UI;
[Serializable, NetSerializable]
public sealed class HumanoidGeneticSequencerBoundUserInterfaceState(List<MutationClass> mutations, NetEntity? targetEntity) : BoundUserInterfaceState {
    public List<MutationClass> Mutations = mutations;
    public readonly NetEntity? TargetEntity = targetEntity;
}
