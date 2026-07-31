using Robust.Shared.GameStates;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Marks an entity as a valid boarding/exit point for a sub-grid (stairs, docked docking ports).
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SubGridAccessComponent : Component;
