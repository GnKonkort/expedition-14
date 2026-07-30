using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Helpers for humanoid NPC ammo logistics: magazine/ballistic compatibility,
/// inventory search (slots + one Storage level), and loaded-mag counting.
/// </summary>
public sealed partial class NPCGunAmmoSystem : EntitySystem
{
    public const string MagazineSlot = "gun_magazine";
    public const string ChamberSlot = "gun_chamber";
    public const string AmmoSearchReadyAt = "AmmoSearchReadyAt";
    public const string AmmoSearchRangeKey = "AmmoSearchRange";
    /// <summary>
    /// Blackboard key: last gun this NPC claimed for ammo logistics (held or stowed on body).
    /// </summary>
    public const string OwnedGunKey = "OwnedGun";
    /// <summary>
    /// Hostiles closer than this block floor/bag ammo looting (under fire).
    /// </summary>
    public const string AmmoLootHostileRangeKey = "AmmoLootHostileRange";
    public const float DefaultAmmoSearchRange = 7f;
    public const float DefaultAmmoLootHostileRange = 4f;
    public const float AmmoSearchCooldownSeconds = 4f;
    /// <summary>Illusion mag / energy "reload" duration (DoAfter).</summary>
    public const float IllusionReloadDurationSeconds = 2f;
    private static readonly SoundSpecifier IllusionMagReloadSound =
        new SoundPathSpecifier("/Audio/Weapons/Guns/MagIn/smg_magin.ogg");
    private static readonly SoundSpecifier IllusionEnergyReloadSound =
        new SoundCollectionSpecifier("sparks");
    /// <summary>
    /// Blackboard TimeSpan: last time this NPC was actively fighting (gun/melee operators).
    /// </summary>
    public const string LastCombatAtKey = "LastCombatAt";
    /// <summary>
    /// Seconds without active combat before the NPC holsters their gun and stows held ammo.
    /// </summary>
    public const float PeaceStowDelaySeconds = 15f;
    public const int MaxNearbyStorages = 10;
    public const int MaxNearbyLookupChecks = 128;
    /// <summary>Target number of compatible magazines (full/partial/empty) on the body.</summary>
    public const int MinCompatibleMags = 3;
    /// <summary>Legacy alias — loaded-mag fill target matches total mag stock goal.</summary>
    public const int MinLoadedCompatibleMags = MinCompatibleMags;
    /// <summary>Target power cells on the body: 1 seated in the gun + 2 spares.</summary>
    public const int MinCompatiblePowerCells = 3;
    /// <summary>Loose live cartridges to keep for tube/shotgun guns (plus one ammo box).</summary>
    public const int MinTubeLooseCartridges = 6;
    private const int MaxCarryParentHops = 10;

    /// <summary>
    /// Verbose NPC ammo logistics logging to the server console.
    /// Off by default — enable temporarily when debugging HTN ammo thrash.
    /// </summary>
    public static bool DebugAmmoLogging = false;

    private static readonly TimeSpan DebugThrottle = TimeSpan.FromSeconds(1.5);

    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly Content.Shared.Interaction.SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedGunSystem _guns = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;

    private EntityQuery<BallisticAmmoProviderComponent> _ballisticQuery;
    private EntityQuery<StorageComponent> _storageQuery;
    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<CartridgeAmmoComponent> _cartridgeQuery;

    private readonly Dictionary<EntityUid, TimeSpan> _debugNextLog = new();

    public override void Initialize()
    {
        base.Initialize();
        _ballisticQuery = GetEntityQuery<BallisticAmmoProviderComponent>();
        _storageQuery = GetEntityQuery<StorageComponent>();
        _gunQuery = GetEntityQuery<GunComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _cartridgeQuery = GetEntityQuery<CartridgeAmmoComponent>();
        InitializeEnergy();
        SubscribeLocalEvent<NpcIllusionReloadDoAfterEvent>(OnIllusionReloadDoAfter);
    }

    /// <summary>
    /// Live (unfired) cartridge — spent casings are useless for reload/fill.
    /// </summary>
    public bool IsLiveCartridge(EntityUid item)
    {
        return _cartridgeQuery.TryGetComponent(item, out var cart) && !cart.Spent;
    }

    /// <summary>
    /// True when the magazine is currently seated in some gun's magazine slot.
    /// </summary>
    public bool IsSeatedMagazine(EntityUid magazine)
    {
        return _containers.TryGetContainingContainer((magazine, null, null), out var container) &&
               container.ID == MagazineSlot;
    }

    /// <summary>
    /// Throttled server-console debug for a specific NPC (prefix [npc.ammo]).
    /// </summary>
    public void DebugAmmo(EntityUid owner, string message, bool force = false)
    {
        if (!DebugAmmoLogging)
            return;

        if (!force)
        {
            var now = _timing.CurTime;
            if (_debugNextLog.TryGetValue(owner, out var next) && now < next)
                return;

            _debugNextLog[owner] = now + DebugThrottle;
        }

        Log.Info($"[npc.ammo] {ToPrettyString(owner)}: {message}");
    }

    public string DescribeGunAmmoState(EntityUid owner, EntityUid gun)
    {
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        EntityUid? seated = null;
        if (TryGetGunMagazineSlot(gun, out var magSlot))
            seated = magSlot.Item;

        var active = _hands.GetActiveItem(owner);
        return
            $"gun={ToPrettyString(gun)} count={ev.Count}/{ev.Capacity} magFed={IsMagazineFed(gun)} " +
            $"seated={(seated == null ? "null" : ToPrettyString(seated.Value))} " +
            $"active={(active == null ? "null" : ToPrettyString(active.Value))}";
    }

    /// <summary>
    /// True while an illusion reload DoAfter is running on this NPC.
    /// </summary>
    public bool IsIllusionReloading(EntityUid owner)
    {
        return TryGetActiveIllusionReload(owner, out _);
    }

