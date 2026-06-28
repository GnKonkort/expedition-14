using Content.Shared.Climbing.Systems;
using Content.Shared.Emag.Systems;
using Content.Shared.Popups;
using Robust.Shared.Containers;

namespace Content.Shared._CitadelStation.HumanoidGenetics.Systems;

public abstract class SharedGeneticsPodSystem : EntitySystem {
    [Dependency] private readonly SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly EmagSystem _emag = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

    public override void Initialize() {
        base.Initialize();
    }
}
