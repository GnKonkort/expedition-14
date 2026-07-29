using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.NPC.Dialogue;

/// <summary>
/// Marks an NPC as talkable. Uses custom inline tree and/or a prototype template id.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NpcDialogueComponent : Component
{
    /// <summary>
    /// Optional prototype dialogue used when <see cref="CustomDialogue"/> is unset.
    /// </summary>
    [DataField, AutoNetworkedField]
    public ProtoId<NpcDialoguePrototype>? Dialogue;

    /// <summary>
    /// Inline dialogue authored in the NPC editor. Takes priority over <see cref="Dialogue"/>.
    /// </summary>
    [DataField]
    public NpcDialogueTreeData? CustomDialogue;

    [DataField, AutoNetworkedField]
    public bool Enabled = true;
}
