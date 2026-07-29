using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.NPC.Dialogue;
using Content.Shared.Preferences;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC;

/// <summary>
/// Serializable custom NPC configuration used by the admin editor and DB presets.
/// </summary>
[DataDefinition]
[Serializable, NetSerializable]
public sealed partial class NpcPresetData
{
    [DataField]
    public string DisplayName { get; set; } = "Custom NPC";

    [DataField]
    public bool RandomAppearance { get; set; } = true;

    /// <summary>
    /// Used when <see cref="RandomAppearance"/> is false. May be null until configured.
    /// </summary>
    [DataField]
    public HumanoidCharacterProfile? Profile { get; set; }

    [DataField]
    public ProtoId<SpeciesPrototype> Species { get; set; } = SharedHumanoidAppearanceSystem.DefaultSpecies;

    [DataField]
    public NpcBehaviorPack BehaviorPack { get; set; } = NpcBehaviorPack.Combat;

    /// <summary>
    /// HTN compound prototype id (server-side <c>HTNCompoundPrototype</c>).
    /// </summary>
    [DataField]
    public string HtnRoot { get; set; } = "SimpleHumanoidHostileCompound";

    /// <summary>
    /// NPC role profile prototype id (server-side <c>NpcRoleProfilePrototype</c>).
    /// </summary>
    [DataField]
    public string? RoleProfile { get; set; } = "SyndicateFootsoldier";

    [DataField]
    public string? StartingGear { get; set; }

    [DataField]
    public List<string> Factions { get; set; } = new() { "Syndicate" };

    [DataField]
    public List<string> AddFriendlyFactions { get; set; } = new();

    [DataField]
    public List<string> AddHostileFactions { get; set; } = new();

    [DataField]
    public bool NavClimb { get; set; } = true;

    [DataField]
    public bool NavInteract { get; set; } = true;

    [DataField]
    public bool NavPry { get; set; } = true;

    [DataField]
    public bool NavSmash { get; set; } = true;

    [DataField]
    public bool JoinSquad { get; set; }

    /// <summary>
    /// When false, idle HTN will not pick random wander destinations.
    /// </summary>
    [DataField]
    public bool AllowWander { get; set; } = true;

    /// <summary>
    /// When true, the NPC's spawn/apply position becomes a fixed stand point they return to when idle.
    /// </summary>
    [DataField]
    public bool UseStandPoint { get; set; }

    /// <summary>
    /// Optional stock dialogue prototype id (<c>npcDialogue</c>), used if custom tree is empty.
    /// </summary>
    [DataField]
    public string? DialogueTree { get; set; }

    /// <summary>
    /// Inline dialogue authored in the NPC editor.
    /// </summary>
    [DataField]
    public NpcDialogueTreeData? CustomDialogue { get; set; }

    [DataField]
    public string? Notes { get; set; }
}
