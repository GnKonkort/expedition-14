using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC.Dialogue;

/// <summary>
/// Data-driven dialogue tree for NPCs. Speech and option text are Fluent loc ids.
/// </summary>
[Prototype("npcDialogue")]
public sealed partial class NpcDialoguePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    /// <summary>
    /// Node id to enter when the dialogue starts.
    /// </summary>
    [DataField(required: true)]
    public string Start = string.Empty;

    [DataField(required: true)]
    public Dictionary<string, NpcDialogueNode> Nodes = new();
}

[DataDefinition]
[Serializable, NetSerializable]
public sealed partial class NpcDialogueNode
{
    /// <summary>
    /// Fluent loc id spoken by the NPC via IC say when this node is entered.
    /// Empty skips speech.
    /// </summary>
    [DataField]
    public string Speech = string.Empty;

    [DataField]
    public List<NpcDialogueOption> Options = new();
}

[DataDefinition]
[Serializable, NetSerializable]
public sealed partial class NpcDialogueOption
{
    /// <summary>
    /// Fluent loc id shown on the player's choice button.
    /// </summary>
    [DataField(required: true)]
    public string Text = string.Empty;

    /// <summary>
    /// Next node id, or null to end the dialogue.
    /// </summary>
    [DataField]
    public string? Next;
}
