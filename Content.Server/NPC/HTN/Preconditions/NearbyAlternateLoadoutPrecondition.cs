using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when a different ammo family gun/ammo loadout is nearby.
/// </summary>
public sealed partial class NearbyAlternateLoadoutPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.HasAlternateLoadoutNearby(owner, gun, ammo.GetAmmoSearchRange(blackboard));
        ammo.DebugAmmo(owner, $"NearbyAlternateLoadout => {has}");
        return Invert ? !has : has;
    }
}
