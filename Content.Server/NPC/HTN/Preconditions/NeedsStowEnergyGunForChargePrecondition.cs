using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when an energy gun must be holstered / docked for recharge right now (held or needs charger insert).
/// Does not keep the NPC idle — after stow, other HTN branches (melee, idle) can run.
/// </summary>
public sealed partial class NeedsStowEnergyGunForChargePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.NeedsStowEnergyGunForCharge(owner, gun, blackboard);
        ammo.DebugAmmo(owner, $"NeedsStowEnergyGunForChargePrecondition => {has} (invert={Invert})");
        return Invert ? !has : has;
    }
}
