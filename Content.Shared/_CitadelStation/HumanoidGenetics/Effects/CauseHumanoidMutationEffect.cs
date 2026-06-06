using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Effects;

public sealed partial class CauseHumanoidMutation : EventEntityEffect<CauseHumanoidMutation> {
    protected override string ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
    {
        return "Causes mutation in organic matterdwd";
    }
}
