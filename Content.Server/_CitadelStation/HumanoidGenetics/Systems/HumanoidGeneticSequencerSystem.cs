using Content.Shared._CitadelStation.HumanoidGenetics.Systems;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;
using Content.Shared.Interaction;
using Content.Shared.DoAfter;
using Content.Shared._CitadelStation.HumanoidGenetics.Events;
using Robust.Server.GameObjects;
using Content.Shared._CitadelStation.HumanoidGenetics.UIKeys;
using Robust.Shared.Player;

namespace Content.Server._CitadelStation.HumanoidGenetics.Systems;


public sealed class HumanoidGeneticSequencerSystem : SharedHumanoidGeneticSequencerSystem
{
    [Dependency] private readonly EntityManager _entityManager = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;

    private ISawmill _sawmill = default!;
    public override void Initialize()
    {
        base.Initialize();

        _sawmill = Logger.GetSawmill("GeneSeq");

        SubscribeLocalEvent<HumanoidGeneSequencerComponent, AfterInteractEvent>(OnScanEntity);
        SubscribeLocalEvent<HumanoidGeneSequencerComponent, GeneticSequencerScanDoAfterEvent>(OnDoAfter);
    }

    private void OnScanEntity(Entity<HumanoidGeneSequencerComponent> ent, ref AfterInteractEvent args)
    {
        if (!args.CanReach)
            return;

        if (args.Handled)
            return;

        if(!_entityManager.HasComponent<HumanoidGeneContainerComponent>(args.Target))
            return;

        var doAfterArgs = new DoAfterArgs(
            _entityManager,
            args.User,
            1.0f,
            new GeneticSequencerScanDoAfterEvent(),
            ent,
            target: args.Target,
            used: ent
        );

        _sawmill.Debug($"Launching doafter event");
        _doAfterSystem.TryStartDoAfter(doAfterArgs);

        args.Handled = true;

    }

    private void OnDoAfter(Entity<HumanoidGeneSequencerComponent> ent, ref GeneticSequencerScanDoAfterEvent args)
    {
        _sawmill.Debug($"{Name(args.User)} interacts with ${Name(ent.Owner)}");
        if (!_uiSystem.HasUi(ent, HumanoidGeneticSequencerUiKey.Key))
        {
            _sawmill.Debug($"{Name(args.User)} failed to retrieve UI key");
            return;
        }
        if (!_uiSystem.TryOpenUi(ent.Owner, HumanoidGeneticSequencerUiKey.Key, args.User))
        {
            _sawmill.Debug($"Failed to open ui with {Name(ent.Owner)} for {Name(args.User)}");
        }
    }
}
