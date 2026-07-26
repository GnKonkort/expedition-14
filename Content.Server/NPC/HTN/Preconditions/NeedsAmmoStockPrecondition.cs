using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC should stock ammo before looking for a fight
/// (fill mags, collect mags/boxes/cartridges, discard empty boxes).
/// </summary>
public sealed partial class NeedsAmmoStockPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return Invert;

        var needs = ammo.NeedsAmmoStock(owner, gun, ammo.GetAmmoSearchRange(blackboard));
        ammo.DebugAmmo(owner, $"NeedsAmmoStock => {needs}");
        return Invert ? !needs : needs;
    }
}
