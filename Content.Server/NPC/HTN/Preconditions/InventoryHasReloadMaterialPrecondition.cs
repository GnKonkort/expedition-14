using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC's inventory has usable reload material for the held gun.
/// </summary>
public sealed partial class InventoryHasReloadMaterialPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.HasUsableReloadMaterial(owner, gun);
        ammo.DebugAmmo(owner, $"InventoryHasReloadMaterial => {has}");
        return Invert ? !has : has;
    }
}
