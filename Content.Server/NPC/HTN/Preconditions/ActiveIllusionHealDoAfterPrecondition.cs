using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True while an illusion heal DoAfter is running.
/// </summary>
public sealed partial class ActiveIllusionHealDoAfterPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var active = medical.IsIllusionHealDoAfterRunning(owner);
        return Invert ? !active : active;
    }
}
