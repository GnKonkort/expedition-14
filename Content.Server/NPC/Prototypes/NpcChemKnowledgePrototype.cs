using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Prototypes;

/// <summary>
/// Chem family whitelist for med/chem selection without full reagent simulation.
/// </summary>
[Prototype("npcChemKnowledge")]
public sealed partial class NpcChemKnowledgePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    /// <summary>
    /// ChemFamily ids this role understands (brute, burn, toxin, airloss, stabilizer, ...).
    /// </summary>
    [DataField]
    public HashSet<string> Families = new(StringComparer.OrdinalIgnoreCase);
}
