using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when inventory has an empty MayTransfer ammo box matching the owned gun.
/// </summary>
public sealed partial class HasEmptyCompatibleAmmoBoxPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.TryFindEmptyCompatibleAmmoBox(owner, gun, out _);
        return Invert ? !has : has;
    }
}
