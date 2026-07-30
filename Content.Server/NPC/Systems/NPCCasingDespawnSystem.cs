using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Spawners;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Despawns spent casings from NPC-fired guns after a short delay so NPCs don't litter.
/// </summary>
public sealed class NPCCasingDespawnSystem : EntitySystem
{
    private const float DespawnLifetime = 15f;

    [Dependency] private readonly SharedContainerSystem _containers = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<NPCSpentCasingComponent, EntGotRemovedFromContainerMessage>(OnCasingRemoved);
    }

    private void OnGunShot(EntityUid uid, GunComponent component, ref GunShotEvent args)
    {
        if (!IsNpcShooter(args.User))
            return;

        foreach (var (ammoUid, _) in args.Ammo)
        {
            if (ammoUid is not { } casing)
                continue;

            if (TerminatingOrDeleted(casing))
                continue;

            if (!TryComp<CartridgeAmmoComponent>(casing, out var cartridge))
                continue;

            // Caseless / already deleted on fire — nothing left to clean up.
            if (!cartridge.Spent || cartridge.DeleteOnSpawn)
                continue;

            EnsureComp<NPCSpentCasingComponent>(casing);
            TryStartDespawn(casing);
        }
    }

    private void OnCasingRemoved(EntityUid uid, NPCSpentCasingComponent component, EntGotRemovedFromContainerMessage args)
    {
        TryStartDespawn(uid);
    }

    private void TryStartDespawn(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid))
            return;

        // Still chambered / in mag / cylinder — wait until ejected onto the map.
        if (_containers.IsEntityInContainer(uid))
            return;

        if (HasComp<TimedDespawnComponent>(uid))
            return;

        var timed = AddComp<TimedDespawnComponent>(uid);
        timed.Lifetime = DespawnLifetime;
    }

    private bool IsNpcShooter(EntityUid uid)
    {
        return HasComp<HTNComponent>(uid) && !HasComp<ActorComponent>(uid);
    }
}
