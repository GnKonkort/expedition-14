using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Spawns a 3x3 sub-grid with a center antigrav when activated from the front with enough sheet stacks in slots.
/// </summary>
public sealed class SubGridPadSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly SubGridSystem _subGrids = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SubGridPadComponent, ActivateInWorldEvent>(OnActivate);
        Log.Info("SubGridPadSystem initialized");
    }

    private void OnActivate(Entity<SubGridPadComponent> ent, ref ActivateInWorldEvent args)
    {
        Log.Debug("Pad activate: pad={Pad} user={User} handled={Handled} complex={Complex}",
            ToPrettyString(ent), ToPrettyString(args.User), args.Handled, args.Complex);

        if (args.Handled || !args.Complex)
        {
            Log.Debug("Pad activate ignored (handled or not complex): pad={Pad}", ToPrettyString(ent));
            return;
        }

        args.Handled = true;

        if (!IsFrontActivation(ent, args.User))
        {
            Log.Warning("Pad activate rejected: not front face. pad={Pad} user={User}",
                ToPrettyString(ent), ToPrettyString(args.User));
            _popup.PopupEntity(Loc.GetString("subgrid-pad-need-front"), ent, args.User);
            return;
        }

        // Slots share a Sheet whitelist, so insert order may swap steel/uranium —
        // resolve by stack type across both slots, not by slot id.
        if (!TryFindRequiredSheets(ent, out var steel, out var uranium))
        {
            Log.Warning("Pad activate rejected: materials. pad={Pad}", ToPrettyString(ent));
            _popup.PopupEntity(Loc.GetString("subgrid-pad-need-materials"), ent, args.User);
            return;
        }

        if (!TrySpawnSubGrid(ent, out var gridUid))
        {
            Log.Error("Pad spawn failed. pad={Pad}", ToPrettyString(ent));
            _popup.PopupEntity(Loc.GetString("subgrid-pad-blocked"), ent, args.User);
            return;
        }

        Log.Info("Pad spawn ok: pad={Pad} grid={Grid} consuming sheets steel={Steel} uranium={Uranium}",
            ToPrettyString(ent), ToPrettyString(gridUid),
            ent.Comp.SteelSheetsRequired, ent.Comp.UraniumSheetsRequired);

        _stacks.Use(steel.Owner, ent.Comp.SteelSheetsRequired, steel.Comp);
        _stacks.Use(uranium.Owner, ent.Comp.UraniumSheetsRequired, uranium.Comp);

        _popup.PopupEntity(Loc.GetString("subgrid-pad-spawned"), ent, args.User);
    }

    private bool TryFindRequiredSheets(
        Entity<SubGridPadComponent> ent,
        out Entity<StackComponent> steel,
        out Entity<StackComponent> uranium)
    {
        steel = default;
        uranium = default;

        Entity<StackComponent>? foundSteel = null;
        Entity<StackComponent>? foundUranium = null;

        ReadSlot(ent, ent.Comp.SteelSlotId, ref foundSteel, ref foundUranium);
        ReadSlot(ent, ent.Comp.UraniumSlotId, ref foundSteel, ref foundUranium);

        var steelOk = foundSteel != null && foundSteel.Value.Comp.Count >= ent.Comp.SteelSheetsRequired;
        var uraniumOk = foundUranium != null && foundUranium.Value.Comp.Count >= ent.Comp.UraniumSheetsRequired;

        Log.Debug(
            "Pad materials: pad={Pad} steel={SteelCount}/{SteelNeed} uranium={UraniumCount}/{UraniumNeed} steelOk={SteelOk} uraniumOk={UraniumOk}",
            ToPrettyString(ent),
            foundSteel?.Comp.Count ?? 0,
            ent.Comp.SteelSheetsRequired,
            foundUranium?.Comp.Count ?? 0,
            ent.Comp.UraniumSheetsRequired,
            steelOk,
            uraniumOk);

        if (!steelOk || !uraniumOk || foundSteel == null || foundUranium == null)
            return false;

        steel = foundSteel.Value;
        uranium = foundUranium.Value;
        return true;
    }

    private void ReadSlot(
        Entity<SubGridPadComponent> ent,
        string slotId,
        ref Entity<StackComponent>? foundSteel,
        ref Entity<StackComponent>? foundUranium)
    {
        var item = _slots.GetItemOrNull(ent, slotId);
        if (item == null)
        {
            Log.Debug("Pad slot empty: pad={Pad} slot={Slot}", ToPrettyString(ent), slotId);
            return;
        }

        if (!TryComp<StackComponent>(item.Value, out var stack))
        {
            Log.Warning("Pad slot item has no Stack: pad={Pad} slot={Slot} item={Item}",
                ToPrettyString(ent), slotId, ToPrettyString(item.Value));
            return;
        }

        Log.Debug("Pad slot contents: pad={Pad} slot={Slot} item={Item} type={Type} count={Count}",
            ToPrettyString(ent), slotId, ToPrettyString(item.Value), stack.StackTypeId, stack.Count);

        if (stack.StackTypeId == ent.Comp.SteelStackType)
            foundSteel = (item.Value, stack);
        else if (stack.StackTypeId == ent.Comp.UraniumStackType)
            foundUranium = (item.Value, stack);
        else
        {
            Log.Debug("Pad slot ignored (wrong material): pad={Pad} slot={Slot} type={Type}",
                ToPrettyString(ent), slotId, stack.StackTypeId);
        }
    }

    private bool IsFrontActivation(Entity<SubGridPadComponent> ent, EntityUid user)
    {
        var padXform = Transform(ent);
        var padPos = _transform.GetWorldPosition(padXform);
        var userPos = _transform.GetWorldPosition(user);
        var delta = userPos - padPos;
        if (delta.LengthSquared() < 0.0001f)
            return true;

        var facing = _transform.GetWorldRotation(padXform).ToWorldVec();
        if (facing == Vector2.Zero)
            return true;

        var dot = Vector2.Dot(facing.Normalized(), delta.Normalized());
        Log.Debug("Pad front check: pad={Pad} user={User} dot={Dot:F2} min={Min:F2}",
            ToPrettyString(ent), ToPrettyString(user), dot, ent.Comp.FrontDotMin);
        return dot >= ent.Comp.FrontDotMin;
    }

    private bool TrySpawnSubGrid(Entity<SubGridPadComponent> ent, out EntityUid gridUid)
    {
        gridUid = default;

        var padXform = Transform(ent);
        if (padXform.MapUid == null)
        {
            Log.Error("Pad spawn: pad not on a map. pad={Pad}", ToPrettyString(ent));
            return false;
        }

        var mapId = padXform.MapID;
        var facing = _transform.GetWorldRotation(padXform);
        var facingVec = facing.ToWorldVec();
        if (facingVec == Vector2.Zero)
            facingVec = new Vector2(0, 1);
        else
            facingVec = facingVec.Normalized();

        var padWorld = _transform.GetWorldPosition(padXform);
        var size = ent.Comp.GridSize;
        var half = size / 2f;
        var centerWorld = padWorld + facingVec * ent.Comp.SpawnOffsetTiles;

        Log.Debug("Pad spawn layout: pad={Pad} map={Map} padWorld={PadWorld} center={Center} facing={Facing} size={Size}",
            ToPrettyString(ent), mapId, padWorld, centerWorld, facing, size);

        var checkBox = Box2.CenteredAround(centerWorld, new Vector2(size + 0.25f, size + 0.25f));
        var grids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(mapId, checkBox, ref grids);
        Log.Debug("Pad spawn overlap check: pad={Pad} intersectingGrids={Count} box={Box}",
            ToPrettyString(ent), grids.Count, checkBox);
        foreach (var g in grids)
        {
            Log.Debug("Pad spawn overlap candidate: {Grid} isSub={Sub}",
                ToPrettyString(g.Owner), HasComp<SubGridComponent>(g.Owner));
            if (HasComp<SubGridComponent>(g.Owner))
            {
                Log.Warning("Pad spawn blocked by existing sub-grid: {Grid} aabb={Aabb}",
                    ToPrettyString(g.Owner), checkBox);
                return false;
            }
        }

        ContentTileDefinition tileDef;
        try
        {
            tileDef = (ContentTileDefinition) _tileDefs[ent.Comp.FloorTileId];
        }
        catch (Exception e)
        {
            Log.Error("Pad spawn: bad floor tile id '{Tile}': {Error}", ent.Comp.FloorTileId, e.Message);
            return false;
        }

        var grid = _mapManager.CreateGridEntity(mapId);
        gridUid = grid.Owner;
        Log.Debug("Pad spawned grid entity {Grid}", ToPrettyString(gridUid));

        var originWorld = centerWorld - facing.RotateVec(new Vector2(half, half));
        _transform.SetWorldPosition(gridUid, originWorld);
        _transform.SetWorldRotation(gridUid, facing);

        var tiles = new List<(Vector2i Index, Tile Tile)>(size * size);
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                tiles.Add((new Vector2i(x, y), new Tile(tileDef.TileId)));
            }
        }

        _map.SetTiles(gridUid, grid.Comp, tiles);
        Log.Debug("Pad set {Count} tiles on {Grid} origin={Origin}", tiles.Count, ToPrettyString(gridUid), originWorld);

        var sub = EnsureComp<SubGridComponent>(gridUid);
        Dirty(gridUid, sub);
        EnsureComp<Content.Server.Atmos.Components.GridAtmosphereComponent>(gridUid);
        var gravity = EnsureComp<Content.Shared.Gravity.GravityComponent>(gridUid);
        gravity.Inherent = true;
        Dirty(gridUid, gravity);
        Log.Debug("Pad ensured atmos+gravity comps on {Grid}", ToPrettyString(gridUid));

        if (TryComp<ShuttleComponent>(gridUid, out var shuttle))
        {
            // Parked: Kinematic — wall contacts work, players don't shove the hull.
            shuttle.Enabled = false;
            if (TryComp(gridUid, out PhysicsComponent? body))
            {
                _physics.SetBodyType(gridUid, BodyType.Kinematic, body: body);
                _physics.SetFixedRotation(gridUid, true, body: body);
            }

            Log.Debug("Pad set Kinematic body (parked) on {Grid}", ToPrettyString(gridUid));
        }
        else
        {
            Log.Warning("Pad: no ShuttleComponent on new grid {Grid}", ToPrettyString(gridUid));
        }

        try
        {
            SpawnPadHardware(ent, gridUid, size, half);
        }
        catch (Exception e)
        {
            Log.Error("Pad spawn entities failed on {Grid}: {Error}", ToPrettyString(gridUid), e);
            QueueDel(gridUid);
            gridUid = default;
            return false;
        }

        _subGrids.RebuildBumper((gridUid, sub));
        _subGrids.SyncAllPerimeters(gridUid);
        Log.Info("Pad spawn complete: {Grid}", ToPrettyString(gridUid));
        return true;
    }

    /// <summary>
    /// Spawns antigrav, boarding stairs, shuttle console, cardinal thrusters, and a gyroscope.
    /// Drive input comes from the console (same shuttle piloting path as normal ships).
    /// </summary>
    private void SpawnPadHardware(Entity<SubGridPadComponent> ent, EntityUid gridUid, int size, float half)
    {
        var antigrav = Spawn(ent.Comp.AntigravPrototype, new EntityCoordinates(gridUid, half, half));
        Log.Debug("Pad spawned antigrav {Ent} at ({X},{Y})", ToPrettyString(antigrav), half, half);

        var stair = Spawn("SubGridStairs", new EntityCoordinates(gridUid, half, 0.5f));
        Log.Debug("Pad spawned stairs {Ent} at ({X},{Y})", ToPrettyString(stair), half, 0.5f);

        // Console on SW tile — clear of stairs (south mid) and antigrav (center).
        var console = Spawn(ent.Comp.ConsolePrototype, new EntityCoordinates(gridUid, 0.5f, 0.5f));
        Log.Debug("Pad spawned console {Ent}", ToPrettyString(console));

        // Mid-edge thrusters for N/E/W; south thruster offset to SE (stairs occupy south mid).
        SpawnOriented(ent.Comp.ThrusterPrototype, gridUid, new Vector2(half, size - 0.5f), Direction.North);
        SpawnOriented(ent.Comp.ThrusterPrototype, gridUid, new Vector2(size - 0.5f, half), Direction.East);
        SpawnOriented(ent.Comp.ThrusterPrototype, gridUid, new Vector2(size - 0.5f, 0.5f), Direction.South);
        SpawnOriented(ent.Comp.ThrusterPrototype, gridUid, new Vector2(0.5f, half), Direction.West);

        var gyro = Spawn(ent.Comp.GyroscopePrototype, new EntityCoordinates(gridUid, size - 0.5f, size - 0.5f));
        Log.Debug("Pad spawned gyroscope {Ent}", ToPrettyString(gyro));
    }

    private void SpawnOriented(EntProtoId prototype, EntityUid gridUid, Vector2 local, Direction facing)
    {
        var uid = Spawn(prototype, new EntityCoordinates(gridUid, local));
        _transform.SetLocalRotation(uid, facing.ToAngle());
        Log.Debug("Pad spawned {Proto} {Ent} at {Pos} facing {Dir}",
            prototype, ToPrettyString(uid), local, facing);
    }
}
