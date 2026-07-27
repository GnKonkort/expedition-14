namespace Content.Server.NPC.Components;

/// <summary>
/// Opt-in for NPC squad formation. Same-faction members nearby form a squad (max 10)
/// and share a rally point outside of combat.
/// </summary>
[RegisterComponent]
public sealed partial class NPCSquadMemberComponent : Component
{
    /// <summary>Runtime squad id; null when not in a squad.</summary>
    [ViewVariables]
    public Guid? SquadId;

    /// <summary>
    /// Prevents automatic squad joins after this NPC was removed.
    /// Can be cleared by explicit manual command.
    /// </summary>
    [ViewVariables]
    public bool AutoJoinBlocked;

    /// <summary>
    /// Runtime flag for future player command: leave current squad manually.
    /// NPCs do not set this themselves.
    /// </summary>
    [ViewVariables]
    public bool ManualLeaveRequested;

    /// <summary>Range to scan for allies to join / form a squad.</summary>
    [DataField]
    public float JoinRange = 8f;

    /// <summary>Leave the squad if farther than this from the rally centroid.</summary>
    [DataField]
    public float LeaveRange = 14f;

    /// <summary>Preferred gather radius around the shared rally point (mirrors SquadRallyRange).</summary>
    [DataField]
    public float RallyRadius = 2.5f;
}
