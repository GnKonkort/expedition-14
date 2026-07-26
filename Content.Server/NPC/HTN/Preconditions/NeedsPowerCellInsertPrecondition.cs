using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when a power-cell energy gun needs a cell insert or a better spare cell.
/// </summary>
public sealed partial class NeedsPowerCellInsertPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.NeedsPowerCellInsert(owner, gun);
        ammo.DebugAmmo(owner, $"NeedsPowerCellInsertPrecondition => {has} (invert={Invert})", force: true);
        return Invert ? !has : has;
    }
}
