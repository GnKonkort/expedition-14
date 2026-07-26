using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the held ballistic tube/internal magazine is below capacity and feed ammo exists.
/// </summary>
public sealed partial class NeedsBallisticTubeFillPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var has = ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) &&
                  ammo.NeedsBallisticTubeFill(owner, gun);
        ammo.DebugAmmo(owner, $"NeedsBallisticTubeFillPrecondition => {has}");
        return Invert ? !has : has;
    }
}
