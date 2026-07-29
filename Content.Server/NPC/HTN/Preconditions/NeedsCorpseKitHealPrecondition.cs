using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when HealTarget is a revivable corpse that still needs kit healing before defibrillation.
/// </summary>
public sealed partial class NeedsCorpseKitHealPrecondition : HTNPrecondition
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

        if (medical.IsHealingDoAfterRunning(owner))
            return Invert;

        if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out var patient, _entManager))
            return Invert;

        var needs = medical.NeedsCorpseKitHeal(owner, patient);
        return Invert ? !needs : needs;
    }
}
