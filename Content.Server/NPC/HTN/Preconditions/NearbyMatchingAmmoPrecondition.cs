using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when matching ammo for the held gun exists nearby (loose or storage).
/// </summary>
public sealed partial class NearbyMatchingAmmoPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.HasCompatibleAmmoNearby(owner, gun, ammo.GetAmmoSearchRange(blackboard));
        ammo.DebugAmmo(owner, $"NearbyMatchingAmmo => {has}");
        return Invert ? !has : has;
    }
}
