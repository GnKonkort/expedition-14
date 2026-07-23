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
using Content.Shared.Polymorph;
using Robust.Shared.Timing;
using System.Linq;
using Content.Server.Polymorph.Systems;
using Content.Shared.Preferences;
using Robust.Shared.Serialization.Manager;
using Content.Server.MassMedia.Components;
using Content.Server.Polymorph.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Server.Chemistry.EntitySystems;
using Content.Shared.Chemistry.EntitySystems;

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
    [Dependency] private readonly IComponentFactory _compFact = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainer = default!;
    private readonly ISawmill _sawmill = Logger.GetSawmill("HumanoidGenetics");

    public override void Initialize()
    {
        base.Initialize();
        foreach (var prototype in _proto.EnumeratePrototypes<HumanoidMutationPrototype>())
        {
            _sawmill.Debug($"Found prototype {prototype.Name}");
        }

        SubscribeLocalEvent<ExecuteEntityEffectEvent<CauseHumanoidMutation>>(OnCauseHumanoidMutation);
        SubscribeLocalEvent<ExecuteEntityEffectEvent<CauseHumanoidMutationReversion>>(OnCauseHumanoidMutationReversion);
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



        // Iterate over mutations
        foreach (var prototype in _proto.EnumeratePrototypes<HumanoidMutationPrototype>())
        {
            if (!_entityManager.HasComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity))
                continue;

            var GeneContainerComponent = _entityManager.GetComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity);

            var mutation = new MutationClass();

            mutation.MutationProto = prototype;
            mutation.UpdateCooldown = TimeSpan.FromSeconds(1);

            if (_robustRandom.NextFloat() <= prototype.Chanсe)
            {
                var matches = GeneContainerComponent.AppliedMutations.Where(p => p.MutationProto == prototype);

                if (matches.Any()) {
                    _sawmill.Debug("Attempted to add existing mutation. Skipping");
                    continue;
                }
                _sawmill.Debug("invoking mutation");




                if (_entityManager.HasComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity))
                {
                    var appearanceComponent = _entityManager.GetComponent<HumanoidAppearanceComponent>(ev.Args.TargetEntity);

                    if (!_proto.Resolve(prototype.Marking, out var markingToApply))
                    {
                        _sawmill.Error($"Tried to resolve marking {prototype.Marking}, failed misreably");
                        continue;
                    }
                }

                _sawmill.Debug($"Resolved prototype: {prototype.PolymorphEntity.Id}");

                if (_entityManager.HasComponent<PolymorphedEntityComponent>(ev.Args.TargetEntity) && prototype.PolymorphEntity.Id != null) {
                    _sawmill.Debug("SKIPPING FUCKING MUTATION!");
                    continue;
                }

                _humanoidAppearance.AddMarking(ev.Args.TargetEntity, prototype.Marking, forced: true);
                GeneContainerComponent.AppliedMutations.Add(mutation);
                _sawmill.Debug($"Successfully added mutation {mutation}");
                _popup.PopupEntity($"{Name(ev.Args.TargetEntity)}'s body change in a strange way...", ev.Args.TargetEntity);

                if (prototype.PolymorphEntity.Id != null && !_entityManager.HasComponent<PolymorphedEntityComponent>(ev.Args.TargetEntity))
                {
                    var config = new PolymorphConfiguration
                    {
                        Entity = (EntProtoId)prototype.PolymorphEntity.Id,
                        TransferDamage = true,
                        Forced = true,
                        Inventory = PolymorphInventoryChange.Transfer,
                        RevertOnCrit = false,
                        RevertOnDeath = false,
                        AllowRepeatedMorphs = true
                    };

                    var newUid_ = _polymorph.PolymorphEntity(ev.Args.TargetEntity, config);
                    if (newUid_ is not EntityUid newUid)
                    {
                        _sawmill.Debug($"Tried to polymorph entity {Name(ev.Args.TargetEntity)} ({ev.Args.TargetEntity}) into {prototype.PolymorphEntity.Id} but failed miserably!");
                        return;
                    }

                    _metaData.SetEntityName(newUid, Name(ev.Args.TargetEntity));

                    // Randomize appearance
                    var profile = HumanoidCharacterProfile.RandomWithSpecies(_entityManager.GetComponent<HumanoidAppearanceComponent>((EntityUid)newUid).Species);
                    _humanoidAppearance.LoadProfile(newUid, profile);

                    // We need to transfer and update Gene Container

                    _entityManager.TryGetComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity, out var geneticComp);
                    if (geneticComp == null)
                    {
                        _sawmill.Debug($"Tried to get component of type {typeof(HumanoidGeneContainerComponent)} from entity {Name(ev.Args.TargetEntity)} ({ev.Args.TargetEntity}) but failed miserably!");
                        return;
                    }

                    var newGeneticsComp = _compFact.GetComponent<HumanoidGeneContainerComponent>();
                    newGeneticsComp.AppliedMutations = geneticComp.AppliedMutations;
                    _entityManager.AddComponent(uid: (EntityUid)newUid, component: (Component)newGeneticsComp!, overwrite: true);

                    // We also need to re-apply every marking
                    foreach (var geneMarking in newGeneticsComp.AppliedMutations)
                    {
                        if (!_proto.Resolve(geneMarking.MutationProto, out var mutationProtoToApply))
                        {
                            _sawmill.Error($"Tried to resolve marking {geneMarking.MutationProto}, failed misreably");
                            continue;
                        }

                        _humanoidAppearance.AddMarking((EntityUid)newUid, mutationProtoToApply.Marking, forced: true);
                    }

                    if (_entityManager.TryGetComponent<SolutionContainerManagerComponent>(ev.Args.TargetEntity, out var oldSolutionManager) && _entityManager.TryGetComponent<SolutionContainerManagerComponent>(newUid, out var newSolutionManager))
                    {

                        foreach (var (name, soln) in _solutionContainer.EnumerateSolutions((ev.Args.TargetEntity, oldSolutionManager)))
                        {
                            var solution = soln.Comp.Solution;
                            if (!_solutionContainer.TryGetSolution(((EntityUid)newUid, newSolutionManager), name, out var entity, out var newSolution))
                                continue;
                            _solutionContainer.AddSolution((Entity<Content.Shared.Chemistry.Components.SolutionComponent>)entity, solution);
                            _solutionContainer.RemoveAllSolution(soln);
                        }
                    }
                    ev.Args.TargetEntity = newUid;
                }
            }
        }
    }

    public void OnCauseHumanoidMutationReversion(ref ExecuteEntityEffectEvent<CauseHumanoidMutationReversion> ev)
    {
        if (!_entityManager.HasComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity))
            return;

        var geneContainer = _entityManager.GetComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity);

        if (geneContainer.AppliedMutations.Count == 0)
            return;

        var mutationToRemove = _robustRandom.PickAndTake<MutationClass>(geneContainer.AppliedMutations);

        if (!_proto.Resolve(mutationToRemove.MutationProto, out var mutation))
        {
            _sawmill.Debug($"Tried to resolve mutation {mutationToRemove.MutationProto} while removing it from {ev.Args.TargetEntity} but failed misreably");
            return;
        }

        _humanoidAppearance.RemoveMarking(ev.Args.TargetEntity, mutation.Marking, true);

        // Was transformed by mutation, transforming back
        if (_entityManager.HasComponent<PolymorphedEntityComponent>(ev.Args.TargetEntity) && mutation.PolymorphEntity.Id != null)
        {
            var newUid = _polymorph.Revert(ev.Args.TargetEntity);
            if (newUid == null)
            {
                _sawmill.Debug($"Tried to revert polymorping from mutation {mutation.Name} for entity {ev.Args.TargetEntity} but failed miserably!");
                return;
            }
            if (!_entityManager.TryGetComponent<HumanoidGeneContainerComponent>(ev.Args.TargetEntity, out var geneticComp) ||
                !_entityManager.TryGetComponent<HumanoidGeneContainerComponent>(newUid, out var newGeneticComp))
            {
                _sawmill.Debug($"Attempted to get genetic components for previous and current entity, but failed miserably");
                return;
            }

            // Remove all markings caused by mutations and apply new ones
            foreach (var geneMarking in newGeneticComp.AppliedMutations)
            {
                if (!_proto.Resolve(geneMarking.MutationProto, out var mutationProtoToRemove))
                {
                    _sawmill.Error($"Tried to resolve marking {geneMarking.MutationProto}, failed misreably");
                    continue;
                }
                _humanoidAppearance.RemoveMarking((EntityUid)newUid, mutationProtoToRemove.Marking);
            }
            foreach (var geneMarking in geneticComp.AppliedMutations)
            {
                if (!_proto.Resolve(geneMarking.MutationProto, out var mutationProtoToRemove))
                {
                    _sawmill.Error($"Tried to resolve marking {geneMarking.MutationProto}, failed misreably");
                    continue;
                }
                _humanoidAppearance.AddMarking(ev.Args.TargetEntity, mutationProtoToRemove.Marking);
            }

            newGeneticComp.AppliedMutations = geneticComp.AppliedMutations;

            _entityManager.AddComponent((EntityUid)newUid, newGeneticComp, true);

            if (_entityManager.TryGetComponent<SolutionContainerManagerComponent>(ev.Args.TargetEntity, out var oldSolutionManager) && _entityManager.TryGetComponent<SolutionContainerManagerComponent>(newUid, out var newSolutionManager))
            {
                _sawmill.Debug("AAAAAAAAAAAAAAAAAAA BLYAT!!!!");
                foreach (var (name, soln) in _solutionContainer.EnumerateSolutions((ev.Args.TargetEntity, oldSolutionManager)))
                {
                    var solution = soln.Comp.Solution;
                    if (!_solutionContainer.TryGetSolution(((EntityUid)newUid, newSolutionManager), name, out var entity, out var newSolution))
                        continue;
                    _solutionContainer.AddSolution((Entity<Content.Shared.Chemistry.Components.SolutionComponent>)entity, solution);
                    _solutionContainer.RemoveAllSolution(soln);
                }
            }
        }
    }
}
