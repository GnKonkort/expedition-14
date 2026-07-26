using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when a nearby floor bag/belt contains compatible ammo for the held gun.
/// </summary>
public sealed partial class NearbyAmmoStoragePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.HasNearbyAmmoStorage(owner, gun, ammo.GetAmmoSearchRange(blackboard));
        ammo.DebugAmmo(owner, $"NearbyAmmoStorage => {has}");
        return Invert ? !has : has;
    }
}
