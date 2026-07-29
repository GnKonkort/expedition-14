using Robust.Shared.Serialization;

namespace Content.Shared.NPC.Prototypes;

/// <summary>
/// Editor / gameplay grouping for <see cref="NpcFactionPrototype"/>.
/// </summary>
[Serializable, NetSerializable]
public enum NpcFactionCategory : byte
{
    Crew = 0,
    Civilian = 1,
    Antag = 2,
    Wildlife = 3,
    Expedition = 4,
    Special = 5,
}