    public bool TryGetActiveIllusionReload(EntityUid owner, out ushort id)
    {
        id = 0;
        if (!TryComp<DoAfterComponent>(owner, out var comp))
            return false;

        foreach (var (index, doAfter) in comp.DoAfters)
        {
            if (doAfter.Cancelled || doAfter.Completed)
                continue;

            if (doAfter.Args.Event is NpcIllusionReloadDoAfterEvent)
            {
                id = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts (or continues) a timed illusion reload: Hidden DoAfter + SFX, then refill + bolt close.
    /// Returns true when the gun has usable ammo after this call (already full, or DoAfter just finished).
    /// Returns false while waiting / if start failed.
    /// </summary>
    public bool TryIllusionReload(EntityUid owner, EntityUid gun, GunComponent? gunComp = null)
    {
        if (!_gunQuery.Resolve(gun, ref gunComp, false))
            return false;

        // Prefer shooting from this gun after refill.
        if (_hands.IsHolding(owner, gun))
            _hands.TrySelect(owner, gun);
        else
            TryDrawOwnedGun(owner, gun);

        // Mag may already be full with bolt open after an empty cycle — rack before early-out.
        EnsureChamberReady(gun, owner);

        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        if (ev.Count > 0)
            return true;

        if (IsIllusionReloading(owner))
            return false;

        return TryStartIllusionReload(owner, gun);
    }

    /// <summary>
    /// Starts Hidden DoAfter + reload/short-circuit SFX. Does not refill until DoAfter finishes.
    /// </summary>
    public bool TryStartIllusionReload(EntityUid owner, EntityUid gun)
    {
        if (!Exists(owner) || !Exists(gun))
            return false;

        if (IsIllusionReloading(owner))
            return true;

        EnsureComp<DoAfterComponent>(owner);

        var energy = IsEnergyGun(gun) || IsPowerCellSwapGun(gun);
        var sound = energy ? IllusionEnergyReloadSound : IllusionMagReloadSound;
        _audio.PlayPvs(sound, gun);

        var args = new DoAfterArgs(EntityManager, owner, IllusionReloadDurationSeconds,
            new NpcIllusionReloadDoAfterEvent(), eventTarget: owner, used: gun)
        {
            Broadcast = true,
            Hidden = true,
            BreakOnMove = false,
            BreakOnDamage = true,
            NeedHand = false,
            CancelDuplicate = true,
            DuplicateCondition = DuplicateConditions.SameEvent,
        };

        if (!_doAfter.TryStartDoAfter(args))
        {
            DebugAmmo(owner, "IllusionReload DoAfter failed to start", force: true);
            return false;
        }

        DebugAmmo(owner, $"IllusionReload started energy={energy} {DescribeGunAmmoState(owner, gun)}", force: true);
        return true;
    }

    private void OnIllusionReloadDoAfter(NpcIllusionReloadDoAfterEvent args)
    {
        if (args.Cancelled)
            return;

        var owner = args.User;
        if (args.Used is not { } gun || !Exists(gun))
            return;

        ApplyIllusionReload(owner, gun);
    }

    /// <summary>
    /// Instant refill + chamber/bolt ready (no DoAfter). Used when the illusion wait finishes.
    /// </summary>
    public bool ApplyIllusionReload(EntityUid owner, EntityUid gun, GunComponent? gunComp = null)
    {
        if (!_gunQuery.Resolve(gun, ref gunComp, false))
            return false;

        if (_hands.IsHolding(owner, gun))
            _hands.TrySelect(owner, gun);
        else
            TryDrawOwnedGun(owner, gun);

        var filled = false;

        // Mag-fed guns often spawn without a seated mag (Frontier) — seat one from inventory first.
        if (IsMagazineFed(gun))
        {
            if (!TryGetSeatedMagazine(gun, out var mag))
            {
                if (TryFindCompatibleLoadedMagazine(owner, gun, out var spare) ||
                    TryFindIncompleteCompatibleMagazine(owner, gun, out spare))
                {
                    TryInsertMagazine(owner, gun, spare);
                }
            }

            if (TryGetSeatedMagazine(gun, out mag))
                filled |= TryFillAmmoProvider(mag);
        }

        // Chamber-fed: top off chamber ammo provider if present.
        if (TryGetGunChamberSlot(gun, out var chamber) && chamber.Item is { } chamberAmmo)
            filled |= TryFillAmmoProvider(chamberAmmo);

        filled |= TryFillAmmoProvider(gun);

        if (TryGetSeatedPowerCell(gun, out var cell) && TryComp<BatteryComponent>(cell, out var battery))
        {
            _battery.SetCharge(cell, battery.MaxCharge, battery);
            filled = true;
        }

        // Chamber-magazine pistols/SMGs need a closed bolt + live round or Count stays 0.
        EnsureChamberReady(gun, owner);

        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        if (ev.Count > 0)
            filled = true;

        DebugAmmo(owner, $"IllusionReload applied filled={filled} {DescribeGunAmmoState(owner, gun)}", force: true);
        return filled;
    }

    private bool TryFillAmmoProvider(EntityUid provider)
    {
        if (_ballisticQuery.TryGetComponent(provider, out var ballistic))
        {
            var shots = ballistic.Entities.Count + ballistic.UnspawnedCount;
            if (shots >= ballistic.Capacity)
                return false;

            _guns.SetBallisticUnspawned((provider, ballistic),
                Math.Max(0, ballistic.Capacity - ballistic.Entities.Count));
            return true;
        }

        // BatteryAmmoProviderComponent is abstract — TryComp on it asserts "Unknown component".
        if ((_hitscanBatteryQuery.HasComponent(provider) || _projectileBatteryQuery.HasComponent(provider)) &&
            _batteryQuery.TryGetComponent(provider, out var bat))
        {
            _battery.SetCharge(provider, bat.MaxCharge, bat);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Quiet magazine-slot lookup — never Resolve()-errors on entities without ItemSlots (ammo boxes, energy guns).
    /// </summary>
    private bool TryGetGunMagazineSlot(EntityUid uid, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        if (!TryComp<ItemSlotsComponent>(uid, out var slots))
            return false;

        return _slots.TryGetSlot(uid, MagazineSlot, out slot, slots);
    }

    private bool TryGetGunChamberSlot(EntityUid uid, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        if (!TryComp<ItemSlotsComponent>(uid, out var slots))
            return false;

        return _slots.TryGetSlot(uid, ChamberSlot, out slot, slots);
    }

    public bool TryGetHeldGun(EntityUid owner, out EntityUid gunUid, [NotNullWhen(true)] out GunComponent? gun)
    {
        gunUid = default;
        gun = null;

        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (_gunQuery.TryGetComponent(held, out gun))
            {
                gunUid = held;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="item"/> is in the NPC's hands, inventory slots, or a container worn/held on their body.
    /// </summary>
    public bool IsCarriedBy(EntityUid owner, EntityUid item)
    {
        if (!Exists(item))
            return false;

        if (_hands.IsHolding(owner, item))
            return true;

        if (!_containers.TryGetContainingContainer((item, null, null), out var container))
            return false;

        var current = container.Owner;
        for (var hop = 0; hop < MaxCarryParentHops; hop++)
        {
            if (current == owner)
                return true;

            if (!_containers.TryGetContainingContainer((current, null, null), out container))
                return false;

            current = container.Owner;
        }

        return false;
    }

    public void RememberOwnedGun(NPCBlackboard? blackboard, EntityUid gun)
    {
        // HTN planning uses a readonly blackboard copy — never write during Plan/preconditions.
        if (blackboard == null || blackboard.ReadOnly)
            return;

        blackboard.SetValue(OwnedGunKey, gun);
    }

    public void ForgetOwnedGun(NPCBlackboard? blackboard)
    {
        if (blackboard == null || blackboard.ReadOnly)
            return;

        blackboard.Remove<EntityUid>(OwnedGunKey);
    }

    /// <summary>
    /// Resolves this NPC's gun for ammo logistics: held first, then blackboard memory if still carried,
    /// then any gun currently on their body. Updates <see cref="OwnedGunKey"/> when the blackboard is writable.
    /// </summary>
    public bool TryGetOwnedGun(
        EntityUid owner,
        out EntityUid gunUid,
        [NotNullWhen(true)] out GunComponent? gun,
        NPCBlackboard? blackboard = null)
    {
        gunUid = default;
        gun = null;

        if (TryGetHeldGun(owner, out gunUid, out gun))
        {
            RememberOwnedGun(blackboard, gunUid);
            return true;
        }

        if (blackboard != null &&
            blackboard.TryGetValue<EntityUid>(OwnedGunKey, out var remembered, EntityManager) &&
            Exists(remembered) &&
            IsCarriedBy(owner, remembered) &&
            _gunQuery.TryGetComponent(remembered, out gun))
        {
            gunUid = remembered;
            return true;
        }

        foreach (var candidate in EnumerateCarriedGuns(owner))
        {
            if (!_gunQuery.TryGetComponent(candidate, out gun))
                continue;

            gunUid = candidate;
            RememberOwnedGun(blackboard, gunUid);
            return true;
        }

        ForgetOwnedGun(blackboard);
        return false;
    }

    public IEnumerable<EntityUid> EnumerateCarriedGuns(EntityUid owner)
    {
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (_gunQuery.HasComponent(item))
                yield return item;

            // Energy gun docked in a worn portable recharger.
            if (_chargerQuery.TryGetComponent(item, out var charger) &&
                charger.Portable &&
                _slots.TryGetSlot(item, charger.SlotId, out var slot) &&
                slot.Item is { } docked &&
                _gunQuery.HasComponent(docked))
            {
                yield return docked;
            }
        }
    }

    /// <summary>
    /// Moves a held item into body storage first, then free inventory slots.
    /// Storage-first avoids pocket thrash that can eject a stowed gun when equipping a mag/box.
    /// Already-carried (not held) items count as success. Prefer this over dropping to the floor.
    /// </summary>
    public bool TryStowItem(EntityUid owner, EntityUid item)
    {
        if (!Exists(item))
            return false;

        // Wield placeholders are not real items — never try to bag them.
        if (HasComp<VirtualItemComponent>(item))
            return false;

        if (!_hands.IsHolding(owner, item))
            return IsCarriedBy(owner, item);

        var storages = new List<EntityUid>();
        foreach (var storageEnt in EnumerateBodyStorages(owner))
        {
            if (storageEnt != item)
                storages.Add(storageEnt);
        }

        foreach (var storageUid in storages)
        {
            if (!_storage.CanInsert(storageUid, item, out _))
                continue;

            if (_storage.Insert(storageUid, item, out _, user: owner, playSound: false))
            {
                DebugAmmo(owner, $"TryStowItem inserted → {ToPrettyString(storageUid)} {ToPrettyString(item)}");
                return true;
            }
        }

        if (_inventory.TryGetSlots(owner, out var slots))
        {
            foreach (var slot in slots)
            {
                if (!_inventory.TryGetSlotContainer(owner, slot.Name, out var container, out _))
                    continue;

                if (container.ContainedEntity != null)
                    continue;

                if (_inventory.TryEquip(owner, item, slot.Name, silent: true))
                {
                    DebugAmmo(owner, $"TryStowItem equipped → slot '{slot.Name}' {ToPrettyString(item)}");
                    return true;
                }
            }
        }

        DebugAmmo(owner, $"TryStowItem FAIL {ToPrettyString(item)}");
        return false;
    }

    private static readonly string[] PreferredGunEquipSlots = ["suitstorage", "back", "belt", "pocket1", "pocket2"];

    /// <summary>
    /// Moves a held gun into any free inventory slot that accepts it, or into a storage container on the body.
    /// Already-carried (stowed) guns count as success. Prefers suitstorage/back over pockets.
    /// </summary>
    public bool TryStowGun(EntityUid owner, EntityUid gun)
    {
        if (!Exists(gun) || !_gunQuery.HasComponent(gun))
            return false;

        TryUnwieldIfNeeded(owner, gun);

        if (!_hands.IsHolding(owner, gun))
            return IsCarriedBy(owner, gun);

        // Prefer docking chargeable energy guns into the portable recharger.
        if (IsEnergyGun(gun) && !IsPowerCellSwapGun(gun) && TryInsertGunIntoPortableRecharger(owner, gun))
            return true;

        // Prefer back / suit storage so shotguns hang on the body instead of filling pockets.
        foreach (var slotName in PreferredGunEquipSlots)
        {
            if (!_inventory.TryGetSlotContainer(owner, slotName, out var container, out _))
                continue;

            if (container.ContainedEntity != null)
                continue;

            if (_inventory.TryEquip(owner, gun, slotName, silent: true))
            {
                DebugAmmo(owner, $"TryStowGun equipped → slot '{slotName}' {ToPrettyString(gun)}");
                return true;
            }
        }

        return TryStowItem(owner, gun);
    }

    /// <summary>
    /// Force-unwield so the off-hand VirtualItem is cleared before ammo work / holstering.
    /// </summary>
    public void TryUnwieldIfNeeded(EntityUid owner, EntityUid item)
    {
        if (!TryComp<WieldableComponent>(item, out var wield) || !wield.Wielded)
            return;

        _wieldable.TryUnwield(item, wield, owner);
    }

    /// <summary>
    /// Puts the owned gun into a hand (from pocket/backpack/belt/etc) and wields if required.
    /// </summary>
    public bool TryDrawOwnedGun(EntityUid owner, EntityUid gun)
    {
        if (_hands.IsHolding(owner, gun))
        {
            EnsureChamberReady(gun, owner);
            TryEnsureWielded(owner, gun);
            return true;
        }

        var ok = TryObtainInHand(owner, gun);
        DebugAmmo(owner, $"TryDrawOwnedGun {ToPrettyString(gun)} => {ok}");
        if (!ok)
            return false;

        EnsureChamberReady(gun, owner);
        TryEnsureWielded(owner, gun);
        return true;
    }

    /// <summary>
    /// Occupies both hands for a wieldable weapon (rifle/shotgun/spear/etc).
    /// Stows other held items first so TryWield does not drop ammo on the floor.
    /// </summary>
    public bool TryEnsureWielded(EntityUid owner, EntityUid item)
    {
        if (!TryComp<WieldableComponent>(item, out var wield))
            return true;

        if (wield.Wielded)
            return true;

        if (!_hands.IsHolding(owner, item))
        {
            DebugAmmo(owner, $"TryEnsureWielded FAIL: not holding {ToPrettyString(item)}");
            return false;
        }

        // Free the off-hand(s) without dropping logistics onto the floor.
        var heldSnapshot = new List<EntityUid>();
        foreach (var held in _hands.EnumerateHeld(owner))
            heldSnapshot.Add(held);

        foreach (var held in heldSnapshot)
        {
            if (held == item)
                continue;

            if (HasComp<VirtualItemComponent>(held))
                continue;

            if (_gunQuery.HasComponent(held))
            {
                if (!TryStowGunToStorage(owner, held))
                    TryStowGun(owner, held);
            }
            else
            {
                TryStowItem(owner, held);
            }
        }

        var ok = _wieldable.TryWield(item, wield, owner);
        DebugAmmo(owner, $"TryEnsureWielded {ToPrettyString(item)} => {ok} (wielded={wield.Wielded})");
        return ok;
    }

    /// <summary>
    /// Ensures the owned gun is stowed on the body so both hands are free for magazine/ammo work.
    /// Prefers backpack/storage so pockets stay free for magazines.
    /// </summary>
    public bool TryEnsureGunStowedForAmmoWork(EntityUid owner, NPCBlackboard blackboard, out EntityUid gun)
    {
        if (!TryGetOwnedGun(owner, out gun, out _, blackboard))
        {
            DebugAmmo(owner, "TryEnsureGunStowedForAmmoWork FAIL: no owned gun");
            return false;
        }

        if (!_hands.IsHolding(owner, gun))
            return true;

        if (TryStowGunToStorage(owner, gun))
            return true;

        return TryStowGun(owner, gun);
    }

    private IEnumerable<EntityUid> EnumerateBodyStorages(EntityUid owner)
    {
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (_storageQuery.HasComponent(held))
                yield return held;
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var enumerator))
            yield break;

        while (enumerator.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } child)
                continue;

            if (_storageQuery.HasComponent(child))
                yield return child;
        }
    }

    /// <summary>
    /// True for magazine-fed guns (including ChamberMagazine pistols/SMGs).
    /// Must use the item slot — HasComp&lt;MagazineAmmoProvider&gt; is false for ChamberMagazine.
    /// </summary>
    public bool IsMagazineFed(EntityUid gun)
    {
        return TryGetGunMagazineSlot(gun, out _);
    }

    /// <summary>
    /// True if <paramref name="item"/> is a magazine that fits the gun's magazine slot,
    /// a cartridge for the chamber, an ammo box that feeds this caliber, or ballistic feed ammo.
    /// </summary>
    public bool IsAmmoCompatible(EntityUid gun, EntityUid item)
    {
        if (gun == item)
            return false;

        // Never treat spent casings as usable ammo.
        if (_cartridgeQuery.TryGetComponent(item, out var spentCart) && spentCart.Spent)
            return false;

        // Empty transfer boxes are discarded, not collected.
        if (_ballisticQuery.TryGetComponent(item, out var maybeBox) &&
            maybeBox.MayTransfer &&
            GetAmmoCount(item) <= 0)
            return false;

        // Ammo boxes / speedloaders: match by caliber whitelist overlap, NOT by treating the
        // box entity as a ShellShotgun/cartridge (boxes rarely carry those tags themselves).
        if (IsCompatibleAmmoBox(gun, item))
            return true;

        if (IsCompatiblePowerCell(gun, item))
            return true;

        if (IsMagazineFed(gun))
        {
            if (TryGetGunMagazineSlot(gun, out var magSlot) &&
                !_whitelist.IsWhitelistFail(magSlot.Whitelist, item) &&
                !_whitelist.IsBlacklistPass(magSlot.Blacklist, item))
                return true;

            // Loose cartridges for chamber-magazine pistols/SMGs/rifles.
            if (TryGetGunChamberSlot(gun, out var chamberSlot) &&
                !_whitelist.IsWhitelistFail(chamberSlot.Whitelist, item) &&
                !_whitelist.IsBlacklistPass(chamberSlot.Blacklist, item))
                return true;
        }

        if (_ballisticQuery.TryGetComponent(gun, out var ballistic) &&
            !_whitelist.IsWhitelistFailOrNull(ballistic.Whitelist, item))
            return true;

        return false;
    }

    /// <summary>
    /// True if item is a magazine compatible with this magazine-fed gun (not a loose cartridge).
    /// </summary>
    public bool IsCompatibleMagazine(EntityUid gun, EntityUid item)
    {
        if (!IsMagazineFed(gun))
            return false;

        if (!TryGetGunMagazineSlot(gun, out var magSlot))
            return false;

        // Magazines are BallisticAmmoProvider; loose cartridges usually are not.
        if (!_ballisticQuery.HasComponent(item))
            return false;

        return !_whitelist.IsWhitelistFail(magSlot.Whitelist, item) &&
               !_whitelist.IsBlacklistPass(magSlot.Blacklist, item);
    }

    public bool IsCompatibleCartridgeFor(EntityUid provider, EntityUid cartridge)
    {
        if (_cartridgeQuery.TryGetComponent(cartridge, out var cart) && cart.Spent)
            return false;

        return _ballisticQuery.TryGetComponent(provider, out var ballistic) &&
               !_whitelist.IsWhitelistFailOrNull(ballistic.Whitelist, cartridge);
    }

    public int GetAmmoCount(EntityUid uid)
    {
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(uid, ref ev);
        return ev.Count;
    }

    public bool TryGetAmmoPercent(EntityUid gun, out float percent)
    {
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        if (ev.Capacity <= 0)
        {
            percent = 0f;
            return false;
        }

        percent = Math.Clamp(ev.Count / (float) ev.Capacity, 0f, 1f);
        return true;
    }

    public int CountCompatibleLoadedMags(EntityUid owner, EntityUid gun)
    {
        if (!IsMagazineFed(gun))
            return 0;

        var count = 0;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatibleMagazine(gun, item))
                continue;

            if (GetAmmoCount(item) > 0)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Compatible magazines on the body (seated + inventory), any fill level including empty.
    /// </summary>
    public int CountCompatibleMags(EntityUid owner, EntityUid gun)
    {
        if (!IsMagazineFed(gun))
            return 0;

        var count = 0;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (IsCompatibleMagazine(gun, item))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Live loose cartridges in inventory that fit a tube/ballistic gun (not inside a mag/box).
    /// </summary>
    public int CountLooseCompatibleCartridges(EntityUid owner, EntityUid gun)
    {
        if (IsMagazineFed(gun) || !_ballisticQuery.TryGetComponent(gun, out var gunBal))
            return 0;

        var count = 0;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!_cartridgeQuery.TryGetComponent(item, out var cart) || cart.Spent)
                continue;

            if (_whitelist.IsWhitelistFailOrNull(gunBal.Whitelist, item))
                continue;

            // Skip cartridges already inside a mag/box ballistic container.
            if (_containers.TryGetContainingContainer((item, null, null), out var container) &&
                (container.ID == "ballistic-ammo" || container.ID == MagazineSlot || container.ID == ChamberSlot))
                continue;

            count++;
        }

        return count;
    }

    public bool IsCompatibleAmmoBox(EntityUid gun, EntityUid item)
    {
        if (!_ballisticQuery.TryGetComponent(item, out var box) || !box.MayTransfer)
            return false;

        // NF magazines also set mayTransfer — they are stock, not ammo boxes.
        if (IsCompatibleMagazine(gun, item) || IsMagazinePrototype(item))
            return false;

        if (IsMagazineFed(gun))
        {
            if (TryGetGunChamberSlot(gun, out var chamber))
                return WhitelistTagsOverlap(box.Whitelist, chamber.Whitelist);

            if (TryGetGunMagazineSlot(gun, out var magSlot))
                return WhitelistTagsOverlap(box.Whitelist, magSlot.Whitelist);

            return false;
        }

        return _ballisticQuery.TryGetComponent(gun, out var gunBal) &&
               WhitelistTagsOverlap(box.Whitelist, gunBal.Whitelist);
    }

    public bool HasNonEmptyCompatibleAmmoBox(EntityUid owner, EntityUid gun)
    {
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (IsCompatibleAmmoBox(gun, item) && GetAmmoCount(item) > 0)
                return true;
        }

        return false;
    }

