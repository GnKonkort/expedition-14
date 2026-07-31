using Robust.Shared.Prototypes;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Station machine that spends sheet stacks in item slots to spawn a starter 3x3 sub-grid pad with antigrav.
/// </summary>
[RegisterComponent]
public sealed partial class SubGridPadComponent : Component
{
    [DataField]
    public string SteelSlotId = "steel_slot";

    [DataField]
    public string UraniumSlotId = "uranium_slot";

    /// <summary>Test recipe: 5 steel sheets.</summary>
    [DataField]
    public int SteelSheetsRequired = 5;

    /// <summary>Test recipe: 5 uranium sheets.</summary>
    [DataField]
    public int UraniumSheetsRequired = 5;

    [DataField]
    public string SteelStackType = "Steel";

    [DataField]
    public string UraniumStackType = "Uranium";

    /// <summary>How many tiles in front of the pad to place the grid center.</summary>
    [DataField]
    public float SpawnOffsetTiles = 2.5f;

    [DataField]
    public int GridSize = 3;

    [DataField]
    public EntProtoId AntigravPrototype = "SubGridAntigrav";

    [DataField]
    public EntProtoId ConsolePrototype = "SubGridConsole";

    [DataField]
    public EntProtoId ThrusterPrototype = "SubGridThruster";

    [DataField]
    public EntProtoId GyroscopePrototype = "SubGridGyroscope";

    [DataField]
    public string FloorTileId = "Plating";

    /// <summary>Minimum dot product between pad facing and vector-to-user (front arc).</summary>
    [DataField]
    public float FrontDotMin = 0.25f;
}
