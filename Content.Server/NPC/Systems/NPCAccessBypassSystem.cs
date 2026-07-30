using System.Diagnostics.CodeAnalysis;
using Content.Server.NPC.HTN;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Prying.Components;
using Content.Shared.Prying.Systems;
using Content.Shared.Sticky.Components;
using Content.Shared.Sticky.Systems;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Door open / hack / pry / breach policy for NPC steering + HTN.
/// Cadence: on obstacle / plan. Worst-case: local door + inventory scan.
/// </summary>
public sealed class NPCAccessBypassSystem : EntitySystem
{
    private const float ProbeRange = 1.5f;
    private const float MinMeleeBreachDamage = 25f;

    private static readonly EntProtoId C4Proto = "C4";
    private static readonly EntProtoId SeismicProto = "SeismicCharge";

    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly EmagSystem _emag = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PryingSystem _prying = default!;
    [Dependency] private readonly SharedDoorSystem _doors = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StickySystem _sticky = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;

    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<AccessReaderComponent> _readerQuery;
    private EntityQuery<AirlockComponent> _airlockQuery;
    private EntityQuery<DoorBoltComponent> _boltQuery;

    public override void Initialize()
    {
        base.Initialize();
        _doorQuery = GetEntityQuery<DoorComponent>();
        _readerQuery = GetEntityQuery<AccessReaderComponent>();
        _airlockQuery = GetEntityQuery<AirlockComponent>();
        _boltQuery = GetEntityQuery<DoorBoltComponent>();
    }

    public enum DoorHandleResult : byte
    {
        None,
        OpenAccess,
        Hack,
        Pry,
        BreachExplosive,
        BreachMelee,
    }

    public bool IsDoorPowered(EntityUid door)
    {
        // Non-airlocks are treated as powered for open/interact purposes.
        return !_airlockQuery.TryGetComponent(door, out var airlock) || airlock.Powered;
    }

    public bool CanHack(EntityUid owner) => TryFindAccessDisruptor(owner, out _);

    public DoorHandleResult Evaluate(EntityUid owner, EntityUid door)
    {
        if (!_doorQuery.TryGetComponent(door, out var doorComp))
            return DoorHandleResult.None;

        if (doorComp.State is DoorState.Open or DoorState.Opening)
            return DoorHandleResult.None;

        // Role gate from editor profile (missing key = allow).
        if (TryComp<HTNComponent>(owner, out var htn) &&
            htn.Blackboard.TryGetValue<bool>(NPCRoleSystem.CanBypassDoorKey, out var canBypass, EntityManager) &&
            !canBypass)
            return DoorHandleResult.None;

        if (doorComp.State == DoorState.Welded)
        {
            if (TryFindBreachCharge(owner, out _))
                return DoorHandleResult.BreachExplosive;
            return DoorHandleResult.None;
        }

        var powered = IsDoorPowered(door);
        var bolted = _doors.IsBolted(door);
        var hasReader = _readerQuery.HasComponent(door);
        var allowed = !hasReader || _access.IsAllowed(owner, door);

        // Simple open when allowed.
        if (allowed && powered && !bolted)
            return DoorHandleResult.OpenAccess;

        // Bolted / locked: C4 first, else emag/access breaker. No pry/melee tool thinking.
        if (bolted || !allowed)
        {
            if (TryFindBreachCharge(owner, out _))
                return DoorHandleResult.BreachExplosive;
            if (CanHack(owner) && powered)
                return DoorHandleResult.Hack;
            return DoorHandleResult.None;
        }

        // Unpowered unbolted: emag useless — C4 only if we have it.
        if (!powered && TryFindBreachCharge(owner, out _))
            return DoorHandleResult.BreachExplosive;

        return DoorHandleResult.None;
    }

    /// <summary>
    /// True when a nearby closed door needs intentional bypass (not a simple access-open).
    /// </summary>
    public bool HasBlockingDoorNeed(EntityUid owner, NPCBlackboard blackboard)
    {
        return TryFindBlockingDoor(owner, out _);
    }

    public bool TryFindBlockingDoor(EntityUid owner, out EntityUid door)
    {
        door = default;
        if (!TryComp(owner, out TransformComponent? xform))
            return false;

        var mapPos = _transform.GetMapCoordinates(owner, xform);
        foreach (var ent in _lookup.GetEntitiesInRange(mapPos, ProbeRange))
        {
            if (!_doorQuery.TryGetComponent(ent, out var doorComp))
                continue;

            if (doorComp.State is not (DoorState.Closed or DoorState.Welded))
                continue;

            var result = Evaluate(owner, ent);
            if (result is DoorHandleResult.Hack or DoorHandleResult.Pry
                or DoorHandleResult.BreachExplosive or DoorHandleResult.BreachMelee)
            {
                door = ent;
                return true;
            }
        }

        return false;
    }

    public bool TrySelectBypassDoor(EntityUid owner, NPCBlackboard blackboard)
    {
        if (!TryFindBlockingDoor(owner, out var door))
            return false;

        blackboard.SetValue(NPCBlackboard.BypassDoorTarget, door);
        blackboard.SetValue(NPCBlackboard.Target, door);
        if (TryComp(door, out TransformComponent? dx))
            blackboard.SetValue(NPCBlackboard.TargetCoordinates, dx.Coordinates);
        return true;
    }

