using Content.Server.NPC.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Events;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Prevents same-squad friendly fire for ballistic / energy projectiles.
/// Hitscan ally skip lives in <see cref="Content.Server.Weapons.Ranged.Systems.GunSystem"/>.
/// </summary>
public sealed class NPCSquadFriendlyFireSystem : EntitySystem
{
    [Dependency] private readonly NPCSquadSystem _squads = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Must not subscribe on ProjectileComponent — SharedProjectileSystem already owns that.
        // PreventCollide is raised on both bodies, so handle it on the squad member being hit.
        SubscribeLocalEvent<NPCSquadMemberComponent, PreventCollideEvent>(OnSquadMemberPreventCollide);
    }

    private void OnSquadMemberPreventCollide(Entity<NPCSquadMemberComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (!TryComp(args.OtherEntity, out ProjectileComponent? projectile))
            return;

        if (projectile.Shooter is not { } shooter)
            return;

        if (_squads.AreSameSquad(shooter, ent.Owner))
            args.Cancelled = true;
    }
}
