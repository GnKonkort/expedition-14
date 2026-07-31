using System.Globalization;
using System.IO;
using System.Text;
using Content.Shared._CitadelStation.SubGrid.Systems;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>Server-side SubGrid diagnostics → <c>data/subgrid_debug_server.log</c>.</summary>
public sealed class SubGridDebugLog : SharedSubGridDebugLogSystem
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _path;

    protected override string SideTag => "SRV";
    public override string Path => _path ??= BuildPath();

    private static string BuildPath()
    {
        var dir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, "subgrid_debug_server.log");
    }

    public override void Initialize()
    {
        EnsureOpen();
        base.Initialize();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        lock (_lock)
        {
            _writer?.WriteLine("======== SubGrid debug session end {0:O} ========", DateTimeOffset.Now);
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureOpen()
    {
        lock (_lock)
        {
            if (_writer != null)
                return;

            _path ??= BuildPath();
            _writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
            _writer.WriteLine();
            _writer.WriteLine("======== SubGrid debug session side=SRV {0:O} ========", DateTimeOffset.Now);
            _writer.WriteLine("file={0}", _path);
        }
    }

    protected override void Emit(string line)
    {
        EnsureOpen();
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }
    }
}
