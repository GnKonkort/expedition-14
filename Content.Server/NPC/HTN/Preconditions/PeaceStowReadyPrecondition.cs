using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC has not been in active gun/melee combat for
/// <see cref="NPCGunAmmoSystem.PeaceStowDelaySeconds"/> and still holds a gun or loose ammo.
/// </summary>
public sealed partial class PeaceStowReadyPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var ready = ammo.IsPeaceStowReady(owner, blackboard);
        return Invert ? !ready : ready;
    }
}