    public bool TryFindEmptyCompatibleAmmoBox(EntityUid owner, EntityUid gun, out EntityUid box)
    {
        box = default;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatibleAmmoBox(gun, item))
                continue;

            if (GetAmmoCount(item) > 0)
                continue;

            box = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops one empty matching ammo box from inventory. Returns true if a box was discarded.
    /// </summary>
    public bool TryDiscardEmptyAmmoBox(EntityUid owner, EntityUid gun)
    {
        if (!TryFindEmptyCompatibleAmmoBox(owner, gun, out var box))
            return false;

        if (!TryObtainInHand(owner, box))
        {
            DebugAmmo(owner, $"TryDiscardEmptyAmmoBox FAIL obtain {ToPrettyString(box)}");
            return false;
        }

        var ok = _hands.TryDrop(owner, box, checkActionBlocker: false);
        DebugAmmo(owner, $"TryDiscardEmptyAmmoBox {ToPrettyString(box)} => {ok}");
        return ok;
    }

    /// <summary>
    /// True when the NPC should stock ammo before seeking a fight (fill, collect mags/box/carts, discard empties).
    /// </summary>
    public bool NeedsAmmoStock(EntityUid owner, EntityUid gun, float range = DefaultAmmoSearchRange)
    {
        if (TryFindEmptyCompatibleAmmoBox(owner, gun, out _))
            return true;

        if (TryFindDrainedDisposablePowerCell(owner, gun, out _))
            return true;

        if (IsPowerCellSwapGun(gun))
        {
            if (NeedsPowerCellInsert(owner, gun))
                return true;

            // Under MinCompatiblePowerCells, or a floor cell that beats the worst owned spare.
            return TryFindFirstCompatibleAmmoNearby(owner, gun, range);
        }

        if (IsMagazineFed(gun))
        {
            if (HasMagazineFillMaterial(owner, gun))
                return true;

            if (NeedsMagazineInsert(owner, gun))
                return true;
        }
        else if (NeedsBallisticTubeFill(owner, gun))
        {
            return true;
        }

        // Existence only — no need to rank every shell on the floor.
        return TryFindFirstCompatibleAmmoNearby(owner, gun, range);
    }

    /// <summary>
    /// True if inventory has something useful for a self-reload / mag-fill step.
    /// Also true when an incomplete mag is already carried and matching fill ammo is nearby on the floor.
    /// </summary>
    public bool HasUsableReloadMaterial(EntityUid owner, EntityUid gun)
    {
        if (IsMagazineFed(gun))
        {
            if (NeedsMagazineInsert(owner, gun))
            {
                DebugAmmo(owner, "HasUsableReloadMaterial: NeedsMagazineInsert");
                return true;
            }

            if (HasMagazineFillMaterial(owner, gun))
            {
                DebugAmmo(owner, "HasUsableReloadMaterial: HasMagazineFillMaterial");
                return true;
            }

            // Empty/incomplete mag in hands/inv + box/carts on floor → should fill, not thrash-pickup.
            if (TryFindIncompleteCompatibleMagazine(owner, gun, out var incomplete) &&
                TryFindNearbyFillMaterial(owner, gun, incomplete, out _))
            {
                DebugAmmo(owner, "HasUsableReloadMaterial: incomplete mag + nearby fill");
                return true;
            }
        }

        if (IsPowerCellSwapGun(gun) && NeedsPowerCellInsert(owner, gun))
        {
            DebugAmmo(owner, "HasUsableReloadMaterial: NeedsPowerCellInsert");
            return true;
        }

        if (NeedsBallisticTubeFill(owner, gun))
        {
            DebugAmmo(owner, "HasUsableReloadMaterial: NeedsBallisticTubeFill");
            return true;
        }

        DebugAmmo(owner, "HasUsableReloadMaterial: NONE");
        return false;
    }

    /// <summary>
    /// Magazine slot empty/missing or seated mag empty, and a spare loaded mag exists.
    /// Will not steal a mag that is still being filled (incomplete + fill material available).
    /// </summary>
    public bool NeedsMagazineInsert(EntityUid owner, EntityUid gun)
    {
        if (!IsMagazineFed(gun))
            return false;

        if (!TryFindCompatibleLoadedMagazine(owner, gun, out var spare))
            return false;

        // Let FillMagazine finish topping off this spare before inserting it.
        if (IsProviderBelowCapacity(spare) && HasFillMaterialFor(owner, gun, spare))
            return false;

        if (!TryGetGunMagazineSlot(gun, out var magSlot) || magSlot.Item is not { } seated)
        {
            if (DebugAmmoLogging)
                DebugAmmo(owner, $"NeedsMagazineInsert=true (no seated, spare={ToPrettyString(spare)})");
            return true;
        }

        var seatedCount = GetAmmoCount(seated);
        var needs = seatedCount <= 0;
        if (needs && DebugAmmoLogging)
            DebugAmmo(owner, $"NeedsMagazineInsert=true (seated empty={ToPrettyString(seated)}, spare={ToPrettyString(spare)})");
        return needs;
    }

    /// <summary>
    /// True when cartridges or a MayTransfer box for <paramref name="provider"/> are available (inventory or floor).
    /// </summary>
    public bool HasFillMaterialFor(EntityUid owner, EntityUid gun, EntityUid provider)
    {
        return TryFindCompatibleCartridge(owner, provider, out _) ||
               TryFindMayTransferAmmoBox(owner, gun, provider, out _) ||
               TryFindNearbyFillMaterial(owner, gun, provider, out _);
    }

