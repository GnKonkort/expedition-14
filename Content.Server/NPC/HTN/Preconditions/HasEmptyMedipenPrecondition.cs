using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC is carrying a used / empty recognized medipen.
/// </summary>
public sealed partial class HasEmptyMedipenPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = medical.TryFindEmptyMedipen(owner, out _);
        return Invert ? !has : has;
    }
}
