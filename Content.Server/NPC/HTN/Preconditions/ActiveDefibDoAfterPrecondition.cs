using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True while a defibrillator zap do-after is running on the NPC.
/// </summary>
public sealed partial class ActiveDefibDoAfterPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var active = medical.IsDefibDoAfterRunning(owner);
        return Invert ? !active : active;
    }
}
