using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the held magazine-fed gun needs a spare loaded magazine inserted
/// (slot empty/missing or seated mag empty). Chamber ammo is ignored.
/// </summary>
public sealed partial class NeedsMagazineInsertPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.NeedsMagazineInsert(owner, gun);
        ammo.DebugAmmo(owner, $"NeedsMagazineInsertPrecondition => {has}");
        return Invert ? !has : has;
    }
}
