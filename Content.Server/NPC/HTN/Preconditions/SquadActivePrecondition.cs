namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC is in an active squad (shared rally coordinates on the blackboard).
/// </summary>
public sealed partial class SquadActivePrecondition : HTNPrecondition
{
    [DataField]
    public string RallyKey = Content.Server.NPC.Systems.NPCSquadSystem.SquadRallyCoordinatesKey;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        return blackboard.ContainsKey(RallyKey);
    }
}
