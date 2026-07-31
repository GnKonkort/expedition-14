using Content.Shared._CitadelStation.SubGrid.Systems;
using Robust.Shared.Log;

namespace Content.Client._CitadelStation.SubGrid;

/// <summary>
/// Client SubGrid diagnostics via sawmill (sandbox forbids System.IO file writes).
/// Look for logger <c>subgrid</c> in client console / log.
/// </summary>
public sealed class SubGridDebugLog : SharedSubGridDebugLogSystem
{
    private ISawmill _saw = default!;

    protected override string SideTag => "CLI";
    public override string Path => "sawmill:subgrid";

    public override void Initialize()
    {
        _saw = Logger.GetSawmill("subgrid");
        base.Initialize();
    }

    protected override void Emit(string line)
    {
        _saw.Info(line);
    }
}
