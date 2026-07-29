using Robust.Shared.Prototypes;

namespace Content.Shared.NPC.Prototypes;

/// <summary>
/// Contains data about this faction's relations with other factions.
/// </summary>
[Prototype]
public sealed partial class NpcFactionPrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public List<ProtoId<NpcFactionPrototype>> Friendly = new();

    [DataField]
    public List<ProtoId<NpcFactionPrototype>> Hostile = new();

    /// <summary>
    /// High-level grouping for the custom NPC editor and tooling.
    /// </summary>
    [DataField]
    public NpcFactionCategory Category = NpcFactionCategory.Special;

    /// <summary>
    /// When false, the faction is hidden from the normal NPC editor faction picker
    /// (e.g. meta factions like AllHostile).
    /// </summary>
    [DataField]
    public bool EditorVisible = true;

    /// <summary>
    /// Optional short description shown in admin tooling.
    /// </summary>
    [DataField]
    public string? Description;
}

/// <summary>
/// Cached data for the faction prototype. Is modified at runtime, whereas the prototype is not.
/// </summary>
public record struct FactionData
{
    [ViewVariables]
    public HashSet<ProtoId<NpcFactionPrototype>> Friendly;

    [ViewVariables]
    public HashSet<ProtoId<NpcFactionPrototype>> Hostile;
}
