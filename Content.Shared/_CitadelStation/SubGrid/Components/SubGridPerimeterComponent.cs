using Robust.Shared.GameStates;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Per-tile invisible boarding barrier on a SubGrid.
/// Blocks outsiders from walking onto the tile; disabled on stairs/dock tiles;
/// ignored by anyone already on this SubGrid.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SubGridPerimeterComponent : Component
{
    /// <summary>Tile this barrier covers on its parent SubGrid.</summary>
    [DataField, AutoNetworkedField]
    public Vector2i Tile;

    /// <summary>
    /// SubGrid entity that owns this barrier. Stored explicitly because transform GridUid
    /// can briefly disagree while the hull moves / reparents.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid OwnerGrid;
}
