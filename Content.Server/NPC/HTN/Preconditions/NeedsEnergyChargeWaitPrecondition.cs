using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True while an energy gun must not be used for ranged combat (empty / mid-recharge until full).
/// Does not idle the NPC — after stow, melee / other HTN branches can run.
/// </summary>
public sealed partial class NeedsEnergyChargeWaitPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.NeedsEnergyChargeWait(owner, gun, blackboard);
        ammo.DebugAmmo(owner, $"NeedsEnergyChargeWaitPrecondition => {has} (invert={Invert})");
        return Invert ? !has : has;
    }
}