    /// <summary>
    /// Ballistic tube/internal mag below capacity with a loose cartridge or transfer box available.
    /// </summary>
    public bool NeedsBallisticTubeFill(EntityUid owner, EntityUid gun)
    {
        if (IsMagazineFed(gun))
            return false;

        if (!_ballisticQuery.TryGetComponent(gun, out _))
            return false;

        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        if (ev.Capacity <= 0 || ev.Count >= ev.Capacity)
            return false;

        var hasCart = TryFindCompatibleCartridge(owner, gun, out var cart);
        var hasBox = TryFindMayTransferAmmoBox(owner, gun, gun, out var box);
        var needs = hasCart || hasBox;
        if (needs && DebugAmmoLogging)
        {
            DebugAmmo(owner,
                $"NeedsBallisticTubeFill=true count={ev.Count}/{ev.Capacity} cart={(hasCart ? ToPrettyString(cart) : "null")} box={(hasBox ? ToPrettyString(box) : "null")}");
        }

        return needs;
    }

    /// <summary>
    /// Incomplete compatible mag (seated or spare) plus cartridges / MayTransfer box to fill it.
    /// </summary>
    public bool HasMagazineFillMaterial(EntityUid owner, EntityUid gun)
    {
        if (!IsMagazineFed(gun))
            return false;

        if (!TryFindIncompleteCompatibleMagazine(owner, gun, out var incomplete))
            return false;

        var hasCart = TryFindCompatibleCartridge(owner, incomplete, out var cart);
        var hasBox = TryFindMayTransferAmmoBox(owner, gun, incomplete, out var box);
        EntityUid nearby = default;
        var hasNearby = !hasCart && !hasBox &&
                        TryFindNearbyFillMaterial(owner, gun, incomplete, out nearby);
        var has = hasCart || hasBox || hasNearby;
        if (has && DebugAmmoLogging)
        {
            var total = CountCompatibleMags(owner, gun);
            var loaded = CountCompatibleLoadedMags(owner, gun);
            DebugAmmo(owner,
                $"HasMagazineFillMaterial=true mags={loaded}/{total} (goal {MinCompatibleMags}) mag={ToPrettyString(incomplete)} cart={(hasCart ? ToPrettyString(cart) : "null")} box={(hasBox ? ToPrettyString(box) : "null")} nearby={(hasNearby ? ToPrettyString(nearby) : "null")}");
        }

        return has;
    }

    /// <summary>
    /// Finds floor ammo that can fill <paramref name="provider"/> (MayTransfer box or loose cartridge).
    /// </summary>
    public bool TryFindNearbyFillMaterial(EntityUid owner, EntityUid gun, EntityUid provider, out EntityUid material)
    {
        material = default;
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var range = DefaultAmmoSearchRange;
        EntityUid? bestBox = null;
        EntityUid? bestCart = null;
        var bestBoxDist = float.MaxValue;
        var bestCartDist = float.MaxValue;

        foreach (var ent in _lookup.GetEntitiesInRange<BallisticAmmoProviderComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == gun || uid == provider)
                continue;
            if (_containers.IsEntityInContainer(uid) || IsCarriedBy(owner, uid))
                continue;
            if (!_ballisticQuery.TryGetComponent(uid, out var boxBal) || !boxBal.MayTransfer)
                continue;
            // Floor magazines are pickup targets, not fill-from-box sources.
            if (IsCompatibleMagazine(gun, uid) || IsMagazinePrototype(uid))
                continue;
            if (GetAmmoCount(uid) <= 0)
                continue;
            if (!_ballisticQuery.TryGetComponent(provider, out var providerBal))
                continue;
            if (!WhitelistTagsOverlap(boxBal.Whitelist, providerBal.Whitelist))
                continue;
            if (!TryComp(uid, out TransformComponent? tx) ||
                !xform.Coordinates.TryDistance(EntityManager, tx.Coordinates, out var dist))
                continue;
            if (dist >= bestBoxDist)
                continue;
            bestBoxDist = dist;
            bestBox = uid;
        }

