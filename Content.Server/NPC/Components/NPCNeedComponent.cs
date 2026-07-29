namespace Content.Server.NPC.Components;

/// <summary>
/// TTL cache for cheap Need Board flags refreshed by <see cref="Systems.NPCNeedSystem"/>.
/// </summary>
[RegisterComponent]
public sealed partial class NPCNeedComponent : Component
{
    [ViewVariables]
    public TimeSpan NextRefresh = TimeSpan.Zero;

    [ViewVariables]
    public bool InVacuum;

    [ViewVariables]
    public bool LowPressure;

    [ViewVariables]
    public bool HasHostile;

    [ViewVariables]
    public bool AmmoCritical;

    [ViewVariables]
    public bool MedStockLow;

    [ViewVariables]
    public bool SeekAtmosphere;

    [ViewVariables]
    public bool DoorBlocked;
}
