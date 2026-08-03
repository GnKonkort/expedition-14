using Content.Shared._CitadelStation.Painting;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Utility;

namespace Content.Server._CitadelStation.Painting;

public sealed class EaselSystem : SharedEaselSystem
{
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly CanvasSystem _canvas = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EaselComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<EaselComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<EaselComponent, AfterInteractUsingEvent>(OnAfterInteractUsing);
        SubscribeLocalEvent<EaselComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<EaselComponent, EntRemovedFromContainerMessage>(OnRemoved);

        Subs.BuiEvents<EaselComponent>(EaselUiKey.Key, subs =>
        {
            subs.Event<EaselSelectSlotMessage>(OnSelectSlot);
            subs.Event<EaselEjectSlotMessage>(OnEjectSlot);
            subs.Event<EaselOpenCanvasMessage>(OnOpenCanvas);
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
        });
    }

    private void OnUseInHand(EntityUid uid, EaselComponent component, UseInHandEvent args)
    {
        if (args.Handled)
            return;

        OpenUi(uid, args.User);
        args.Handled = true;
    }

    private void OnActivate(EntityUid uid, EaselComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        OpenUi(uid, args.User);
        args.Handled = true;
    }

    private void OnAfterInteractUsing(EntityUid uid, EaselComponent component, AfterInteractUsingEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        if (!HasComp<CanvasComponent>(args.Used))
            return;

        if (TryInsert(uid, args.Used, args.User))
            args.Handled = true;
    }

    private void OnInserted(EntityUid uid, EaselComponent component, EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != EaselComponent.ContainerId)
            return;

        Dirty(uid, component);
        UpdateUiState(uid, component);
        UpdateAppearance(uid, component);
    }

    private void OnRemoved(EntityUid uid, EaselComponent component, EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != EaselComponent.ContainerId)
            return;

        if (component.ActiveIndex >= component.Container.ContainedEntities.Count)
            component.ActiveIndex = Math.Max(0, component.Container.ContainedEntities.Count - 1);

        Dirty(uid, component);
        UpdateUiState(uid, component);
        UpdateAppearance(uid, component);
    }

    private void OnUiOpened(Entity<EaselComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUiState(ent.Owner, ent.Comp);
    }

    private void OnSelectSlot(Entity<EaselComponent> ent, ref EaselSelectSlotMessage args)
    {
        var count = ent.Comp.Container.ContainedEntities.Count;
        if (count == 0 || args.Index < 0 || args.Index >= count)
            return;

        ent.Comp.ActiveIndex = args.Index;
        Dirty(ent);
        UpdateUiState(ent.Owner, ent.Comp);
        UpdateAppearance(ent.Owner, ent.Comp);
    }

    private void OnEjectSlot(Entity<EaselComponent> ent, ref EaselEjectSlotMessage args)
    {
        var contained = ent.Comp.Container.ContainedEntities;
        if (args.Index < 0 || args.Index >= contained.Count)
            return;

        var painting = contained[args.Index];
        if (!_containers.Remove(painting, ent.Comp.Container))
            return;

        if (!_hands.TryPickupAnyHand(args.Actor, painting))
            _transform.DropNextTo(painting, ent.Owner);

        UpdateUiState(ent.Owner, ent.Comp);
        UpdateAppearance(ent.Owner, ent.Comp);
    }

    private void OnOpenCanvas(Entity<EaselComponent> ent, ref EaselOpenCanvasMessage args)
    {
        var contained = ent.Comp.Container.ContainedEntities;
        if (args.Index < 0 || args.Index >= contained.Count)
            return;

        var canvas = contained[args.Index];
        BeginEditSession(args.Actor, ent.Owner, canvas);
        if (!_canvas.TryOpenUi(canvas, args.Actor))
            EndEditSession(args.Actor, canvas);
    }

    public bool TryInsert(EntityUid easel, EntityUid painting, EntityUid user)
    {
        if (!TryComp<EaselComponent>(easel, out var comp))
            return false;

        if (!HasComp<CanvasComponent>(painting))
            return false;

        if (CompOrNull<CanvasComponent>(painting)?.Mounted == true)
            return false;

        if (comp.Container.ContainedEntities.Count >= EaselComponent.MaxPaintings)
        {
            _popup.PopupEntity(Loc.GetString("easel-full"), easel, user);
            return false;
        }

        if (!_containers.Insert(painting, comp.Container))
            return false;

        comp.ActiveIndex = comp.Container.ContainedEntities.Count - 1;
        Dirty(easel, comp);
        _popup.PopupEntity(Loc.GetString("easel-insert-success"), easel, user);
        UpdateUiState(easel, comp);
        UpdateAppearance(easel, comp);
        return true;
    }

    private void OpenUi(EntityUid uid, EntityUid user)
    {
        if (!_ui.HasUi(uid, EaselUiKey.Key))
            return;

        _ui.OpenUi(uid, EaselUiKey.Key, user);
        if (TryComp<EaselComponent>(uid, out var easel))
            UpdateUiState(uid, easel);
    }

    private void UpdateUiState(EntityUid uid, EaselComponent component)
    {
        if (!_ui.HasUi(uid, EaselUiKey.Key))
            return;

        var contained = component.Container.ContainedEntities;
        var slots = new NetEntity[contained.Count];
        var names = new string[contained.Count];

        for (var i = 0; i < contained.Count; i++)
        {
            slots[i] = GetNetEntity(contained[i]);
            var meta = MetaData(contained[i]);
            var title = TryComp<CanvasComponent>(contained[i], out var canvas) && !string.IsNullOrWhiteSpace(canvas.Title)
                ? canvas.Title
                : meta.EntityName;
            names[i] = title;
        }

        var active = contained.Count == 0 ? 0 : Math.Clamp(component.ActiveIndex, 0, contained.Count - 1);
        _ui.SetUiState(uid, EaselUiKey.Key, new EaselBoundUserInterfaceState(slots, active, names));
    }

    private void UpdateAppearance(EntityUid uid, EaselComponent component)
    {
        // Client visualizer reads ActiveIndex + container via component state / child canvas state.
        Dirty(uid, component);
    }
}
