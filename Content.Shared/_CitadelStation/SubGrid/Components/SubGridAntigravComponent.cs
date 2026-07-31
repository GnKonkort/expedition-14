using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Core machine on a sub-grid. While intact and powered on, the parent sub-grid can move.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SubGridAntigravComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled;

    [DataField, AutoNetworkedField]
    public SubGridMovementMode Mode = SubGridMovementMode.Fighter;

    /// <summary>Cycles mode on alt-activate / verb.</summary>
    [DataField]
    public List<SubGridMovementMode> ModeCycle = new()
    {
        SubGridMovementMode.Fighter,
        SubGridMovementMode.Car,
        SubGridMovementMode.Tracks,
        SubGridMovementMode.Hover,
        SubGridMovementMode.ArcHover,
    };
}