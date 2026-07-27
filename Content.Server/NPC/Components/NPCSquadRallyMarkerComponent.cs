namespace Content.Server.NPC.Components;

/// <summary>
/// Marks a debug entity as a squad rally visual.
/// </summary>
[RegisterComponent]
public sealed partial class NPCSquadRallyMarkerComponent : Component
{
    [ViewVariables]
    public Guid SquadId;
}
