using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Prototypes;

/// <summary>
/// Thin role task definition: approach → progress/SFX → effect tag.
/// Wired from the NPC editor via <see cref="NpcRoleProfilePrototype"/>.
/// </summary>
[Prototype("npcSimpleTask")]
public sealed partial class NpcSimpleTaskPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public NpcSimpleTaskEffect Effect = NpcSimpleTaskEffect.None;

    [DataField]
    public float Range = 1.5f;

    [DataField]
    public float Duration = 2f;

    [DataField]
    public SoundSpecifier? Sound;
}

/// <summary>Effect tag for thin role DoTasks (YAML / editor only — not networked).</summary>
public enum NpcSimpleTaskEffect : byte
{
    None = 0,
    DefibAlly = 1,
    BypassDoor = 2,
    RepairCritical = 3,
}
