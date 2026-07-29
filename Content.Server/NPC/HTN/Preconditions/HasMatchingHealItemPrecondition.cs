using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC owns a kit/medipen that helps the patient.
/// </summary>
public sealed partial class HasMatchingHealItemPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

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

    [DataField]
    public bool AllowDead;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // Cheap inventory gate before world / ally scans.
        if (!medical.HasOwnedMedTools(owner, AllowKits, AllowMedipens))
            return Invert;

        EntityUid patient = owner;
        if (!Self)
        {
            if (!blackboard.TryGetValue<EntityUid>(HealTargetKey, out patient, _entManager))
            {
                // Branch preconditions run before PickHealTarget — probe for a suitable ally.
                if (!CritAllyPensOnly)
                    return Invert;

                var range = medical.GetMedSearchRange(blackboard);
                if (!medical.TryPickHealAlly(owner, range, out var ally, critOnly: true))
                    return Invert;

                patient = ally.Value;
            }
        }

        var has = medical.TrySelectBestOwnedHeal(owner, patient, AllowKits, AllowMedipens, out _, CritAllyPensOnly, AllowDead);
        return Invert ? !has : has;
    }
}
