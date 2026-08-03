using Content.Shared.Interaction;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;

namespace Content.Shared._CitadelStation.Painting;

/// <summary>
/// Floor easel that holds up to <see cref="MaxPaintings"/> canvases and displays one at a time.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class EaselComponent : Component
{
    public const int MaxPaintings = 9;
    public const string ContainerId = "easel_paintings";

    [DataField, AutoNetworkedField]
    public int ActiveIndex;

    [ViewVariables]
    public Container Container = default!;
}

/// <summary>
/// Temporary marker on a player editing a canvas that is stored inside an easel.
/// Makes BUI range/accessibility checks use the easel instead of the contained canvas.
/// </summary>
[RegisterComponent]
public sealed partial class EaselPaintingEditSessionComponent : Component
{
    public EntityUid Easel;
    public EntityUid Canvas;
}

public abstract class SharedEaselSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EaselComponent, ComponentInit>(OnInit);

        SubscribeLocalEvent<EaselPaintingEditSessionComponent, AccessibleOverrideEvent>(OnSessionAccessible);
        SubscribeLocalEvent<EaselPaintingEditSessionComponent, InRangeOverrideEvent>(OnSessionInRange);

        // Keep BUI open while the player stays near the easel (after InteractionSystem's Fail).
        SubscribeLocalEvent<BoundUserInterfaceCheckRangeEvent>(OnCanvasUiRange, after: [typeof(SharedInteractionSystem)]);
    }

    private void OnInit(EntityUid uid, EaselComponent component, ComponentInit args)
    {
        component.Container = _container.EnsureContainer<Container>(uid, EaselComponent.ContainerId);
    }

    /// <summary>
    /// Marks <paramref name="user"/> as editing <paramref name="canvas"/> through <paramref name="easel"/>.
    /// </summary>
    public void BeginEditSession(EntityUid user, EntityUid easel, EntityUid canvas)
    {
        var session = EnsureComp<EaselPaintingEditSessionComponent>(user);
        session.Easel = easel;
        session.Canvas = canvas;
    }

    public void EndEditSession(EntityUid user, EntityUid? canvas = null)
    {
        if (!TryComp(user, out EaselPaintingEditSessionComponent? session))
            return;

        if (canvas != null && session.Canvas != canvas)
            return;

        RemCompDeferred<EaselPaintingEditSessionComponent>(user);
    }

    private void OnSessionAccessible(Entity<EaselPaintingEditSessionComponent> ent, ref AccessibleOverrideEvent args)
    {
        if (args.Target != ent.Comp.Canvas && args.Target != ent.Comp.Easel)
            return;

        if (!IsCanvasOnEasel(args.Target == ent.Comp.Canvas ? args.Target : ent.Comp.Canvas, ent.Comp.Easel))
            return;

        args.Handled = true;
        args.Accessible = _interaction.InRangeUnobstructed(args.User, ent.Comp.Easel);
    }

    private void OnSessionInRange(Entity<EaselPaintingEditSessionComponent> ent, ref InRangeOverrideEvent args)
    {
        if (args.Target != ent.Comp.Canvas)
            return;

        if (!IsCanvasOnEasel(ent.Comp.Canvas, ent.Comp.Easel))
            return;

        args.Handled = true;
        args.InRange = _interaction.InRangeUnobstructed(args.User, ent.Comp.Easel);
    }

    private void OnCanvasUiRange(ref BoundUserInterfaceCheckRangeEvent args)
    {
        if (args.Result == BoundUserInterfaceRangeResult.Pass)
            return;

        if (!HasComp<CanvasComponent>(args.Target))
            return;

        if (!_container.TryGetContainingContainer(args.Target, out var container)
            || container.ID != EaselComponent.ContainerId)
            return;

        var actor = args.Actor.Owner;
        if (_interaction.InRangeUnobstructed(actor, container.Owner, args.Data.InteractionRange))
            args.Result = BoundUserInterfaceRangeResult.Pass;
    }

    private bool IsCanvasOnEasel(EntityUid canvas, EntityUid easel)
    {
        return _container.TryGetContainingContainer(canvas, out var container)
               && container.ID == EaselComponent.ContainerId
               && container.Owner == easel;
    }

    public EntityUid? GetActivePainting(Entity<EaselComponent> easel)
    {
        var contained = easel.Comp.Container.ContainedEntities;
        if (contained.Count == 0)
            return null;

        var idx = Math.Clamp(easel.Comp.ActiveIndex, 0, contained.Count - 1);
        return contained[idx];
    }
}
