using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when an empty external-charge energy gun has no portable recharger and should be discarded.
/// </summary>
public sealed partial class ShouldDiscardEnergyGunPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.ShouldDiscardEnergyGun(owner, gun);
        ammo.DebugAmmo(owner, $"ShouldDiscardEnergyGunPrecondition => {has} (invert={Invert})", force: true);
        return Invert ? !has : has;
    }
}
