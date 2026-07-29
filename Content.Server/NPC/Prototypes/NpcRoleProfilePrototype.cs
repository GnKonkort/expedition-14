using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Prototypes;

/// <summary>
/// Role / profession profile: inventory and chem policies for an NPC agent.
/// </summary>
[Prototype("npcRoleProfile")]
public sealed partial class NpcRoleProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public ProtoId<NpcInventoryPolicyPrototype>? InventoryPolicy;

    [DataField]
    public ProtoId<NpcChemKnowledgePrototype>? ChemKnowledge;
}
