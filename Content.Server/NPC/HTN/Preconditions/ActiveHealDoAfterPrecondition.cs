using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True while the owner has an active kit healing do-after.
/// Used to pin HTN on waiting so ConstantlyReplan does not abort healing for combat branches.
/// </summary>
public sealed partial class ActiveHealDoAfterPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var active = medical.IsHealingDoAfterRunning(owner);
        return Invert ? !active : active;
    }
}
