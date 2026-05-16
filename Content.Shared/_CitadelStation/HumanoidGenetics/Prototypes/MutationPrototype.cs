using Robust.Shared.Prototypes;
using Content.Shared.EntityEffects;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Polymorph;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Prototypes;

[Prototype]
public sealed partial class HumanoidMutationPrototype : IPrototype {
    [IdDataField]
    public string ID { get; private set; } = default!;


    // Name of mutation in human-readable form
    [DataField("name")]
    public string Name = string.Empty;

    // Chance for weighted random
    // 1.0 - guaranteed
    // 0.0 - impossible
    [DataField("chance")]
    public float Chanсe = 0.0f;

    //Which entity Effect the mutation should apply to entity
    // null = purely cosmetic effect
    [DataField("effects")]
    public List<EntityEffect> Effects = [];

    //Which marking to be applied
    [DataField("marking")]
    public ProtoId<MarkingPrototype> Marking;

    //Which race entity should be transformed into
    [DataField("race")]
    public string Race = string.Empty;

    //Which entity should person be polymorhped to, when mutation occurs
    [DataField("polymorphEntity")]
    public EntProtoId PolymorphEntity;
}
