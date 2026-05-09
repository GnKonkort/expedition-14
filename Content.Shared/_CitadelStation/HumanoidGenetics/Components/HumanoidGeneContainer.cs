using Content.Shared._CitadelStation.HumanoidGenetics.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Components;


public class MutationStruct
{
    public ProtoId<HumanoidMutationPrototype> MutationProto { get; set; }
    public TimeSpan LastUpdate { get; set; }

    public TimeSpan UpdateCooldown { get; set; }

    public MutationStruct()
    {
        MutationProto = new();
        LastUpdate = TimeSpan.FromSeconds(0);
        UpdateCooldown = TimeSpan.FromSeconds(1);
    }
}

[RegisterComponent]
public sealed partial class HumanoidGeneContainerComponent : Component
{
    [DataField]
    public List<MutationStruct> AppliedMutations = [];
}
