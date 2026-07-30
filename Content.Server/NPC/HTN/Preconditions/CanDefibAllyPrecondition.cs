using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when HealTarget is a revivable faction corpse and the healer owns a usable defib (medic).
/// </summary>
public sealed partial class CanDefibAllyPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (medical.IsDefibDoAfterRunning(owner))
            return Invert;

        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return Invert;

        var ready = medical.CanSafelyDefibAlly(owner, patient);
        return Invert ? !ready : ready;
    }
}