    /// <summary>
    /// Steering entry: handle a door obstacle. Returns true if an action was started.
    /// </summary>
    public bool TryHandleDoorObstacle(EntityUid owner, EntityUid door, out DoAfterId? doAfterId)
    {
        doAfterId = null;
        var result = Evaluate(owner, door);
        if (result == DoorHandleResult.None)
            return false;

        return TryHandle(owner, door, result, out doAfterId);
    }

    public bool TryBypass(EntityUid owner, EntityUid door)
    {
        return TryHandle(owner, door, Evaluate(owner, door), out _);
    }

    public bool TryHandle(EntityUid owner, EntityUid door, DoorHandleResult result, out DoAfterId? doAfterId)
    {
        doAfterId = null;
        switch (result)
        {
            case DoorHandleResult.OpenAccess:
                return _doors.TryOpen(door, user: owner);

            case DoorHandleResult.Hack:
                return TryHack(owner, door);

            case DoorHandleResult.Pry:
                return TryPryDoor(owner, door, out doAfterId);

            case DoorHandleResult.BreachExplosive:
                return TryStickBreachCharge(owner, door);

            case DoorHandleResult.BreachMelee:
                return TryMeleeBreach(owner, door);

            default:
                return false;
        }
    }

    private bool TryHack(EntityUid owner, EntityUid door)
    {
        if (!IsDoorPowered(door))
            return false;

        if (!TryFindAccessDisruptor(owner, out var disruptor) || !_ammo.TryObtainInHand(owner, disruptor.Value))
            return false;

        // Door OnEmagged refuses bolted airlocks — drop bolts first, then disrupt access.
        if (_doors.IsBolted(door) && _boltQuery.TryGetComponent(door, out var bolts))
        {
            if (!_doors.TrySetBoltDown((door, bolts), false, owner, predicted: false))
                return false;
        }

        if (_access.IsAllowed(owner, door) && !_doors.IsBolted(door))
            return _doors.TryOpen(door, user: owner);

        return _emag.TryEmagEffect(disruptor.Value, owner, door);
    }

    private bool TryPryDoor(EntityUid owner, EntityUid door, out DoAfterId? doAfterId)
    {
        doAfterId = null;
        if (TryFindPryTool(owner, out var tool))
        {
            if (!_ammo.TryObtainInHand(owner, tool.Value))
                return false;
            return _prying.TryPry(door, owner, out doAfterId, tool.Value);
        }

        return _prying.TryPry(door, owner, out doAfterId);
    }

    private bool TryStickBreachCharge(EntityUid owner, EntityUid door)
    {
        if (!TryFindBreachCharge(owner, out var charge) || !_ammo.TryObtainInHand(owner, charge.Value))
            return false;

        if (!TryComp(charge.Value, out StickyComponent? sticky))
            return false;

        _sticky.StickToEntity((charge.Value, sticky), door, owner);
        return sticky.StuckTo == door;
    }

    private bool TryMeleeBreach(EntityUid owner, EntityUid door)
    {
        if (!_melee.TryGetWeapon(owner, out var weaponUid, out var weapon))
            return false;

        if (weapon.Damage.GetTotal().Float() < MinMeleeBreachDamage)
            return false;

        return _melee.AttemptLightAttack(owner, weaponUid, weapon, door);
    }

    private bool HasBreachMelee(EntityUid owner)
    {
        if (!_melee.TryGetWeapon(owner, out _, out var weapon))
            return false;
        return weapon.Damage.GetTotal().Float() >= MinMeleeBreachDamage;
    }

    /// <summary>
    /// Authentication disruptor / AccessBreaker — Emag with Access flag.
    /// Cryptographic EMAG (Interaction) cannot doorjack airlocks.
    /// </summary>
    private bool IsAccessDisruptor(EntityUid uid)
    {
        return TryComp<EmagComponent>(uid, out var emag) &&
               !emag.Demag &&
               _emag.CompareFlag(emag.EmagType, EmagType.Access);
    }

    private bool TryFindAccessDisruptor(EntityUid owner, [NotNullWhen(true)] out EntityUid? disruptor)
    {
        return TryFindInInventory(owner, IsAccessDisruptor, out disruptor);
    }

    private bool TryFindPryTool(EntityUid owner, [NotNullWhen(true)] out EntityUid? tool)
    {
        return TryFindInInventory(owner, HasComp<PryingComponent>, out tool);
    }

    private bool TryFindBreachCharge(EntityUid owner, [NotNullWhen(true)] out EntityUid? charge)
    {
        return TryFindInInventory(owner, IsBreachCharge, out charge);
    }

    private bool IsBreachCharge(EntityUid uid)
    {
        if (!HasComp<StickyComponent>(uid))
            return false;

        if (MetaData(uid).EntityPrototype is not { } proto)
            return false;

        return proto.ID == C4Proto.Id || proto.ID == SeismicProto.Id;
    }

    private bool TryFindInInventory(EntityUid owner, Func<EntityUid, bool> pred, [NotNullWhen(true)] out EntityUid? item)
    {
        item = null;
        foreach (var held in _hands.EnumerateHeld(owner))
        {
            if (!pred(held))
                continue;
            item = held;
            return true;
        }

        if (!_inventory.TryGetContainerSlotEnumerator(owner, out var slots))
            return false;

        while (slots.MoveNext(out var slot))
        {
            foreach (var ent in slot.ContainedEntities)
            {
                if (!pred(ent))
                    continue;
                item = ent;
                return true;
            }
        }

        return false;
    }
}
