using Content.Shared._CitadelStation.HumanoidGenetics.Systems;
using Robust.Server.GameObjects;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;
using Content.Shared.DragDrop;
using Content.Shared.DoAfter;
using Content.Shared._CitadelStation.HumanoidGenetics.Events;
using Content.Server.DoAfter;
using Robust.Server.Containers;
using Robust.Shared.Containers;

namespace Content.server._CitadelStation.HumanoidGenetics.Systems;
public sealed class GeneticsPodSystem : SharedGeneticsPodSystem {
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly DoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly ContainerSystem _containerSystem = default!;

    private ISawmill _saw = default!;
    public override void Initialize() {
        base.Initialize();
        SubscribeLocalEvent<GeneticPodComponent, DragDropTargetEvent>(OnDragDropTargetEvent);
        SubscribeLocalEvent<GeneticPodComponent, GeneticPodDragIntoEvent>(OnDragIntoFinished);
        SubscribeLocalEvent<GeneticPodComponent, ComponentStartup>(OnInit);
        _saw = Logger.GetSawmill("genetic_server");
        _saw.Debug("Successfully started genetic pod system");
    }

    private void OnInit(EntityUid uid, GeneticPodComponent component, ComponentStartup args)
    {
        component.BodyContainer = _containerSystem.EnsureContainer<ContainerSlot>(uid, "body_container");
    }

    private void OnDragIntoFinished(Entity<GeneticPodComponent> ent, ref GeneticPodDragIntoEvent args)
    {
        _saw.Debug($"Inserting {Name(ent)}");

        if (ent.Comp.BodyContainer.ContainedEntity != null) {
            _saw.Error($"Attempted to insert entity {args.Target} but failed. Someone already inside");
            return;
        }

        if (!HasComp<HumanoidGeneContainerComponent>(args.Target)) {
            _saw.Error($"Attempted to insert entity {args.Target} but failed. GeneContainerComponent missing");
            return;
        }

        if (args.Target is not EntityUid target) {
            _saw.Error($"Attempted to insert entity {args.Target} but failed. NULL reference");
            return;
        }
        var transform = Transform(target);
        _containerSystem.Insert((target, transform), ent.Comp.BodyContainer);
    }

    private void OnDragDropTargetEvent(Entity<GeneticPodComponent> ent, ref DragDropTargetEvent args)
    {
        if (!HasComp<HumanoidGeneContainerComponent>(args.Dragged))
            return;

        if (ent.Comp.BodyContainer.Count != 0)
            return;

        var doafter = new DoAfterArgs(
            EntityManager,
            args.User,
            ent.Comp.EntryDelay,
            new GeneticPodDragIntoEvent(),
            ent.Owner,
            target: args.Dragged,
            used: ent.Owner
        );

        _doAfterSystem.TryStartDoAfter(doafter);

        args.Handled = true;
    }
}
