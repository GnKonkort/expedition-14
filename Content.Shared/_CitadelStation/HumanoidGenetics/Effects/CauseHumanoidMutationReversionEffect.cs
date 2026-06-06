using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;
using Content.Shared._CitadelStation.HumanoidGenetics.Components;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Effects;

public sealed partial class CauseHumanoidMutationReversion : EventEntityEffect<CauseHumanoidMutationReversion> {
    protected override string ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
    {
        return "Reverses mutations in organic matter";
    }
}
