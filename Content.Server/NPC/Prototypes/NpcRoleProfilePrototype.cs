using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Prototypes;

/// <summary>
/// Thin role tag for the NPC editor: which simple tasks this agent may perform.
/// </summary>
[Prototype("npcRoleProfile")]
public sealed partial class NpcRoleProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    /// <summary>
    /// May run the defibrillator revive task.
    /// </summary>
    [DataField]
    public bool CanDefib;

    /// <summary>
    /// May bypass bolted/locked doors with C4 or emag.
    /// </summary>
    [DataField]
    public bool CanBypassDoor = true;

    /// <summary>
    /// Optional simple task ids (DefibAlly / BypassDoor / …) assigned by the editor.
    /// </summary>
    [DataField]
    public List<ProtoId<NpcSimpleTaskPrototype>> Tasks = new();
}
