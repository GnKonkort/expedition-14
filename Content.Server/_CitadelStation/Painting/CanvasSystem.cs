using Content.Server.Administration.Logs;
using Content.Shared._CitadelStation.Painting;
using Content.Shared.Database;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._CitadelStation.Painting;

public sealed class CanvasSystem : SharedCanvasSystem
{
    private static readonly EntProtoId ItemPrototype = "ArtistCanvas";
    private static readonly EntProtoId WallmountPrototype = "ArtistPaintingWallmount";
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly SharedEaselSystem _easel = default!;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<CanvasComponent>(CanvasUiKey.Key, subs =>
        {
            subs.Event<CanvasUpdateLayersMessage>(OnUpdateLayers);
            subs.Event<CanvasSetSizeMessage>(OnSetSize);
            subs.Event<CanvasSetFrameMessage>(OnSetFrame);
            subs.Event<CanvasSetTitleMessage>(OnSetTitle);
            subs.Event<CanvasLockMessage>(OnLock);
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<BoundUIClosedEvent>(OnUiClosed);
        });

        SubscribeLocalEvent<CanvasComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<CanvasComponent, InteractHandEvent>(OnInteractHand);
    }

    private void OnUiOpened(Entity<CanvasComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUiState(ent);
    }

    private void OnUiClosed(Entity<CanvasComponent> ent, ref BoundUIClosedEvent args)
    {
        _easel.EndEditSession(args.Actor, ent.Owner);
    }

    public void UpdateUiState(Entity<CanvasComponent> ent)
    {
        if (!_ui.HasUi(ent.Owner, CanvasUiKey.Key))
            return;

        var canEdit = !ent.Comp.Locked && !ent.Comp.Mounted;
        var state = new CanvasBoundUserInterfaceState(
            ent.Comp.Size,
            CloneLayers(ent.Comp.Layers),
            ent.Comp.ActiveLayer,
            ent.Comp.FrameId?.Id,
            ent.Comp.Title,
            ent.Comp.Signature,
            ent.Comp.Locked,
            canEdit);

        _ui.SetUiState(ent.Owner, CanvasUiKey.Key, state);
    }

    public bool TryOpenUi(EntityUid canvas, EntityUid user)
    {
        if (!TryComp<CanvasComponent>(canvas, out var comp))
            return false;

        if (comp.Mounted || !_ui.HasUi(canvas, CanvasUiKey.Key))
            return false;

        _ui.OpenUi(canvas, CanvasUiKey.Key, user);
        UpdateUiState((canvas, comp));
        return true;
    }

    private void OnUpdateLayers(Entity<CanvasComponent> ent, ref CanvasUpdateLayersMessage args)
    {
        if (ent.Comp.Locked || ent.Comp.Mounted)
            return;

        if (!ValidateLayers(ent.Comp.Size, args.Layers, out _))
            return;

        ent.Comp.Layers = CloneLayers(args.Layers);
        ent.Comp.ActiveLayer = Math.Clamp(args.ActiveLayer, 0, ent.Comp.Layers.Count - 1);
        Dirty(ent);

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} updated layers on {ToPrettyString(ent):entity}");
    }

    private void OnSetSize(Entity<CanvasComponent> ent, ref CanvasSetSizeMessage args)
    {
        if (ent.Comp.Locked || ent.Comp.Mounted)
            return;

        if (!TryResizeCanvas(ent.Comp, args.Size))
            return;

        Dirty(ent);
        UpdateUiState(ent);

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} set canvas size {args.Size} on {ToPrettyString(ent):entity}");
    }

    private void OnSetFrame(Entity<CanvasComponent> ent, ref CanvasSetFrameMessage args)
    {
        if (ent.Comp.Locked || ent.Comp.Mounted)
            return;

        if (string.IsNullOrEmpty(args.FrameId))
        {
            ent.Comp.FrameId = null;
        }
        else
        {
            if (!_prototype.TryIndex<PaintingFramePrototype>(args.FrameId, out _))
                return;

            ent.Comp.FrameId = args.FrameId;
        }

        Dirty(ent);
        UpdateUiState(ent);
    }

    private void OnSetTitle(Entity<CanvasComponent> ent, ref CanvasSetTitleMessage args)
    {
        if (ent.Comp.Locked || ent.Comp.Mounted)
            return;

        ent.Comp.Title = SanitizeTitle(args.Title);
        ApplyDisplayName(ent);
        Dirty(ent);
        UpdateUiState(ent);
    }

    private void OnLock(Entity<CanvasComponent> ent, ref CanvasLockMessage args)
    {
        if (ent.Comp.Locked || ent.Comp.Mounted)
            return;

        ent.Comp.Title = SanitizeTitle(args.Title);
        if (args.Sign)
            ent.Comp.Signature = Identity.Name(args.Actor, EntityManager);

        ent.Comp.Locked = true;
        ApplyDisplayName(ent);
        Dirty(ent);
        UpdateUiState(ent);
    }

    private void ApplyDisplayName(Entity<CanvasComponent> ent)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.Title))
            return;

        _metaData.SetEntityName(ent.Owner, ent.Comp.Title);
    }

    private void OnAfterInteract(EntityUid uid, CanvasComponent component, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target == null)
            return;

        if (component.Mounted)
            return;

        // Insert into easel
        if (TryComp<EaselComponent>(args.Target.Value, out _))
        {
            if (EntityManager.System<EaselSystem>().TryInsert(args.Target.Value, uid, args.User))
                args.Handled = true;
            return;
        }

        if (!_tag.HasTag(args.Target.Value, WallTag))
            return;

        if (!TryHang(uid, component, args.User, args.Target.Value))
            return;

        args.Handled = true;
    }

    private void OnInteractHand(EntityUid uid, CanvasComponent component, InteractHandEvent args)
    {
        if (args.Handled || !component.Mounted)
            return;

        if (!TryUnhang(uid, component, args.User))
            return;

        args.Handled = true;
    }

    private bool TryHang(EntityUid item, CanvasComponent component, EntityUid user, EntityUid wall)
    {
        var wallXform = Transform(wall);
        var coords = wallXform.Coordinates;

        var wallmount = Spawn(WallmountPrototype, coords);
        var wallmountXform = Transform(wallmount);
        _transform.AnchorEntity(wallmount, wallmountXform);

        var userRot = _transform.GetWorldRotation(user);
        _transform.SetLocalRotation(wallmount, userRot.GetCardinalDir().ToAngle());

        if (!TryComp<CanvasComponent>(wallmount, out var mountedComp))
        {
            QueueDel(wallmount);
            return false;
        }

        CopyCanvasData(component, mountedComp);
        mountedComp.Mounted = true;
        Dirty(wallmount, mountedComp);
        ApplyDisplayName((wallmount, mountedComp));

        _popup.PopupEntity(Loc.GetString("canvas-hang-success"), wallmount, user);
        QueueDel(item);
        return true;
    }

    private bool TryUnhang(EntityUid wallmount, CanvasComponent component, EntityUid user)
    {
        var item = Spawn(ItemPrototype, Transform(user).Coordinates);
        if (!TryComp<CanvasComponent>(item, out var itemComp))
        {
            QueueDel(item);
            return false;
        }

        CopyCanvasData(component, itemComp);
        itemComp.Mounted = false;
        Dirty(item, itemComp);
        ApplyDisplayName((item, itemComp));

        if (!_hands.TryPickupAnyHand(user, item))
            _transform.SetCoordinates(item, Transform(wallmount).Coordinates);

        _popup.PopupEntity(Loc.GetString("canvas-unhang-success"), item, user);
        QueueDel(wallmount);
        return true;
    }
}
