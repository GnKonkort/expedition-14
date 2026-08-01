using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.Shuttles.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared._CitadelStation.SubGrid;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// SubGrid fabricator: opens a 7×15 design UI and assembles a custom-shaped pad.
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
    [Dependency] private readonly SubGridSystem _subGrids = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SubGridPadComponent, AfterActivatableUIOpenEvent>(OnUiOpened);
        SubscribeLocalEvent<SubGridPadComponent, SubGridFabricatorAssembleMessage>(OnAssemble);
        SubscribeLocalEvent<SubGridPadComponent, SubGridFabricatorClearMessage>(OnClear);
        Log.Info("SubGridPadSystem initialized (fabricator UI)");
    }

    private void OnUiOpened(Entity<SubGridPadComponent> ent, ref AfterActivatableUIOpenEvent args)
    {
        UpdateUiState(ent, args.Actor);
    }

    private void OnClear(Entity<SubGridPadComponent> ent, ref SubGridFabricatorClearMessage args)
    {
        UpdateUiState(ent, args.Actor);
    }

    private void OnAssemble(Entity<SubGridPadComponent> ent, ref SubGridFabricatorAssembleMessage args)
    {
        var user = args.Actor;
        var cells = args.Cells;

        if (cells.Length != SubGridFabricatorConstants.CellCount)
        {
            _popup.PopupEntity(Loc.GetString("subgrid-fab-err-empty"), ent, user);
            return;
        }

        if (!IsFrontActivation(ent, user))
        {
            _popup.PopupEntity(Loc.GetString("subgrid-pad-need-front"), ent, user);
            return;
        }

        if (!TryValidateBlueprint(cells, out var error, out var occupied))
        {
            _popup.PopupEntity(error, ent, user);
            UpdateUiState(ent, user);
            return;
        }

        if (!TryFindRequiredSheets(ent, out var steel, out var uranium))
        {
            _popup.PopupEntity(Loc.GetString("subgrid-pad-need-materials"), ent, user);
            UpdateUiState(ent, user);
            return;
        }

        if (!TrySpawnFromBlueprint(ent, cells, occupied, out var gridUid))
        {
            _popup.PopupEntity(Loc.GetString("subgrid-pad-blocked"), ent, user);
            return;
        }

        _stacks.Use(steel.Owner, ent.Comp.SteelSheetsRequired, steel.Comp);
        _stacks.Use(uranium.Owner, ent.Comp.UraniumSheetsRequired, uranium.Comp);

        _popup.PopupEntity(Loc.GetString("subgrid-pad-spawned"), ent, user);
        _ui.CloseUi(ent.Owner, SubGridFabricatorUiKey.Key, user);
        UpdateUiState(ent, user);
        Log.Info("Fabricator assembled SubGrid {Grid} from {Pad} tiles={Tiles}",
            ToPrettyString(gridUid), ToPrettyString(ent), occupied.Count);
    }

    private void UpdateUiState(Entity<SubGridPadComponent> ent, EntityUid? actor = null)
    {
        CountMaterials(ent, out var steelHave, out var uraniumHave);
        var state = new SubGridFabricatorBoundUserInterfaceState(
            new SubGridFabricatorCell[SubGridFabricatorConstants.CellCount],
            steelHave,
            uraniumHave,
            ent.Comp.SteelSheetsRequired,
            ent.Comp.UraniumSheetsRequired);

        if (actor != null)
            _ui.SetUiState(ent.Owner, SubGridFabricatorUiKey.Key, state);
        else
            _ui.SetUiState(ent.Owner, SubGridFabricatorUiKey.Key, state);
    }

    private void CountMaterials(Entity<SubGridPadComponent> ent, out int steel, out int uranium)
    {
        steel = 0;
        uranium = 0;
        Entity<StackComponent>? foundSteel = null;
        Entity<StackComponent>? foundUranium = null;
        ReadSlot(ent, ent.Comp.SteelSlotId, ref foundSteel, ref foundUranium);
        ReadSlot(ent, ent.Comp.UraniumSlotId, ref foundSteel, ref foundUranium);
        steel = foundSteel?.Comp.Count ?? 0;
        uranium = foundUranium?.Comp.Count ?? 0;
    }

    private bool TryValidateBlueprint(
        SubGridFabricatorCell[] cells,
        out string error,
        out List<(int X, int Y, SubGridFabricatorCell Cell)> occupied)
    {
        error = string.Empty;
        occupied = new();

        if (!SubGridFabricatorConstants.TryValidate(cells, out var errId))
        {
            error = Loc.GetString(errId);
            return false;
        }

        for (var x = 0; x < SubGridFabricatorConstants.Width; x++)
        {
            for (var y = 0; y < SubGridFabricatorConstants.Height; y++)
            {
                var cell = cells[SubGridFabricatorConstants.Index(x, y)];
                if (!SubGridFabricatorConstants.IsOccupied(cell))
                    continue;
                occupied.Add((x, y, cell));
            }
        }

        return true;
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

        if (foundSteel == null || foundUranium == null)
            return false;
        if (foundSteel.Value.Comp.Count < ent.Comp.SteelSheetsRequired)
            return false;
        if (foundUranium.Value.Comp.Count < ent.Comp.UraniumSheetsRequired)
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
        if (item == null || !TryComp<StackComponent>(item.Value, out var stack))
            return;

        if (stack.StackTypeId == ent.Comp.SteelStackType)
            foundSteel = (item.Value, stack);
        else if (stack.StackTypeId == ent.Comp.UraniumStackType)
            foundUranium = (item.Value, stack);
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

        return Vector2.Dot(facing.Normalized(), delta.Normalized()) >= ent.Comp.FrontDotMin;
    }

    private bool TrySpawnFromBlueprint(
        Entity<SubGridPadComponent> ent,
        SubGridFabricatorCell[] cells,
        List<(int X, int Y, SubGridFabricatorCell Cell)> occupied,
        out EntityUid gridUid)
    {
        gridUid = default;

        var padXform = Transform(ent);
        if (padXform.MapUid == null)
            return false;

        var mapId = padXform.MapID;
        var facing = _transform.GetWorldRotation(padXform);
        var facingVec = facing.ToWorldVec();
        if (facingVec == Vector2.Zero)
            facingVec = new Vector2(0, 1);
        else
            facingVec = facingVec.Normalized();

        var minX = occupied.Min(c => c.X);
        var maxX = occupied.Max(c => c.X);
        var minY = occupied.Min(c => c.Y);
        var maxY = occupied.Max(c => c.Y);
        var sizeX = maxX - minX + 1;
        var sizeY = maxY - minY + 1;

        var padWorld = _transform.GetWorldPosition(padXform);
        var centerWorld = padWorld + facingVec * MathF.Max(ent.Comp.SpawnOffsetTiles, MathF.Max(sizeX, sizeY) * 0.5f + 1.5f);

        var checkBox = Box2.CenteredAround(centerWorld, new Vector2(sizeX + 0.5f, sizeY + 0.5f));
        var grids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(mapId, checkBox, ref grids);
        foreach (var g in grids)
        {
            if (HasComp<SubGridComponent>(g.Owner))
                return false;
        }

        ContentTileDefinition tileDef;
        try
        {
            tileDef = (ContentTileDefinition) _tileDefs[ent.Comp.FloorTileId];
        }
        catch (Exception e)
        {
            Log.Error("Fabricator bad floor tile '{Tile}': {Error}", ent.Comp.FloorTileId, e.Message);
            return false;
        }

        var grid = _mapManager.CreateGridEntity(mapId);
        gridUid = grid.Owner;

        // Remap blueprint AABB so local (0,0) is SW; place AABB center at centerWorld.
        var originWorld = centerWorld - facing.RotateVec(new Vector2(sizeX / 2f, sizeY / 2f));
        _transform.SetWorldPosition(gridUid, originWorld);
        _transform.SetWorldRotation(gridUid, facing);

        var tiles = new List<(Vector2i Index, Tile Tile)>(occupied.Count);
        foreach (var (x, y, _) in occupied)
        {
            var lx = x - minX;
            var ly = y - minY;
            tiles.Add((new Vector2i(lx, ly), new Tile(tileDef.TileId)));
        }

        _map.SetTiles(gridUid, grid.Comp, tiles);

        var sub = EnsureComp<SubGridComponent>(gridUid);
        sub.MaxTiles = 128;
        Dirty(gridUid, sub);
        EnsureComp<GridAtmosphereComponent>(gridUid);
        var gravity = EnsureComp<GravityComponent>(gridUid);
        gravity.Inherent = true;
        Dirty(gridUid, gravity);

        if (TryComp<ShuttleComponent>(gridUid, out var shuttle) && TryComp(gridUid, out PhysicsComponent? body))
        {
            shuttle.Enabled = false;
            _physics.SetBodyType(gridUid, BodyType.Kinematic, body: body);
            _physics.SetFixedRotation(gridUid, true, body: body);
        }

        try
        {
            SpawnBlueprintHardware(ent, gridUid, cells, occupied, minX, minY, sizeX, sizeY);
        }
        catch (Exception e)
        {
            Log.Error("Fabricator hardware spawn failed on {Grid}: {Error}", ToPrettyString(gridUid), e);
            QueueDel(gridUid);
            gridUid = default;
            return false;
        }

        _subGrids.RebuildBumper((gridUid, sub));
        _subGrids.SyncAllPerimeters(gridUid);
        return true;
    }

    private void SpawnBlueprintHardware(
        Entity<SubGridPadComponent> ent,
        EntityUid gridUid,
        SubGridFabricatorCell[] cells,
        List<(int X, int Y, SubGridFabricatorCell Cell)> occupied,
        int minX,
        int minY,
        int sizeX,
        int sizeY)
    {
        foreach (var (x, y, cell) in occupied)
        {
            var local = new Vector2(x - minX + 0.5f, y - minY + 0.5f);
            switch (cell)
            {
                case SubGridFabricatorCell.Core:
                    Spawn(ent.Comp.AntigravPrototype, new EntityCoordinates(gridUid, local));
                    break;
                case SubGridFabricatorCell.Console:
                    Spawn(ent.Comp.ConsolePrototype, new EntityCoordinates(gridUid, local));
                    break;
                case SubGridFabricatorCell.Gyroscope:
                    Spawn(ent.Comp.GyroscopePrototype, new EntityCoordinates(gridUid, local));
                    break;
                case SubGridFabricatorCell.StairsNorth:
                case SubGridFabricatorCell.StairsEast:
                case SubGridFabricatorCell.StairsSouth:
                case SubGridFabricatorCell.StairsWest:
                {
                    var stair = Spawn(ent.Comp.StairsPrototype, new EntityCoordinates(gridUid, local));
                    _transform.SetLocalRotation(stair, CardinalDirection(cell).ToAngle());
                    var access = EnsureComp<SubGridAccessComponent>(stair);
                    access.OwnerGrid = gridUid;
                    access.Tile = new Vector2i(x - minX, y - minY);
                    Dirty(stair, access);
                    _subGrids.RegisterBoardingTile(gridUid, access.Tile);
                    break;
                }
                case SubGridFabricatorCell.ThrusterNorth:
                case SubGridFabricatorCell.ThrusterEast:
                case SubGridFabricatorCell.ThrusterSouth:
                case SubGridFabricatorCell.ThrusterWest:
                {
                    var thruster = Spawn(ent.Comp.ThrusterPrototype, new EntityCoordinates(gridUid, local));
                    // Thruster exhaust faces opposite the placement cardinal (sprite/thrust axis).
                    _transform.SetLocalRotation(thruster, CardinalDirection(cell).GetOpposite().ToAngle());
                    break;
                }
            }
        }
    }

    private static Direction CardinalDirection(SubGridFabricatorCell cell) => cell switch
    {
        SubGridFabricatorCell.StairsNorth or SubGridFabricatorCell.ThrusterNorth => Direction.North,
        SubGridFabricatorCell.StairsEast or SubGridFabricatorCell.ThrusterEast => Direction.East,
        SubGridFabricatorCell.StairsSouth or SubGridFabricatorCell.ThrusterSouth => Direction.South,
        SubGridFabricatorCell.StairsWest or SubGridFabricatorCell.ThrusterWest => Direction.West,
        _ => Direction.South,
    };
}
