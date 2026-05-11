using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;
using Content.Shared._CitadelStation.HumanoidGenetics.Systems;
using Content.Shared._CitadelStation.HumanoidGenetics.Effects;
using Content.Shared._CitadelStation.HumanoidGenetics.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Humanoid.Markings;
using Content.Server.Humanoid;
using Content.Server.Popups;
using Content.Shared.Humanoid;
using Content.Server.EntityEffects;
using Robust.Shared.Timing;
using System.Linq;
using Content.Server.Polymorph.Systems;
using Content.Shared.Preferences;

public sealed class HumanoidGeneticsSystem : SharedHumanoidGeneticsSystem
{

    [Dependency] private readonly IPrototypeManager _proto = default!;

    [Dependency] private readonly EntityManager _entityManager = default!;

    [Dependency] private readonly IRobustRandom _robustRandom = default!;

    [Dependency] private readonly MarkingManager _markingManager = default!;

    [Dependency] private readonly HumanoidAppearanceSystem _humanoidAppearance = default!;

    [Dependency] private readonly PopupSystem _popup = default!;

    [Dependency] private readonly EntityEffectSystem _entityEffect = default!;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly PolymorphSystem _polymorph = default!;
    private readonly ISawmill _sawmill = Logger.GetSawmill("HumanoidGenetics");

    public override void Initialize()
    {
        base.Initialize();
        foreach (var prototype in _proto.EnumeratePrototypes<HumanoidMutationPrototype>())
        {
            _sawmill.Debug($"Found prototype {prototype.Name}");
        }

        SubscribeLocalEvent<ExecuteEntityEffectEvent<CauseHumanoidMutation>>(OnCauseHumanoidMutation);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var enumerator = _entityManager.EntityQueryEnumerator<HumanoidGeneContainerComponent>();

        while (enumerator.MoveNext(out var _uid, out var _geneContainer))
        {
            foreach (var mutation in _geneContainer.AppliedMutations)
            {
                if (mutation.LastUpdate + mutation.UpdateCooldown < _timing.CurTime)
                {
                    mutation.LastUpdate = _timing.CurTime;

                    if (!_proto.Resolve<HumanoidMutationPrototype>(mutation.MutationProto, out var mutationProto))
                    {
                        _sawmill.Error($"Tried to resolve prototype {mutationProto}, failed misreably");
                        continue;
                    }

                    foreach (var effect in mutationProto.Effects)
                    {
                        var baseargs = new EntityEffectBaseArgs(_uid, _entityManager);
                        effect.Effect(baseargs);
                    }
                }
            }
        }

    }

    public void OnCauseHumanoidMutation(ref ExecuteEntityEffectEvent<CauseHumanoidMutation> ev)
    {
        if (!_entityManager.HasComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity))
            return;

        var GeneContainerComponent = _entityManager.GetComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity);

        // Iterate over mutations
        foreach (var prototype in _proto.EnumeratePrototypes<HumanoidMutationPrototype>())
        {
            if (!_entityManager.HasComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity))
                continue;

            var mutation = new MutationClass();

            mutation.MutationProto = prototype;
            mutation.UpdateCooldown = TimeSpan.FromSeconds(1);

            if (_robustRandom.NextFloat() <= prototype.Chanсe)
            {
                var matches = GeneContainerComponent.AppliedMutations.Where(p => p.MutationProto == prototype);

                if (matches.Any())
                    continue;

                // Polymorhping should always be pirorityzed
                if (prototype.PolymorphEntity.Id != null)
                {
                    _polymorph.PolymorphEntity(ev.Args.TargetEntity, prototype.PolymorphEntity);
                }

                if (prototype.Race != string.Empty)
                {
                    var profile = HumanoidCharacterProfile.RandomWithSpecies(prototype.Race);

                    _humanoidAppearance.SetSpecies(ev.Args.TargetEntity, prototype.Race);
                    _humanoidAppearance.LoadProfile(ev.Args.TargetEntity, profile);
                }

                GeneContainerComponent.AppliedMutations.Add(mutation);

                _sawmill.Debug("invoking mutation");
                _popup.PopupEntity($"{Name(ev.Args.TargetEntity)}'s body change in a strange way...", ev.Args.TargetEntity);

                if (_entityManager.HasComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity))
                {
                    var appearanceComponent = _entityManager.GetComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity);

                    if (!_proto.Resolve(prototype.Marking, out var markingToApply))
                    {
                        _sawmill.Error($"Tried to resolve marking {prototype.Marking}, failed misreably");
                        continue;
                    }

                    _humanoidAppearance.AddMarking(ev.Args.TargetEntity, prototype.Marking, forced: true);
                }
            }
        }

    }
}