        if (bestBox != null)
        {
            material = bestBox.Value;
            return true;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<CartridgeAmmoComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == gun || uid == provider)
                continue;
            if (_containers.IsEntityInContainer(uid) || IsCarriedBy(owner, uid))
                continue;
            if (!IsCompatibleCartridgeFor(provider, uid))
                continue;
            if (!TryComp(uid, out TransformComponent? tx) ||
                !xform.Coordinates.TryDistance(EntityManager, tx.Coordinates, out var dist))
                continue;
            if (dist >= bestCartDist)
                continue;
            bestCartDist = dist;
            bestCart = uid;
        }

        if (bestCart != null)
        {
            material = bestCart.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pulls nearby fill material onto the body/inventory so FillMagazine can use it.
    /// </summary>
    public bool TryPullNearbyFillMaterial(EntityUid owner, EntityUid gun, EntityUid incompleteMag)
    {
        if (TryFindCompatibleCartridge(owner, incompleteMag, out _) ||
            TryFindMayTransferAmmoBox(owner, gun, incompleteMag, out _))
            return true;

        if (!TryFindNearbyFillMaterial(owner, gun, incompleteMag, out var nearby))
            return false;

        var ok = TryStashItemOnBody(owner, nearby);
        DebugAmmo(owner, $"TryPullNearbyFillMaterial {ToPrettyString(nearby)} => {ok}");
        return ok;
    }

    public bool TryFindCompatibleLoadedMagazine(EntityUid owner, EntityUid gun, out EntityUid magazine)
    {
        magazine = default;
        if (!IsMagazineFed(gun))
            return false;

        // Prefer a spare mag not already seated in the gun.
        EntityUid? seated = null;
        if (TryGetGunMagazineSlot(gun, out var magSlot))
            seated = magSlot.Item;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (seated != null && item == seated)
                continue;

            if (!IsCompatibleMagazine(gun, item))
                continue;

            if (GetAmmoCount(item) <= 0)
                continue;

            magazine = item;
            return true;
        }

        return false;
    }

    public bool TryFindCompatibleCartridge(EntityUid owner, EntityUid provider, out EntityUid cartridge)
    {
        cartridge = default;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (item == provider)
                continue;

            // Skip MayTransfer boxes — those are handled separately.
            if (_ballisticQuery.TryGetComponent(item, out var bal) && bal.MayTransfer)
                continue;

            if (!IsCompatibleCartridgeFor(provider, item))
                continue;

            cartridge = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a MayTransfer ammo box/speedloader whose caliber matches <paramref name="provider"/>.
    /// Never returns a magazine — magazines set mayTransfer but are stock, not fill sources.
    /// </summary>
    public bool TryFindMayTransferAmmoBox(EntityUid owner, EntityUid gun, EntityUid provider, out EntityUid box)
    {
        box = default;
        if (!_ballisticQuery.TryGetComponent(provider, out var providerBal))
            return false;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (item == provider)
                continue;

            if (!_ballisticQuery.TryGetComponent(item, out var boxBal) || !boxBal.MayTransfer)
                continue;

            // Mag↔mag transfer causes an infinite 15↔16 ping-pong after chambering one round.
            if (IsCompatibleMagazine(gun, item) || IsMagazinePrototype(item))
                continue;

            if (GetAmmoCount(item) <= 0)
                continue;

            if (!WhitelistTagsOverlap(boxBal.Whitelist, providerBal.Whitelist))
                continue;

            box = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Instantly transfers one round from a MayTransfer box into a ballistic provider (mag/tube).
    /// Skips the player DoAfter so HTN can progress one round per tick.
    /// </summary>
    public bool TryTransferOneFromAmmoBox(EntityUid owner, EntityUid box, EntityUid target)
    {
        DebugAmmo(owner, $"TryTransferOneFromAmmoBox box={ToPrettyString(box)} target={ToPrettyString(target)}");

        if (!_ballisticQuery.TryGetComponent(box, out var boxBal) || !boxBal.MayTransfer)
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: not MayTransfer box");
            return false;
        }

        // Hard guard: never pour one magazine into another (magazines also set mayTransfer).
        if (IsMagazinePrototype(box))
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: source is a magazine");
            return false;
        }

        if (!_ballisticQuery.TryGetComponent(target, out var targetBal))
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: target not ballistic");
            return false;
        }

        var before = new GetAmmoCountEvent();
        RaiseLocalEvent(target, ref before);
        if (before.Capacity > 0 && before.Count >= before.Capacity)
        {
            DebugAmmo(owner, $"TryTransferOneFromAmmoBox FAIL: target full {before.Count}/{before.Capacity}");
            return false;
        }

        if (GetAmmoCount(box) <= 0)
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: box empty");
            return false;
        }

        var seated = IsSeatedMagazine(target);
        // Seated: fill in-place. Spare/tube: must be held or InteractUsing drops the round.
        if (!seated)
        {
            if (!_hands.IsHolding(owner, target) && !TryObtainInHand(owner, target))
            {
                DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: obtain target mag");
                return false;
            }

            // Need a free hand for the box — don't holster the tube/gun we're filling.
            TryUnwieldIfNeeded(owner, target);
        }

        if (!TryObtainInHand(owner, box))
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: obtain box");
            return false;
        }

        if (!seated && !_hands.IsHolding(owner, target) && !TryObtainInHand(owner, target))
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: re-obtain target mag");
            return false;
        }

        var ammo = new List<(EntityUid? Entity, IShootable Shootable)>();
        var coords = Transform(box).Coordinates;
        var take = new TakeAmmoEvent(1, ammo, coords, owner);
        RaiseLocalEvent(box, take);

        if (ammo.Count == 0 || ammo[0].Entity is not { } round)
        {
            DebugAmmo(owner, "TryTransferOneFromAmmoBox FAIL: TakeAmmo returned nothing");
            return false;
        }

        if (_whitelist.IsWhitelistFailOrNull(targetBal.Whitelist, round))
        {
            DebugAmmo(owner, $"TryTransferOneFromAmmoBox FAIL: whitelist {ToPrettyString(round)}");
            _interaction.InteractUsing(owner, round, box, coords, checkCanInteract: false, checkCanUse: false);
            return false;
        }

        var targetCoords = Transform(target).Coordinates;
        var ok = _interaction.InteractUsing(owner, round, target, targetCoords, checkCanInteract: false, checkCanUse: false);

        var after = new GetAmmoCountEvent();
        RaiseLocalEvent(target, ref after);
        var loaded = after.Count > before.Count;

        if (!loaded)
        {
            // Cartridge likely fell on the floor — put it back into the box if possible.
            DebugAmmo(owner,
                $"TryTransferOneFromAmmoBox FAIL: mag count unchanged {before.Count}->{after.Count}, recovering round");
            if (Exists(round))
                _interaction.InteractUsing(owner, round, box, coords, checkCanInteract: false, checkCanUse: false);
            return false;
        }

        DebugAmmo(owner,
            $"TryTransferOneFromAmmoBox OK InteractUsing={ok} count {before.Count}->{after.Count}");
        return true;
    }

    /// <summary>
    /// Feeds a specific live cartridge into a ballistic provider (tube/mag).
    /// </summary>
    public bool TryFeedCartridgeIntoProvider(EntityUid owner, EntityUid provider, EntityUid cartridge)
    {
        if (!Exists(cartridge) || !IsCompatibleCartridgeFor(provider, cartridge))
        {
            DebugAmmo(owner, $"TryFeedCartridgeIntoProvider FAIL incompatible {ToPrettyString(cartridge)}");
            return false;
        }

        var seated = IsSeatedMagazine(provider);
        if (!seated)
        {
            if (!_hands.IsHolding(owner, provider) && !TryObtainInHand(owner, provider))
            {
                DebugAmmo(owner, "TryFeedCartridgeIntoProvider FAIL obtain provider");
                return false;
            }

            // Free the off-hand without holstering — we need gun + cartridge both held.
            TryUnwieldIfNeeded(owner, provider);
        }

        var before = new GetAmmoCountEvent();
        RaiseLocalEvent(provider, ref before);

        if (!TryObtainInHand(owner, cartridge))
        {
            DebugAmmo(owner, "TryFeedCartridgeIntoProvider FAIL obtain cart");
            return false;
        }

        if (!seated && !_hands.IsHolding(owner, provider) && !TryObtainInHand(owner, provider))
        {
            DebugAmmo(owner, "TryFeedCartridgeIntoProvider FAIL re-obtain provider");
            return false;
        }

        var coords = Transform(provider).Coordinates;
        var ok = _interaction.InteractUsing(owner, cartridge, provider, coords, checkCanInteract: false, checkCanUse: false);

        var after = new GetAmmoCountEvent();
        RaiseLocalEvent(provider, ref after);
        var loaded = after.Count > before.Count;
        DebugAmmo(owner,
            $"TryFeedCartridgeIntoProvider InteractUsing => {ok} count {before.Count}->{after.Count}");
        return loaded;
    }

    /// <summary>
    /// Feeds one cartridge (loose or from a transfer box) into a ballistic provider.
    /// </summary>
    public bool TryFeedOneIntoProvider(EntityUid owner, EntityUid gun, EntityUid provider)
    {
        if (TryFindCompatibleCartridge(owner, provider, out var cartridge))
        {
            DebugAmmo(owner, $"TryFeedOneIntoProvider via cart {ToPrettyString(cartridge)} -> {ToPrettyString(provider)}");
            return TryFeedCartridgeIntoProvider(owner, provider, cartridge);
        }

        if (TryFindMayTransferAmmoBox(owner, gun, provider, out var box))
        {
            DebugAmmo(owner, $"TryFeedOneIntoProvider via box {ToPrettyString(box)} -> {ToPrettyString(provider)}");
            return TryTransferOneFromAmmoBox(owner, box, provider);
        }

        DebugAmmo(owner, $"TryFeedOneIntoProvider FAIL: no cart/box for {ToPrettyString(provider)}");
        return false;
    }

    public bool IsProviderBelowCapacity(EntityUid provider)
    {
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(provider, ref ev);
        return ev.Capacity > 0 && ev.Count < ev.Capacity;
    }

    public bool TryGetSeatedMagazine(EntityUid gun, out EntityUid magazine)
    {
        magazine = default;
        if (!TryGetGunMagazineSlot(gun, out var magSlot) || magSlot.Item is not { } seated)
            return false;

        magazine = seated;
        return true;
    }

    /// <summary>
    /// Incomplete compatible mag for fill-from-box/cartridge.
    /// Prefers seated (combat readiness), then spare incomplete mags.
    /// </summary>
    public bool TryFindIncompleteCompatibleMagazine(EntityUid owner, EntityUid gun, out EntityUid magazine)
    {
        magazine = default;
        if (!IsMagazineFed(gun))
            return false;

        EntityUid? seatedIncomplete = null;
        EntityUid? spareIncomplete = null;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatibleMagazine(gun, item))
                continue;

            var ev = new GetAmmoCountEvent();
            RaiseLocalEvent(item, ref ev);
            if (ev.Capacity <= 0 || ev.Count >= ev.Capacity)
                continue;

            if (IsSeatedMagazine(item))
            {
                seatedIncomplete ??= item;
                continue;
            }

            spareIncomplete ??= item;
        }

        magazine = seatedIncomplete ?? spareIncomplete ?? default;
        return magazine != default;
    }

    /// <summary>
    /// Resolves an incomplete mag for fill. Seated mags stay in the gun; spare mags are drawn to hand.
    /// </summary>
    public bool TryPrepareMagazineForFill(EntityUid owner, EntityUid gun, out EntityUid magazine)
    {
        magazine = default;
        if (!TryFindIncompleteCompatibleMagazine(owner, gun, out var candidate))
            return false;

        // Fill seated mag in-place — ejecting it fights MagInsert forever.
        if (IsSeatedMagazine(candidate))
        {
            magazine = candidate;
            return true;
        }

        if (!TryObtainInHand(owner, candidate))
        {
            DebugAmmo(owner, $"TryPrepareMagazineForFill FAIL obtain {ToPrettyString(candidate)}");
            return false;
        }

        magazine = candidate;
        return true;
    }

    /// <summary>
    /// Hands + inventory slots + contents of worn/held Storage (one nested storage level) + seated gun mags.
    /// </summary>
    public IEnumerable<EntityUid> EnumerateInventoryAmmoCandidates(EntityUid owner)
    {
        var seen = new HashSet<EntityUid>();

        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (!seen.Add(held))
                continue;

            yield return held;

            if (TryGetGunMagazineSlot(held, out var heldMagSlot) &&
                heldMagSlot.Item is { } seatedMag &&
                seen.Add(seatedMag))
            {
                yield return seatedMag;
            }

            if (TryGetSeatedPowerCell(held, out var heldCell) && seen.Add(heldCell))
                yield return heldCell;

            foreach (var inner in EnumerateStorageContentsRecursive(held, depth: 0))
            {
                if (seen.Add(inner))
                    yield return inner;
            }
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var enumerator))
            yield break;

        while (enumerator.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } child)
                continue;

            if (!seen.Add(child))
                continue;

            yield return child;

            // Seated magazine inside a stowed gun (pocket / belt / bag).
            if (TryGetGunMagazineSlot(child, out var invMagSlot) &&
                invMagSlot.Item is { } invSeatedMag &&
                seen.Add(invSeatedMag))
            {
                yield return invSeatedMag;
            }

            if (TryGetSeatedPowerCell(child, out var invCell) && seen.Add(invCell))
                yield return invCell;

            foreach (var inner in EnumerateStorageContentsRecursive(child, depth: 0))
            {
                if (!seen.Add(inner))
                    continue;

                yield return inner;

                if (TryGetGunMagazineSlot(inner, out var bagMagSlot) &&
                    bagMagSlot.Item is { } bagSeatedMag &&
                    seen.Add(bagSeatedMag))
                {
                    yield return bagSeatedMag;
                }

                if (TryGetSeatedPowerCell(inner, out var bagCell) && seen.Add(bagCell))
                    yield return bagCell;
            }
        }
    }

    /// <summary>
    /// Depth 0 = direct storage contents; depth 1 = boxes/bags inside that storage (e.g. BoxSurvival in backpack).
    /// </summary>
    private IEnumerable<EntityUid> EnumerateStorageContentsRecursive(EntityUid storageUid, int depth)
    {
        foreach (var item in EnumerateStorageContents(storageUid))
        {
            yield return item;

            if (depth < 1 && _storageQuery.HasComponent(item))
            {
                foreach (var nested in EnumerateStorageContents(item))
                    yield return nested;
            }
        }
    }

    private IEnumerable<EntityUid> EnumerateStorageContents(EntityUid storageUid)
    {
        if (!_storageQuery.TryGetComponent(storageUid, out var storage))
            yield break;

        var seen = new HashSet<EntityUid>();

        foreach (var uid in storage.StoredItems.Keys)
        {
            if (seen.Add(uid))
                yield return uid;
        }

        if (_containers.TryGetContainer(storageUid, StorageComponent.ContainerId, out var container))
        {
            foreach (var uid in container.ContainedEntities)
            {
                if (seen.Add(uid))
                    yield return uid;
            }
        }
    }

    /// <summary>
    /// Magazines (including NF ones with mayTransfer) — never stow/drop these to free a hand;
    /// they are mid-fill / stock targets. Ammo boxes are not magazines.
    /// </summary>
    /// <summary>
    /// Prototype-id heuristic: NFMagazine* / Magazine* are mags; AmmunitionBox / MagazineBox are boxes.
    /// Magazines also set BallisticAmmoProvider.mayTransfer, so MayTransfer alone cannot distinguish them.
    /// </summary>
    private bool IsMagazinePrototype(EntityUid item)
    {
        if (!_ballisticQuery.HasComponent(item))
            return false;

        if (!TryComp(item, out MetaDataComponent? meta) || meta.EntityPrototype is not { } proto)
            return false;

        var id = proto.ID;
        if (id.Contains("AmmunitionBox", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("MagazineBox", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("AmmoBox", StringComparison.OrdinalIgnoreCase))
            return false;

        // Speedloaders transfer like boxes but are not gun magazines — leave them as boxes.
        if (id.Contains("SpeedLoader", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Speedloader", StringComparison.OrdinalIgnoreCase))
            return false;

        return id.Contains("Magazine", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures <paramref name="item"/> is held, pulling it out of worn/world storage if needed.
    /// Frees hands by stowing guns/boxes into bags — never stows magazines or drops them on the floor.
    /// </summary>
    public bool TryObtainInHand(EntityUid owner, EntityUid item)
    {
        if (_hands.IsHolding(owner, item))
            return true;

        if (_containers.IsEntityInContainer(item) &&
            !_containers.TryRemoveFromContainer(item))
        {
            DebugAmmo(owner, $"TryObtainInHand FAIL remove {ToPrettyString(item)}");
            return false;
        }

        if (_hands.TryPickupAnyHand(owner, item, checkActionBlocker: false))
            return true;

        var heldSnapshot = new List<EntityUid>();
        foreach (var held in _hands.EnumerateHeld(owner))
            heldSnapshot.Add(held);

        foreach (var held in heldSnapshot)
        {
            if (held == item)
                continue;

            // Wield placeholders are not stowable; clearing the real wielded item removes them.
            if (HasComp<VirtualItemComponent>(held))
                continue;

            // Never displace magazines being filled — put the gun/box in backpack instead.
            if (IsMagazinePrototype(held))
                continue;

            if (_gunQuery.HasComponent(held))
            {
                // Prefer backpack/belt storage over pockets so a later pocket stow doesn't displace the gun.
                if (!TryStowGunToStorage(owner, held))
                    TryStowGun(owner, held);
            }
            else
            {
                TryStowItem(owner, held);
            }
        }

        if (_hands.TryPickupAnyHand(owner, item, checkActionBlocker: false))
            return true;

        // Last resort: drop only non-gun, non-magazine, non-box junk.
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (held == item)
                continue;

            if (_gunQuery.HasComponent(held) || IsMagazinePrototype(held) || _ballisticQuery.HasComponent(held))
                continue;

            if (!_hands.TryDrop(owner, held, checkActionBlocker: false))
                continue;

            if (_hands.TryPickupAnyHand(owner, item, checkActionBlocker: false))
                return true;
        }

        DebugAmmo(owner, $"TryObtainInHand FAIL pickup {ToPrettyString(item)}");
        return false;
    }

    /// <summary>
    /// Stows a gun into worn/held storage only (backpack etc.), not inventory clothing slots.
    /// Avoids pocket displacement when later stowing a magazine into pocket1.
    /// </summary>
    public bool TryStowGunToStorage(EntityUid owner, EntityUid gun)
    {
        if (!Exists(gun) || !_gunQuery.HasComponent(gun))
            return false;

        if (!_hands.IsHolding(owner, gun))
            return IsCarriedBy(owner, gun) && !_hands.IsHolding(owner, gun);

        TryUnwieldIfNeeded(owner, gun);

        var storages = new List<EntityUid>();
        foreach (var storageEnt in EnumerateBodyStorages(owner))
        {
            if (storageEnt != gun)
                storages.Add(storageEnt);
        }

        foreach (var storageUid in storages)
        {
            if (!_storage.CanInsert(storageUid, gun, out _))
                continue;

            if (_storage.Insert(storageUid, gun, out _, user: owner, playSound: false))
            {
                DebugAmmo(owner, $"TryStowGunToStorage → {ToPrettyString(storageUid)} {ToPrettyString(gun)}");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After ammo work: insert a ready mag if needed, chamber a round, then always holster the gun
    /// and stow any held ammo logistics. Drawing for combat is done by GunCombatCompound.
    /// </summary>
    public void TryFinishAmmoWorkReadyToFight(EntityUid owner, EntityUid gun, NPCBlackboard? blackboard = null)
    {
        RememberOwnedGun(blackboard, gun);

        if (IsMagazineFed(gun))
        {
            var slotEmptyOrSpent = !TryGetSeatedMagazine(gun, out var seated) || GetAmmoCount(seated) <= 0;
            if (slotEmptyOrSpent && TryFindCompatibleLoadedMagazine(owner, gun, out var spare))
                TryInsertMagazine(owner, gun, spare);

            EnsureChamberReady(gun, owner);
        }

        TryStowHeldAmmoLogistics(owner);

        if (_hands.IsHolding(owner, gun))
        {
            if (!TryStowGunToStorage(owner, gun))
                TryStowGun(owner, gun);
            DebugAmmo(owner, $"TryFinishAmmoWorkReadyToFight: stow {ToPrettyString(gun)}");
        }
    }

    /// <summary>
    /// True when at least one faction hostile is within aggro (or <paramref name="range"/>).
    /// </summary>
    public bool HasNearbyHostiles(EntityUid owner, NPCBlackboard? blackboard = null, float? range = null)
    {
        var useRange = range ??
                       blackboard?.GetValueOrDefault<float>("AggroVisionRadius", EntityManager) ??
                       0f;
        if (useRange <= 0f)
            useRange = 10f;

        foreach (var _ in _npcFaction.GetNearbyHostiles(owner, useRange))
            return true;

        return false;
    }

    /// <summary>
    /// Records that the NPC is (or just was) actively fighting — resets the peacetime holster timer.
    /// </summary>
    public void NoteCombatActivity(NPCBlackboard blackboard)
    {
        if (blackboard.ReadOnly)
            return;

        blackboard.SetValue(LastCombatAtKey, _timing.CurTime);
    }

    /// <summary>
    /// True when the NPC holds a gun/ammo (or needs power-cell peace optimize) and has not been in active combat
    /// for <see cref="PeaceStowDelaySeconds"/>. Hostiles in aggro alone do not block — only Gun/Melee combat does.
    /// </summary>
    public bool IsPeaceStowReady(EntityUid owner, NPCBlackboard blackboard)
    {
        var needsCellOpt = TryGetOwnedGun(owner, out var ownedGun, out _, blackboard) &&
                           NeedsPowerCellPeaceOptimize(owner, ownedGun);

        if (!NeedsPeaceStow(owner) && !needsCellOpt)
            return false;

        if (HasComp<NPCRangedCombatComponent>(owner) || HasComp<NPCMeleeCombatComponent>(owner))
        {
            NoteCombatActivity(blackboard);
            return false;
        }

        if (!blackboard.TryGetValue<TimeSpan>(LastCombatAtKey, out var lastCombat, EntityManager))
        {
            // Start the peace timer the first time we notice held gear outside combat.
            NoteCombatActivity(blackboard);
            return false;
        }

        var ready = _timing.CurTime >= lastCombat + TimeSpan.FromSeconds(PeaceStowDelaySeconds);
        if (ready)
            DebugAmmo(owner, $"IsPeaceStowReady => true (no fight {PeaceStowDelaySeconds}s)");
        return ready;
    }

    /// <summary>
    /// True when a hand holds a gun or loose ammo logistics item that should be holstered/stowed in peace.
    /// </summary>
    public bool NeedsPeaceStow(EntityUid owner)
    {
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (_gunQuery.HasComponent(held))
                return true;

            if (IsPeaceStowAmmoItem(held))
                return true;
        }

        return false;
    }

    private bool IsPeaceStowAmmoItem(EntityUid item)
    {
        if (IsMagazinePrototype(item))
            return true;

        if (_powerCellQuery.HasComponent(item))
            return true;

        if (_ballisticQuery.TryGetComponent(item, out var bal) && bal.MayTransfer)
            return true;

        return IsLiveCartridge(item);
    }

    /// <summary>
    /// Stows magazines / ammo boxes / live cartridges currently held in hands.
    /// </summary>
    public void TryStowHeldAmmoLogistics(EntityUid owner)
    {
        var heldAmmo = new List<EntityUid>();
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (IsPeaceStowAmmoItem(held))
                heldAmmo.Add(held);
        }

        foreach (var item in heldAmmo)
        {
            var stowed = TryStowItem(owner, item);
            DebugAmmo(owner, $"TryStowHeldAmmoLogistics {ToPrettyString(item)} => {stowed}");
        }
    }

    /// <summary>
    /// Holsters the owned/held gun (backpack → belt bag → pocket/belt/back slots) and stows held ammo.
    /// </summary>
    public bool TryPeaceStowEquipment(EntityUid owner, NPCBlackboard? blackboard = null)
    {
        var didSomething = false;

        if (TryGetOwnedGun(owner, out var ownedGun, out _, blackboard) && IsPowerCellSwapGun(ownedGun))
            didSomething |= TryPeaceOptimizePowerCells(owner, ownedGun);

        TryStowHeldAmmoLogistics(owner);

        if (TryGetOwnedGun(owner, out var gun, out _, blackboard) && _hands.IsHolding(owner, gun))
        {
            var stowed = TryStowGunPreferringRecharger(owner, gun);
            DebugAmmo(owner, $"TryPeaceStowEquipment gun {ToPrettyString(gun)} => {stowed}");
            didSomething |= stowed;
        }
        else
        {
            foreach (var held in _hands.EnumerateHeld(owner))
            {
                if (!_gunQuery.HasComponent(held))
                    continue;

                var stowed = TryStowGunPreferringRecharger(owner, held);
                DebugAmmo(owner, $"TryPeaceStowEquipment held gun {ToPrettyString(held)} => {stowed}");
                didSomething |= stowed;
                break;
            }
        }

        TryStowHeldAmmoLogistics(owner);

        var stillNeedsCellOpt = TryGetOwnedGun(owner, out var gunAfter, out _, blackboard) &&
                                NeedsPowerCellPeaceOptimize(owner, gunAfter);

        if (!NeedsPeaceStow(owner) && !stillNeedsCellOpt)
            return true;

        return didSomething;
    }

    /// <summary>
    /// Stashes a world/held item onto the body (pocket/bag) without needing a free hand when possible.
    /// </summary>
    public bool TryStashItemOnBody(EntityUid owner, EntityUid item)
    {
        if (IsCarriedBy(owner, item) && !_hands.IsHolding(owner, item))
            return true;

        if (_hands.IsHolding(owner, item))
            return TryStowItem(owner, item);

        // Direct insert from world into worn storage.
        var storages = new List<EntityUid>();
        foreach (var storageEnt in EnumerateBodyStorages(owner))
            storages.Add(storageEnt);

        foreach (var storageUid in storages)
        {
            if (!_storage.CanInsert(storageUid, item, out _))
                continue;

            if (_storage.Insert(storageUid, item, out _, user: owner, playSound: false))
            {
                DebugAmmo(owner, $"TryStashItemOnBody → {ToPrettyString(storageUid)} {ToPrettyString(item)}");
                return true;
            }
        }

        return TryObtainInHand(owner, item) && TryStowItem(owner, item);
    }

    /// <summary>
    /// Inserts a magazine into the held gun. Prefers direct ItemSlots transfer (works from backpack).
    /// </summary>
    public bool TryInsertMagazine(EntityUid owner, EntityUid gun, EntityUid magazine)
    {
        DebugAmmo(owner,
            $"TryInsertMagazine start gun={ToPrettyString(gun)} mag={ToPrettyString(magazine)} holding={_hands.IsHolding(owner, magazine)}");

        if (TryGetGunMagazineSlot(gun, out var magSlot) && magSlot.HasItem)
        {
            var ejected = _slots.TryEject(gun, magSlot, user: null, out var old);
            DebugAmmo(owner,
                $"TryInsertMagazine eject => {ejected} old={(old == null ? "null" : ToPrettyString(old.Value))}");
        }

        if (_slots.TryInsert(gun, MagazineSlot, magazine, owner))
        {
            EnsureChamberReady(gun, owner);
            DebugAmmo(owner, "TryInsertMagazine SUCCESS direct");
            return true;
        }

        DebugAmmo(owner, "TryInsertMagazine direct FAILED, trying obtain-in-hand");

        if (!TryObtainInHand(owner, magazine))
        {
            DebugAmmo(owner, "TryInsertMagazine FAIL obtain");
            return false;
        }

        if (_slots.TryInsert(gun, MagazineSlot, magazine, owner))
        {
            EnsureChamberReady(gun, owner);
            DebugAmmo(owner, "TryInsertMagazine SUCCESS after obtain");
            return true;
        }

        DebugAmmo(owner, "TryInsertMagazine FAIL after obtain");
        return false;
    }

    public bool StorageContainsCompatibleAmmo(EntityUid storageUid, EntityUid gun)
    {
        foreach (var item in EnumerateStorageContents(storageUid))
        {
            if (IsAmmoCompatible(gun, item))
                return true;
        }

        return false;
    }

    public bool TryGetCompatibleItemFromStorage(EntityUid storageUid, EntityUid gun, out EntityUid item)
    {
        item = default;
        EntityUid? loose = null;
        foreach (var contained in EnumerateStorageContents(storageUid))
        {
            if (!IsAmmoCompatible(gun, contained))
                continue;

            if (IsCompatibleMagazine(gun, contained) && GetAmmoCount(contained) > 0)
            {
                item = contained;
                return true;
            }

            loose ??= contained;
        }

        if (loose != null)
        {
            item = loose.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sample up to <see cref="MaxNearbyStorages"/> nearby Storage items that contain compatible ammo.
    /// </summary>
    public bool TryPickNearbyAmmoStorage(EntityUid owner, EntityUid gun, float range, out EntityUid storage)
    {
        storage = default;
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var found = new List<EntityUid>();

        foreach (var ent in _lookup.GetEntitiesInRange<StorageComponent>(mapPos, range))
        {
            if (ent.Owner == owner)
                continue;

            if (_containers.IsEntityInContainer(ent.Owner))
                continue;

            if (!StorageContainsCompatibleAmmo(ent.Owner, gun))
                continue;

            found.Add(ent.Owner);
            if (found.Count >= MaxNearbyStorages * 3)
                break;
        }

        if (found.Count == 0)
            return false;

        _random.Shuffle(found);

        var sampleCount = Math.Min(MaxNearbyStorages, found.Count);
        EntityUid? best = null;
        var bestDist = float.MaxValue;

        for (var i = 0; i < sampleCount; i++)
        {
            var uid = found[i];
            if (!_xformQuery.TryGetComponent(uid, out var tx))
                continue;

            if (!xform.Coordinates.TryDistance(EntityManager, _transform, tx.Coordinates, out var dist))
                continue;

            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = uid;
        }

        if (best == null)
            return false;

        storage = best.Value;
        return true;
    }

    /// <summary>
    /// Inventory metrics computed once per nearby-ammo scan so scoring is O(1) per floor entity.
    /// </summary>
    private readonly struct NearbyAmmoScoreCache
    {
        public readonly bool MagFed;
        public readonly int MagCount;
        public readonly int LooseCartCount;
        public readonly int PowerCellCount;
        /// <summary>Lowest ScorePowerCellQuality among owned compatible cells; -1 if none.</summary>
        public readonly int WorstPowerCellQuality;
        public readonly bool HasNonEmptyBox;
        public readonly bool TubeBelowCapacity;
        public readonly EntityUid IncompleteMag;
        public readonly bool HasIncompleteMag;
        public readonly bool IncompleteHasTransferBox;

        public NearbyAmmoScoreCache(
            bool magFed,
            int magCount,
            int looseCartCount,
            int powerCellCount,
            int worstPowerCellQuality,
            bool hasNonEmptyBox,
            bool tubeBelowCapacity,
            EntityUid incompleteMag,
            bool hasIncompleteMag,
            bool incompleteHasTransferBox)
        {
            MagFed = magFed;
            MagCount = magCount;
            LooseCartCount = looseCartCount;
            PowerCellCount = powerCellCount;
            WorstPowerCellQuality = worstPowerCellQuality;
            HasNonEmptyBox = hasNonEmptyBox;
            TubeBelowCapacity = tubeBelowCapacity;
            IncompleteMag = incompleteMag;
            HasIncompleteMag = hasIncompleteMag;
            IncompleteHasTransferBox = incompleteHasTransferBox;
        }
    }

    private NearbyAmmoScoreCache BuildNearbyAmmoScoreCache(EntityUid owner, EntityUid gun)
    {
        var magFed = IsMagazineFed(gun);
        var magCount = 0;
        var loose = 0;
        var powerCells = 0;
        var worstCellQuality = -1;
        var incomplete = default(EntityUid);
        var hasIncomplete = false;
        var incompleteHasBox = false;
        var tubeBelow = false;

        if (magFed)
        {
            magCount = CountCompatibleMags(owner, gun);
            hasIncomplete = TryFindIncompleteCompatibleMagazine(owner, gun, out incomplete);
            if (hasIncomplete)
                incompleteHasBox = TryFindMayTransferAmmoBox(owner, gun, incomplete, out _);
        }
        else if (IsPowerCellSwapGun(gun))
        {
            powerCells = CountCompatiblePowerCells(owner, gun);
            worstCellQuality = GetWorstOwnedPowerCellQuality(owner, gun);
        }
        else
        {
            loose = CountLooseCompatibleCartridges(owner, gun);
            tubeBelow = IsProviderBelowCapacity(gun);
        }

        return new NearbyAmmoScoreCache(
            magFed,
            magCount,
            loose,
            powerCells,
            worstCellQuality,
            HasNonEmptyCompatibleAmmoBox(owner, gun),
            tubeBelow,
            incomplete,
            hasIncomplete,
            incompleteHasBox);
    }

    /// <summary>
    /// Pickup priority: loaded mag > empty mag (until MinCompatibleMags) > ammo box > tube cartridges (until MinTubeLooseCartridges).
    /// </summary>
    public int GetNearbyAmmoPickupScore(EntityUid owner, EntityUid gun, EntityUid item)
    {
        var cache = BuildNearbyAmmoScoreCache(owner, gun);
        return GetNearbyAmmoPickupScore(gun, item, in cache);
    }

    private int GetNearbyAmmoPickupScore(EntityUid gun, EntityUid item, in NearbyAmmoScoreCache cache)
    {
        if (_cartridgeQuery.TryGetComponent(item, out var cart) && cart.Spent)
            return -1;

        if (IsCompatibleMagazine(gun, item))
        {
            if (cache.MagCount >= MinCompatibleMags)
                return -1;

            var count = GetAmmoCount(item);
            if (count > 0)
                return 400;

            return 150;
        }

        if (IsCompatibleAmmoBox(gun, item))
        {
            if (GetAmmoCount(item) <= 0)
                return -1;

            // Prefer picking a box if we don't already carry a non-empty one.
            return cache.HasNonEmptyBox ? 250 : 320;
        }

        // Loose live cartridge
        if (_cartridgeQuery.HasComponent(item))
        {
            if (cache.MagFed)
            {
                // Only useful to fill an incomplete mag when no box is available.
                if (cache.HasIncompleteMag &&
                    !cache.IncompleteHasTransferBox &&
                    IsCompatibleCartridgeFor(cache.IncompleteMag, item))
                    return 40;

                return -1;
            }

            // Tube / shotgun: stock up to MinTubeLooseCartridges in the backpack.
            // Still allow floor pickup when the tube itself needs rounds (fed on pickup).
            if (cache.LooseCartCount >= MinTubeLooseCartridges && !cache.TubeBelowCapacity)
                return -1;

            if (_ballisticQuery.TryGetComponent(gun, out var gunBal) &&
                !_whitelist.IsWhitelistFailOrNull(gunBal.Whitelist, item))
                return 100;

            return -1;
        }

        if (IsCompatiblePowerCell(gun, item))
        {
            var isSelf = IsSelfRechargingBattery(item);
            var charged = GetBatteryCurrentCharge(item) > 0.01f;

            // Empty disposable cells are dead weight.
            if (!charged && !isSelf)
                return -1;

            var quality = ScorePowerCellQuality(item);
            if (quality <= 0)
                return -1;

            // Stock to 1 seated + 2 spares, or replace the worst owned cell with a better floor one.
            if (cache.PowerCellCount < MinCompatiblePowerCells)
            {
                if (isSelf)
                    return charged ? 550 : 420;
                return 500;
            }

            // At capacity: upgrade any of the three — quality must beat the worst owned cell.
            if (quality <= cache.WorstPowerCellQuality)
                return -1;

            // Prefer the biggest upgrade first (self-recharge / higher MaxCharge).
            return 450 + Math.Min(quality / 50_000, 40);
        }

        return -1;
    }

    public bool TrySelectBestNearbyAmmo(
        EntityUid owner,
        EntityUid gun,
        float range,
        out EntityUid best,
        out float bestDist)
    {
        return TrySelectBestNearbyAmmo(owner, gun, range, out best, out bestDist, out _);
    }

    public bool TrySelectBestNearbyAmmo(
        EntityUid owner,
        EntityUid gun,
        float range,
        out EntityUid best,
        out float bestDist,
        out int bestScore)
    {
        best = default;
        bestDist = float.MaxValue;
        bestScore = int.MinValue;
        var found = new HashSet<EntityUid>();
        CollectNearbyCompatibleAmmo(owner, gun, range, found);
        if (found.Count == 0)
            return false;

        if (!TryComp(owner, out TransformComponent? ownerXform))
            return false;

        var cache = BuildNearbyAmmoScoreCache(owner, gun);
        EntityUid? chosen = null;
        var chosenDist = float.MaxValue;
        var chosenScore = int.MinValue;

        foreach (var ent in found)
        {
            if (!TryComp(ent, out TransformComponent? tx))
                continue;

            if (!ownerXform.Coordinates.TryDistance(EntityManager, tx.Coordinates, out var dist))
                continue;

            var score = GetNearbyAmmoPickupScore(gun, ent, in cache);
            if (score <= 0)
                continue;

            if (score > chosenScore || (score == chosenScore && dist < chosenDist))
            {
                chosenScore = score;
                chosen = ent;
                chosenDist = dist;
            }
        }

        if (chosen == null)
            return false;

        best = chosen.Value;
        bestDist = chosenDist;
        bestScore = chosenScore;
        return true;
    }

    public void CollectNearbyCompatibleAmmo(EntityUid owner, EntityUid gun, float range, HashSet<EntityUid> entities)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var checkedCount = 0;

        // Magazines / ammo boxes (BallisticAmmoProvider is the concrete registered type).
        foreach (var ent in _lookup.GetEntitiesInRange<BallisticAmmoProviderComponent>(mapPos, range))
        {
            if (!TryAddNearbyAmmoCandidate(owner, gun, ent.Owner, entities, ref checkedCount))
                break;
        }

        // Loose cartridges register as CartridgeAmmo, not base Ammo, for lookup purposes.
        foreach (var ent in _lookup.GetEntitiesInRange<CartridgeAmmoComponent>(mapPos, range))
        {
            if (!TryAddNearbyAmmoCandidate(owner, gun, ent.Owner, entities, ref checkedCount))
                break;
        }

        // Swappable energy-gun cells.
        foreach (var ent in _lookup.GetEntitiesInRange<PowerCellComponent>(mapPos, range))
        {
            if (!TryAddNearbyAmmoCandidate(owner, gun, ent.Owner, entities, ref checkedCount))
                break;
        }
    }

    private bool TryAddNearbyAmmoCandidate(
        EntityUid owner,
        EntityUid gun,
        EntityUid ent,
        HashSet<EntityUid> entities,
        ref int checkedCount)
    {
        if (ent == owner || ent == gun)
            return true;

        checkedCount++;
        if (checkedCount > MaxNearbyLookupChecks)
            return false;

        if (_containers.IsEntityInContainer(ent))
            return true;

        // Already on the NPC — picking these again causes drop/pickup thrash.
        if (IsCarriedBy(owner, ent))
            return true;

        if (IsAmmoCompatible(gun, ent))
            entities.Add(ent);

        return true;
    }

    /// <summary>
    /// Early-exit: true on the first compatible loose ammo entity in range.
    /// </summary>
    private bool TryFindFirstCompatibleAmmoNearby(EntityUid owner, EntityUid gun, float range)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var checkedCount = 0;
        var cache = BuildNearbyAmmoScoreCache(owner, gun);

        foreach (var ent in _lookup.GetEntitiesInRange<BallisticAmmoProviderComponent>(mapPos, range))
        {
            if (IsNearbyCompatibleAmmoCandidate(owner, gun, ent.Owner, in cache, ref checkedCount))
                return true;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<CartridgeAmmoComponent>(mapPos, range))
        {
            if (IsNearbyCompatibleAmmoCandidate(owner, gun, ent.Owner, in cache, ref checkedCount))
                return true;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<PowerCellComponent>(mapPos, range))
        {
            if (IsNearbyCompatibleAmmoCandidate(owner, gun, ent.Owner, in cache, ref checkedCount))
                return true;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;
        }

        return false;
    }

    private bool IsNearbyCompatibleAmmoCandidate(
        EntityUid owner,
        EntityUid gun,
        EntityUid ent,
        in NearbyAmmoScoreCache cache,
        ref int checkedCount)
    {
        if (ent == owner || ent == gun)
            return false;

        checkedCount++;
        if (_containers.IsEntityInContainer(ent))
            return false;

        if (IsCarriedBy(owner, ent))
            return false;

        return IsAmmoCompatible(gun, ent) && GetNearbyAmmoPickupScore(gun, ent, in cache) > 0;
    }

    public void CollectNearbyAmmoStorages(EntityUid owner, EntityUid gun, float range, HashSet<EntityUid> entities)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var found = new List<EntityUid>();

        foreach (var ent in _lookup.GetEntitiesInRange<StorageComponent>(mapPos, range))
        {
            if (ent.Owner == owner)
                continue;

            if (_containers.IsEntityInContainer(ent.Owner))
                continue;

            if (!StorageContainsCompatibleAmmo(ent.Owner, gun))
                continue;

            found.Add(ent.Owner);
            if (found.Count >= MaxNearbyStorages * 3)
                break;
        }

        if (found.Count == 0)
            return;

        _random.Shuffle(found);
        var sampleCount = Math.Min(MaxNearbyStorages, found.Count);
        for (var i = 0; i < sampleCount; i++)
            entities.Add(found[i]);
    }

    /// <summary>
    /// Early-exit: true on the first nearby floor storage that contains compatible ammo.
    /// </summary>
    public bool HasNearbyAmmoStorage(EntityUid owner, EntityUid gun, float range)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var checkedCount = 0;

        foreach (var ent in _lookup.GetEntitiesInRange<StorageComponent>(mapPos, range))
        {
            if (ent.Owner == owner)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                break;

            if (_containers.IsEntityInContainer(ent.Owner))
                continue;

            if (StorageContainsCompatibleAmmo(ent.Owner, gun))
                return true;
        }

        return false;
    }

    public void CollectNearbyAlternateLoadouts(EntityUid owner, EntityUid emptyGun, float range, HashSet<EntityUid> entities)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var checkedCount = 0;

        foreach (var ent in _lookup.GetEntitiesInRange<GunComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return;

            if (_containers.IsEntityInContainer(uid))
                continue;

            var ev = new GetAmmoCountEvent();
            RaiseLocalEvent(uid, ref ev);
            if (ev.Count <= 0)
                continue;

            if (!SameAmmoFamily(emptyGun, uid))
                entities.Add(uid);
        }

        foreach (var ent in _lookup.GetEntitiesInRange<BallisticAmmoProviderComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return;

            if (_containers.IsEntityInContainer(uid))
                continue;

            // Skip guns already considered above.
            if (_gunQuery.HasComponent(uid))
                continue;

            if (!IsAmmoCompatible(emptyGun, uid))
                entities.Add(uid);
        }

        foreach (var ent in _lookup.GetEntitiesInRange<CartridgeAmmoComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return;

            if (_containers.IsEntityInContainer(uid))
                continue;

            if (!IsAmmoCompatible(emptyGun, uid))
                entities.Add(uid);
        }
    }

    /// <summary>
    /// Early-exit: true on the first alternate gun/ammo family entity in range.
    /// </summary>
    private bool TryFindFirstAlternateLoadoutNearby(EntityUid owner, EntityUid emptyGun, float range)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform: xform);
        var checkedCount = 0;

        foreach (var ent in _lookup.GetEntitiesInRange<GunComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;

            if (_containers.IsEntityInContainer(uid))
                continue;

            var ev = new GetAmmoCountEvent();
            RaiseLocalEvent(uid, ref ev);
            if (ev.Count <= 0)
                continue;

            if (!SameAmmoFamily(emptyGun, uid))
                return true;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<BallisticAmmoProviderComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;

            if (_containers.IsEntityInContainer(uid) || _gunQuery.HasComponent(uid))
                continue;

            if (!IsAmmoCompatible(emptyGun, uid))
                return true;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<CartridgeAmmoComponent>(mapPos, range))
        {
            var uid = ent.Owner;
            if (uid == owner || uid == emptyGun)
                continue;

            checkedCount++;
            if (checkedCount > MaxNearbyLookupChecks)
                return false;

            if (_containers.IsEntityInContainer(uid))
                continue;

            if (!IsAmmoCompatible(emptyGun, uid))
                return true;
        }

        return false;
    }

    public bool HasCompatibleAmmoNearby(EntityUid owner, EntityUid gun, float range)
    {
        // Early-exit: any score>0 floor ammo is enough (existence check, not best-of-all).
        if (TryFindFirstCompatibleAmmoNearby(owner, gun, range))
            return true;

        return HasNearbyAmmoStorage(owner, gun, range);
    }

    public bool HasAlternateLoadoutNearby(EntityUid owner, EntityUid emptyGun, float range)
    {
        return TryFindFirstAlternateLoadoutNearby(owner, emptyGun, range);
    }

    public bool SameAmmoFamily(EntityUid gunA, EntityUid gunB)
    {
        var aMag = IsMagazineFed(gunA);
        var bMag = IsMagazineFed(gunB);
        if (aMag != bMag)
            return false;

        if (aMag && bMag &&
            TryGetGunMagazineSlot(gunA, out var slotA) &&
            TryGetGunMagazineSlot(gunB, out var slotB))
        {
            return WhitelistsMatch(slotA.Whitelist, slotB.Whitelist);
        }

        if (_ballisticQuery.TryGetComponent(gunA, out var balA) &&
            _ballisticQuery.TryGetComponent(gunB, out var balB))
            return WhitelistsMatch(balA.Whitelist, balB.Whitelist);

        return true;
    }

    private static bool WhitelistsMatch(EntityWhitelist? a, EntityWhitelist? b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a == null || b == null)
            return a == b;

        if (a.Tags != null && b.Tags != null &&
            a.Tags.Count == b.Tags.Count)
        {
            for (var i = 0; i < a.Tags.Count; i++)
            {
                if (a.Tags[i] != b.Tags[i])
                    return false;
            }

            return true;
        }

        if (a.Components != null && b.Components != null &&
            a.Components.Length == b.Components.Length)
        {
            for (var i = 0; i < a.Components.Length; i++)
            {
                if (!string.Equals(a.Components[i], b.Components[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        return false;
    }

    private static bool WhitelistTagsOverlap(EntityWhitelist? a, EntityWhitelist? b)
    {
        if (a?.Tags == null || b?.Tags == null)
            return false;

        foreach (var tagA in a.Tags)
        {
            foreach (var tagB in b.Tags)
            {
                if (tagA == tagB)
                    return true;
            }
        }

        return false;
    }

    public bool IsAmmoSearchReady(NPCBlackboard blackboard)
    {
        if (!blackboard.TryGetValue<TimeSpan>(AmmoSearchReadyAt, out var readyAt, EntityManager))
            return true;

        return _timing.CurTime >= readyAt;
    }

    public void SetAmmoSearchCooldown(NPCBlackboard blackboard)
    {
        if (blackboard.ReadOnly)
            return;

        blackboard.SetValue(AmmoSearchReadyAt, _timing.CurTime + TimeSpan.FromSeconds(AmmoSearchCooldownSeconds));
    }

    public float GetAmmoSearchRange(NPCBlackboard blackboard)
    {
        var range = blackboard.GetValueOrDefault<float>(AmmoSearchRangeKey, EntityManager);
        return range > 0f ? range : DefaultAmmoSearchRange;
    }

    public float GetAmmoLootHostileRange(NPCBlackboard blackboard)
    {
        var range = blackboard.GetValueOrDefault<float>(AmmoLootHostileRangeKey, EntityManager);
        return range > 0f ? range : DefaultAmmoLootHostileRange;
    }

    /// <summary>
    /// After inserting a mag into a chamber-magazine gun, ensure a live round is chambered and the bolt is closed.
    /// Must NOT raise UseInHand — that relays to the magazine and ejects live rounds onto the floor.
    /// </summary>
    public void EnsureChamberReady(EntityUid gun, EntityUid user)
    {
        if (!TryComp<ChamberMagazineAmmoProviderComponent>(gun, out var chamber) || chamber.BoltClosed == null)
            return;

        if (TryGetGunChamberSlot(gun, out var chamberSlot) && chamberSlot.Item is { } inChamber)
        {
            // Spent casing left in chamber — eject before cycling.
            if (_cartridgeQuery.TryGetComponent(inChamber, out var cart) && cart.Spent)
            {
                _slots.TryEject(gun, ChamberSlot, user, out _);
            }
            else
            {
                // Live round already chambered — still close an open bolt.
                if (chamber.BoltClosed == false)
                    _guns.SetBoltClosed(gun, chamber, true, user);

                DebugAmmo(user,
                    $"EnsureChamberReady already-chambered boltClosed={chamber.BoltClosed}");
                return;
            }
        }

        if (!TryGetSeatedMagazine(gun, out var mag) || GetAmmoCount(mag) <= 0)
        {
            DebugAmmo(user, "EnsureChamberReady skip: no mag ammo");
            return;
        }

        // Closing the bolt CycleCartridge's from the mag. If already closed with empty chamber, rack via open→close.
        if (chamber.BoltClosed == true)
            _guns.SetBoltClosed(gun, chamber, false, user);

        if (chamber.BoltClosed == false)
            _guns.SetBoltClosed(gun, chamber, true, user);

        DebugAmmo(user,
            $"EnsureChamberReady boltClosed={chamber.BoltClosed} chambered={(TryGetGunChamberSlot(gun, out var ch) && ch.HasItem)}");
    }

    /// <summary>
    /// True when the NPC should keep an empty gun (compatible mag/box/cartridges on body, or fill material).
    /// </summary>
    public bool HasAmmoLogisticsPotential(EntityUid owner, EntityUid gun)
    {
        if (HasUsableReloadMaterial(owner, gun))
        {
            if (IsEnergyGun(gun))
                DebugAmmo(owner, $"HasAmmoLogisticsPotential=true (reload material) ({DescribeEnergyGunState(owner, gun)})", force: true);
            return true;
        }

        // Self-recharging energy guns always retain value while they can refill.
        if (IsSelfRechargingEnergyGun(gun))
        {
            DebugAmmo(owner, $"HasAmmoLogisticsPotential=true (self-recharge) ({DescribeEnergyGunState(owner, gun)})", force: true);
            return true;
        }

        // External-charge guns are only keepable with a portable recharger.
        if (IsExternalChargeEnergyGun(gun))
        {
            var keep = TryFindPortableRecharger(owner, out var recharger);
            DebugAmmo(owner,
                $"HasAmmoLogisticsPotential={keep} (external) recharger={(keep ? ToPrettyString(recharger) : "null")} ({DescribeEnergyGunState(owner, gun)})",
                force: true);
            return keep;
        }

        if (IsPowerCellSwapGun(gun))
        {
            foreach (var item in EnumerateInventoryAmmoCandidates(owner))
            {
                if (!IsCompatiblePowerCell(gun, item))
                    continue;

                // Charged cells or self-recharging empties (will refill in bag/gun) keep the loadout viable.
                if (GetBatteryCurrentCharge(item) > 0f || IsSelfRechargingBattery(item))
                {
                    DebugAmmo(owner, $"HasAmmoLogisticsPotential=true (spare cell {ToPrettyString(item)})");
                    return true;
                }
            }

            DebugAmmo(owner, $"HasAmmoLogisticsPotential=false (power-cell, no spare) ({DescribeEnergyGunState(owner, gun)})");
            return false;
        }

        if (IsMagazineFed(gun))
        {
            foreach (var item in EnumerateInventoryAmmoCandidates(owner))
            {
                if (IsCompatibleMagazine(gun, item))
                    return true;

                if (IsCompatibleAmmoBox(gun, item))
                    return true;
            }

            return false;
        }

        if (NeedsBallisticTubeFill(owner, gun))
            return true;

        if (CountLooseCompatibleCartridges(owner, gun) > 0)
            return true;

        return HasNonEmptyCompatibleAmmoBox(owner, gun);
    }
}
