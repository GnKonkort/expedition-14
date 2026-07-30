namespace Content.Shared.NPC;

/// <summary>
/// Curated HTN / role defaults for the custom NPC editor.
/// </summary>
public static class NpcBehaviorWhitelist
{
    public static readonly IReadOnlyDictionary<NpcBehaviorPack, string[]> HtnByPack =
        new Dictionary<NpcBehaviorPack, string[]>
        {
            [NpcBehaviorPack.Basic] =
            [
                "HumanoidAgentCompound",
            ],
            [NpcBehaviorPack.Civilian] =
            [
                "HumanoidAgentCompound",
            ],
            [NpcBehaviorPack.Combat] =
            [
                "SimpleHumanoidHostileCompound",
                "HumanoidAgentCompound",
            ],
        };

    public static readonly IReadOnlyDictionary<NpcBehaviorPack, string[]> RoleProfilesByPack =
        new Dictionary<NpcBehaviorPack, string[]>
        {
            [NpcBehaviorPack.Basic] =
            [
                "SyndicateFootsoldier",
                "StationMedic",
                "StationEngineer",
                "StationSecurity",
            ],
            [NpcBehaviorPack.Civilian] =
            [
                "StationMedic",
                "StationEngineer",
            ],
            [NpcBehaviorPack.Combat] =
            [
                "SyndicateFootsoldier",
                "StationSecurity",
                "StationMedic",
            ],
        };

    public static readonly IReadOnlyDictionary<NpcBehaviorPack, string> DefaultFaction =
        new Dictionary<NpcBehaviorPack, string>
        {
            [NpcBehaviorPack.Basic] = "Independent",
            [NpcBehaviorPack.Civilian] = "CrewCivilian",
            [NpcBehaviorPack.Combat] = "Syndicate",
        };

    public static readonly IReadOnlyDictionary<NpcBehaviorPack, string?> DefaultRoleProfile =
        new Dictionary<NpcBehaviorPack, string?>
        {
            [NpcBehaviorPack.Basic] = null,
            [NpcBehaviorPack.Civilian] = "StationMedic",
            [NpcBehaviorPack.Combat] = "SyndicateFootsoldier",
        };

    public static readonly IReadOnlyDictionary<NpcBehaviorPack, string> DefaultHtn =
        new Dictionary<NpcBehaviorPack, string>
        {
            [NpcBehaviorPack.Basic] = "HumanoidAgentCompound",
            [NpcBehaviorPack.Civilian] = "HumanoidAgentCompound",
            [NpcBehaviorPack.Combat] = "SimpleHumanoidHostileCompound",
        };

    public static NpcPresetData CreateDefault(NpcBehaviorPack pack = NpcBehaviorPack.Combat)
    {
        return new NpcPresetData
        {
            BehaviorPack = pack,
            HtnRoot = DefaultHtn[pack],
            RoleProfile = DefaultRoleProfile[pack],
            Factions = new List<string> { DefaultFaction[pack] },
            RandomAppearance = true,
            JoinSquad = pack == NpcBehaviorPack.Combat,
        };
    }
}
