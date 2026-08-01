using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Marks an entity as a valid boarding/exit point for a sub-grid (stairs, docked docking ports).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SubGridAccessComponent : Component
{
    /// <summary>
    /// Grid that owns this access point. For SubGrid stairs this is the SubGrid entity;
    /// used to reseat stairs if transform drifts onto the host underneath.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid OwnerGrid;

    /// <summary>Tile on <see cref="OwnerGrid"/> (SubGrid-local indices).</summary>
    [DataField, AutoNetworkedField]
    public Vector2i Tile;
}
