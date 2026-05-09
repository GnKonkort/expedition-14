using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Shared.Prototypes;
using Content.Shared._CitadelStation.HumanoidGenetics.Effects;
using Content.Shared.EntityEffects;
using Content.Shared.Popups;


namespace Content.Shared._CitadelStation.HumanoidGenetics.Systems;

public abstract class SharedHumanoidGeneticsSystem : EntitySystem {
    [Dependency] private readonly SharedHumanoidAppearanceSystem _sharedHumanoidAppearanceSystem = default!;

    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly MarkingManager _markingManaged = default!;

    public override void Initialize() {
        base.Initialize();
        //SubscribeLocalEvent<ExecuteEntityEffectEvent<CauseHumanoidMutation>>(OnCauseHumanoidMutation);
    }

    /*public virtual void OnCauseHumanoidMutation(ExecuteEntityEffectEvent<CauseHumanoidMutation> ev) {

    }*/
}
