using Robust.Shared.Containers;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Components;

[RegisterComponent]
public sealed partial class GeneticPodComponent : Component {
    /// <summary>
    /// Container for mobs inserted in the pod.
    /// </summary>
    [ViewVariables]
    public ContainerSlot BodyContainer = default!;

    /// <summary>
    ///     Delay applied when inserting a mob in the pod.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("entryDelay")]
    public float EntryDelay = 2f;

}

