using Content.Shared.DeviceLinking;
using Content.Shared.Examine;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._CitadelStation.Signboard;

public abstract class SharedSignboardSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private readonly SharedDeviceLinkSystem _deviceLink = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SignboardComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<SignboardComponent, ExaminedEvent>(OnExamined);
    }

    private void OnInit(EntityUid uid, SignboardComponent component, ComponentInit args)
    {
        if (!SignboardPixels.IsValidSize(component.Size))
            component.Size = SignboardComponent.DefaultSize;

        component.GridWidth = Math.Clamp(component.GridWidth, 1, SignboardComponent.MaxGrid);
        component.GridHeight = Math.Clamp(component.GridHeight, 1, SignboardComponent.MaxGrid);
        component.PanelX = Math.Clamp(component.PanelX, 0, component.GridWidth - 1);
        component.PanelY = Math.Clamp(component.PanelY, 0, component.GridHeight - 1);
        component.FrameDelay = Math.Clamp(component.FrameDelay, 0.05f, 10f);

        if (component.Master == null && component.Frames.Count == 0)
        {
            component.Frames.Add(SignboardPixels.CreateFrame("Frame 1", component.FullWidth, component.FullHeight));
            component.ActiveEditFrame = 0;
            component.PlaybackFrame = 0;
            Dirty(uid, component);
        }

        _deviceLink.EnsureSinkPorts(uid, component.OnPort, component.OffPort, component.NextFramePort);
    }

    private void OnExamined(EntityUid uid, SignboardComponent component, ExaminedEvent args)
    {
        var master = ResolveMaster((uid, component));
        if (master == null)
            return;

        var m = master.Value.Comp;
        args.PushMarkup(Loc.GetString("signboard-examine-size",
            ("size", m.Size),
            ("panels", m.PanelCount),
            ("width", m.GridWidth),
            ("height", m.GridHeight)));
        args.PushMarkup(Loc.GetString("signboard-examine-frames", ("count", m.Frames.Count)));
        args.PushMarkup(Loc.GetString(m.Powered ? "signboard-examine-powered" : "signboard-examine-unpowered"));
        if (m.Neon)
            args.PushMarkup(Loc.GetString("signboard-examine-neon"));
    }

    public Entity<SignboardComponent>? ResolveMaster(Entity<SignboardComponent> ent)
    {
        if (ent.Comp.Master == null || ent.Comp.Master == ent.Owner)
            return ent;

        if (!TryComp(ent.Comp.Master.Value, out SignboardComponent? master))
            return ent;

        return (ent.Comp.Master.Value, master);
    }

    public List<SignboardFrameData> CloneFrames(IReadOnlyList<SignboardFrameData> source)
    {
        var list = new List<SignboardFrameData>(source.Count);
        foreach (var frame in source)
        {
            list.Add(new SignboardFrameData
            {
                Name = frame.Name,
                PixelData = (byte[])frame.PixelData.Clone()
            });
        }

        return list;
    }

    public bool ValidateFrames(int size, int gridW, int gridH, List<SignboardFrameData> frames, out string? error)
    {
        error = null;
        if (!SignboardPixels.IsValidSize(size))
        {
            error = "bad-size";
            return false;
        }

        gridW = Math.Clamp(gridW, 1, SignboardComponent.MaxGrid);
        gridH = Math.Clamp(gridH, 1, SignboardComponent.MaxGrid);
        if (frames.Count is < 1 or > SignboardComponent.MaxFrames)
        {
            error = "bad-frame-count";
            return false;
        }

        var expected = SignboardPixels.ByteLength(size * gridW, size * gridH);
        foreach (var frame in frames)
        {
            if (frame.PixelData.Length > expected * 2)
            {
                error = "bad-payload";
                return false;
            }

            if (!SignboardPixels.TryDecompress(frame.PixelData, expected, out _))
            {
                error = "bad-decompress";
                return false;
            }

            if (string.IsNullOrWhiteSpace(frame.Name))
                frame.Name = "Frame";
            else if (frame.Name.Length > 32)
                frame.Name = frame.Name[..32];
        }

        return true;
    }

    public bool TryResizeFrames(SignboardComponent component, int newSize, int newGridW, int newGridH)
    {
        newGridW = Math.Clamp(newGridW, 1, SignboardComponent.MaxGrid);
        newGridH = Math.Clamp(newGridH, 1, SignboardComponent.MaxGrid);
        if (!SignboardPixels.IsValidSize(newSize))
            return false;

        var oldW = component.FullWidth;
        var oldH = component.FullHeight;
        var newW = newSize * newGridW;
        var newH = newSize * newGridH;

        if (oldW == newW && oldH == newH)
        {
            component.Size = newSize;
            component.GridWidth = newGridW;
            component.GridHeight = newGridH;
            return true;
        }

        var oldLen = SignboardPixels.ByteLength(oldW, oldH);
        foreach (var frame in component.Frames)
        {
            if (!SignboardPixels.TryDecompress(frame.PixelData, oldLen, out var rgba))
                rgba = SignboardPixels.CreateBlank(oldW, oldH, opaqueBlack: true);

            frame.PixelData = SignboardPixels.Compress(SignboardPixels.Resize(rgba, oldW, oldH, newW, newH));
        }

        component.Size = newSize;
        component.GridWidth = newGridW;
        component.GridHeight = newGridH;
        component.PlaybackFrame = Math.Clamp(component.PlaybackFrame, 0, Math.Max(0, component.Frames.Count - 1));
        component.ActiveEditFrame = Math.Clamp(component.ActiveEditFrame, 0, Math.Max(0, component.Frames.Count - 1));
        return true;
    }

    public bool TryGetFrameStyle(ProtoId<SignboardFramePrototype>? id, out SignboardFramePrototype proto)
    {
        proto = default!;
        if (id == null)
            return false;
        return _prototype.TryIndex(id.Value, out proto!);
    }
}
