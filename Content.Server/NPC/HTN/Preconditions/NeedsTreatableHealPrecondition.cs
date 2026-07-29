using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the patient (self or HealTarget) has more than 15 treatable damage
/// matching the healer's owned kits and/or medipens.
/// </summary>
public sealed partial class NeedsTreatableHealPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    /// <summary>
    /// When true, patient is Owner. When false, uses HealTargetKey.
    /// </summary>
    [DataField]
    public bool Self = true;

    [DataField]
    public string HealTargetKey = NPCBlackboard.HealTarget;

    [DataField]
    public bool AllowKits = true;

    [DataField]
    public bool AllowMedipens = true;

    /// <summary>
    /// When true, only Emergency-style pens that require a critical ally are considered.
    /// </summary>
    [DataField]
    public bool CritAllyPensOnly;

    /// <summary>
    /// When true, dead allies are valid patients (kit corpse healing only).
    /// </summary>
    [DataField]
    public bool AllowDead;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // Kit heal already in progress — don't replan a fresh UseHeal cycle.
        if (AllowKits && medical.IsHealingDoAfterRunning(owner))
            return Invert;

        EntityUid patient = owner;
        if (!Self)
        {
            if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out patient, _entManager))
                return Invert;
        }

        // NeedsHeal uses owned tools; still require a matching category to exist when filtering.
        var needs = medical.NeedsHeal(owner, patient, AllowDead);
        if (needs)
        {
            if (!medical.TrySelectBestOwnedHeal(owner, patient, AllowKits, AllowMedipens, out _, CritAllyPensOnly, AllowDead))
                needs = false;
        }

        return Invert ? !needs : needs;
    }
}
