using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// ChemFamily ontology for med/chem selection without full reagent simulation.
/// </summary>
public sealed class NPCChemOntologySystem : EntitySystem
{
    public readonly record struct ChemProfile(
        string Family,
        string[] DamageTypes,
        bool StopsBleed,
        float Priority,
        bool CritAllyOnly = false,
        bool Stabilizer = false);

    private static readonly Dictionary<string, ChemProfile> Profiles = new(StringComparer.Ordinal)
    {
        ["BruteAutoInjector"] = new("brute", new[] { "Blunt", "Slash", "Piercing" }, true, 40),
        ["BruizAutoInjector"] = new("brute", new[] { "Blunt" }, false, 35),
        ["LacerAutoInjector"] = new("brute", new[] { "Slash" }, false, 35),
        ["PunctAutoInjector"] = new("brute", new[] { "Piercing" }, false, 35),
        ["BurnAutoInjector"] = new("burn", new[] { "Heat", "Cold", "Shock", "Caustic" }, false, 40),
        ["PyraAutoInjector"] = new("burn", new[] { "Heat", "Cold", "Shock", "Caustic" }, false, 35),
        ["RadAutoInjector"] = new("radiation", new[] { "Radiation" }, false, 30),
        ["AntiPoisonMedipen"] = new("toxin", new[] { "Poison" }, false, 30),
        ["AirlossAutoInjector"] = new("airloss", new[] { "Asphyxiation" }, false, 25),
        ["HemostasisAutoInjector"] = new("stabilizer", Array.Empty<string>(), true, 45, Stabilizer: true),
        ["EmergencyMedipen"] = new("stabilizer", Array.Empty<string>(), true, 100, CritAllyOnly: true, Stabilizer: true),
        ["CombatMedipen"] = new("combat", new[] { "Blunt", "Slash", "Piercing", "Heat", "Cold", "Shock", "Caustic" }, true, 50, Stabilizer: true),
    };

    [Dependency] private readonly IPrototypeManager _proto = default!;

    public bool TryGetProfile(string prototypeId, out ChemProfile profile)
        => Profiles.TryGetValue(prototypeId, out profile);

    public bool TryGetProfile(EntityUid item, out ChemProfile profile)
    {
        profile = default;
        if (MetaData(item).EntityPrototype is not { } proto)
            return false;
        return TryGetProfile(proto.ID, out profile);
    }

    public bool IsAllowedByKnowledge(EntityUid owner, EntityUid item)
    {
        if (!TryGetProfile(item, out var profile))
            return false;

        if (!TryGetKnowledge(owner, out var knowledge))
            return true;

        if (knowledge.Families.Count == 0)
            return true;

        return knowledge.Families.Contains(profile.Family);
    }

    public bool TryGetKnowledge(EntityUid owner, out NpcChemKnowledgePrototype knowledge)
    {
        knowledge = default!;

        if (TryComp<HTNComponent>(owner, out var htn) &&
            htn.Blackboard.TryGetValue<string>(NPCBlackboard.ChemKnowledge, out var id, EntityManager) &&
            _proto.TryIndex(id, out NpcChemKnowledgePrototype? fromBb))
        {
            knowledge = fromBb;
            return true;
        }

        if (TryComp<NPCRoleComponent>(owner, out var role) &&
            _proto.TryIndex(role.Profile, out NpcRoleProfilePrototype? profile) &&
            profile.ChemKnowledge is { } chemId &&
            _proto.TryIndex(chemId, out NpcChemKnowledgePrototype? fromRole))
        {
            knowledge = fromRole;
            return true;
        }

        return false;
    }
}
