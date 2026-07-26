using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the NPC can fill an incomplete compatible magazine (loose carts or transfer boxes).
/// </summary>
public sealed partial class InventoryHasMagazineFillMaterialPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.HasMagazineFillMaterial(owner, gun);
        ammo.DebugAmmo(owner, $"InventoryHasMagazineFillMaterial => {has}");
        return Invert ? !has : has;
    }
}
