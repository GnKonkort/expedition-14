using Robust.Shared.Timing;

namespace Content.Shared._CitadelStation.SubGrid.Systems;

/// <summary>
/// Side-specific SubGrid diagnostics sink (file on server, sawmill on client).
/// No System.IO here — client sandbox forbids it.
/// </summary>
public abstract class SharedSubGridDebugLogSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Dictionary<string, (TimeSpan Next, int Suppressed)> _throttle = new();

    protected abstract string SideTag { get; }

    /// <summary>Human-readable sink description (file path or sawmill name).</summary>
    public abstract string Path { get; }

    protected IGameTiming Timing => _timing;

    public override void Initialize()
    {
        base.Initialize();
        Log.Info("SubGridDebugLog ({Side}) -> {Path}", SideTag, Path);
    }

    /// <summary>Always emit a line (board/exit/collide).</summary>
    public void Write(string category, string message)
    {
        Emit(FormatLine(category, message));
    }

    /// <summary>Rate-limited emit; dumps suppressed count on next emit.</summary>
    public void WriteThrottle(string key, TimeSpan interval, string category, string message)
    {
        var now = _timing.CurTime;
        if (_throttle.TryGetValue(key, out var state) && now < state.Next)
        {
            _throttle[key] = (state.Next, state.Suppressed + 1);
            return;
        }

        var suppressed = state.Suppressed;
        _throttle[key] = (now + interval, 0);

        if (suppressed > 0)
            message = message + " (+" + suppressed + " suppressed)";

        Emit(FormatLine(category, message));
    }

    private string FormatLine(string category, string message)
    {
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0:hh\\:mm\\:ss\\.fff} t={1} side={2} pred={3}/{4} [{5}] {6}",
            _timing.CurTime,
            _timing.CurTick.Value,
            SideTag,
            _timing.IsFirstTimePredicted ? "1st" : "replay",
            _timing.InPrediction ? "inPred" : "real",
            category,
            message);
    }

    protected abstract void Emit(string line);
}
