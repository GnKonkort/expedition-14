using System.Numerics;
using Content.Shared._CitadelStation.Signboard;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.Client._CitadelStation.Signboard.UI;

public enum SignboardTool : byte
{
    Brush,
    Eraser,
    Fill,
    Eyedropper,
    Text
}

/// <summary>
/// Wide/tall signboard editor surface. Supports onion-skin of the previous frame.
/// </summary>
public sealed class SignboardDrawControl : Control
{
    private int _panelSize = SignboardComponent.DefaultSize;
    private int _gridWidth = 1;
    private int _gridHeight = 1;
    private readonly List<(string Name, byte[] Pixels)> _frames = [];
    private int _activeFrame;
    private byte[]? _onionPixels;
    private bool _drawing;
    private Vector2i? _lastPixel;

    public Color BrushColor { get; set; } = Color.White;
    public int BrushSize { get; set; } = 1;
    public SignboardTool Tool { get; set; } = SignboardTool.Brush;
    public bool ReadOnly { get; set; }
    public bool OnionSkin { get; set; } = true;
    public float OnionOpacity { get; set; } = 0.4f;

    /// <summary>1 = EditorDisplayHeight per panel side.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>Text prepared for click-to-place stamping.</summary>
    public string? PendingText { get; set; }
    public int TextFontStyle { get; set; }
    public int TextScale { get; set; } = 1;

    public event Action? PixelsModified;
    public event Action<Color>? ColorPicked;
    public event Action? TextPlaced;

    public int PanelSize => _panelSize;
    public int GridWidth => _gridWidth;
    public int GridHeight => _gridHeight;
    public int FullWidth => _panelSize * _gridWidth;
    public new int PixelHeight => _panelSize * _gridHeight;
    public int ActiveFrame => _activeFrame;
    public int FrameCount => _frames.Count;

    private Vector2 DisplaySize
    {
        get
        {
            var cell = CellSize;
            return new Vector2(FullWidth * cell, PixelHeight * cell);
        }
    }

    private float CellSize => SignboardComponent.EditorDisplayHeight * Zoom / Math.Max(1, _panelSize);

    public SignboardDrawControl()
    {
        MouseFilter = MouseFilterMode.Stop;
        EnsureDefaultFrame();
        UpdateMinSize();
    }

    public void SetZoom(float zoom)
    {
        Zoom = Math.Clamp(zoom, 0.25f, 2f);
        UpdateMinSize();
    }

    public void LoadFromState(int size, int gridWidth, int gridHeight, List<SignboardFrameData> frames, int activeFrame)
    {
        _panelSize = size;
        _gridWidth = Math.Clamp(gridWidth, 1, SignboardComponent.MaxGrid);
        _gridHeight = Math.Clamp(gridHeight, 1, SignboardComponent.MaxGrid);
        _frames.Clear();
        var expected = SignboardPixels.ByteLength(FullWidth, PixelHeight);

        foreach (var frame in frames)
        {
            if (!SignboardPixels.TryDecompress(frame.PixelData, expected, out var rgba))
                rgba = SignboardPixels.CreateBlank(FullWidth, PixelHeight, opaqueBlack: true);
            _frames.Add((frame.Name, rgba));
        }

        if (_frames.Count == 0)
            EnsureDefaultFrame();

        _activeFrame = Math.Clamp(activeFrame, 0, _frames.Count - 1);
        RefreshOnion();
        UpdateMinSize();
    }

    public List<SignboardFrameData> ExportFrames()
    {
        var list = new List<SignboardFrameData>(_frames.Count);
        foreach (var (name, pixels) in _frames)
        {
            list.Add(new SignboardFrameData
            {
                Name = name,
                PixelData = SignboardPixels.Compress(pixels)
            });
        }

        return list;
    }

    public IReadOnlyList<string> GetFrameNames()
    {
        var list = new List<string>(_frames.Count);
        foreach (var (name, _) in _frames)
            list.Add(name);
        return list;
    }

    public void SetActiveFrame(int index)
    {
        if (index < 0 || index >= _frames.Count)
            return;
        _activeFrame = index;
        RefreshOnion();
    }

    public bool TryAddFrame(string name, bool copyPrevious)
    {
        if (_frames.Count >= SignboardComponent.MaxFrames)
            return false;

        byte[] pixels;
        if (copyPrevious && _frames.Count > 0)
            pixels = (byte[])_frames[_activeFrame].Pixels.Clone();
        else
            pixels = SignboardPixels.CreateBlank(FullWidth, PixelHeight, opaqueBlack: true);

        _frames.Add((name, pixels));
        _activeFrame = _frames.Count - 1;
        RefreshOnion();
        PixelsModified?.Invoke();
        return true;
    }

    public bool TryRemoveActiveFrame()
    {
        if (_frames.Count <= 1)
            return false;

        _frames.RemoveAt(_activeFrame);
        _activeFrame = Math.Clamp(_activeFrame, 0, _frames.Count - 1);
        RefreshOnion();
        PixelsModified?.Invoke();
        return true;
    }

    public void CopyFromPrevious()
    {
        if (_activeFrame <= 0 || ReadOnly)
            return;

        var prev = _frames[_activeFrame - 1].Pixels;
        var cur = _frames[_activeFrame];
        _frames[_activeFrame] = (cur.Name, (byte[])prev.Clone());
        RefreshOnion();
        PixelsModified?.Invoke();
    }

    public void StampPixels(byte[] rgba)
    {
        if (ReadOnly || rgba.Length != SignboardPixels.ByteLength(FullWidth, PixelHeight))
            return;

        var cur = _frames[_activeFrame];
        _frames[_activeFrame] = (cur.Name, rgba);
        PixelsModified?.Invoke();
    }

    public byte[] GetActivePixelsCopy() => (byte[])_frames[_activeFrame].Pixels.Clone();

    private void RefreshOnion()
    {
        if (_activeFrame <= 0)
        {
            _onionPixels = null;
            return;
        }

        _onionPixels = _frames[_activeFrame - 1].Pixels;
    }

    private void EnsureDefaultFrame()
    {
        _frames.Clear();
        _frames.Add(("Frame 1", SignboardPixels.CreateBlank(FullWidth, PixelHeight, opaqueBlack: true)));
        _activeFrame = 0;
        _onionPixels = null;
    }

    private void UpdateMinSize()
    {
        MinSize = DisplaySize;
        MaxSize = DisplaySize;
        SetSize = DisplaySize;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var cell = CellSize;
        var w = FullWidth;
        var h = PixelHeight;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var odd = ((x + y) & 1) == 1;
                var bg = odd ? Color.FromHex("#2a2a2a") : Color.FromHex("#1a1a1a");
                handle.DrawRect(new UIBox2(x * cell, y * cell, (x + 1) * cell, (y + 1) * cell), bg);
            }
        }

        // Current frame first…
        DrawBuffer(handle, _frames[_activeFrame].Pixels, w, h, cell, 1f);

        // …then onion-skin overlay on top so opaque black does not hide it.
        if (OnionSkin && _onionPixels != null)
            DrawBuffer(handle, _onionPixels, w, h, cell, OnionOpacity);

        for (var p = 1; p < GridWidth; p++)
        {
            var x = p * _panelSize * cell;
            handle.DrawLine(new Vector2(x, 0), new Vector2(x, h * cell), Color.Cyan.WithAlpha(0.4f));
        }

        for (var p = 1; p < GridHeight; p++)
        {
            var y = p * _panelSize * cell;
            handle.DrawLine(new Vector2(0, y), new Vector2(w * cell, y), Color.Cyan.WithAlpha(0.4f));
        }
    }

    private static void DrawBuffer(DrawingHandleScreen handle, byte[] pixels, int w, int h, float cell, float opacity)
    {
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var a = pixels[i + 3] / 255f * opacity;
                if (a <= 0.01f)
                    continue;

                var color = new Color(pixels[i] / 255f, pixels[i + 1] / 255f, pixels[i + 2] / 255f, a);
                handle.DrawRect(new UIBox2(x * cell, y * cell, (x + 1) * cell, (y + 1) * cell), color);
            }
        }
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (ReadOnly || args.Function != EngineKeyFunctions.UIClick)
            return;

        if (Tool == SignboardTool.Text)
        {
            PlaceTextAt(args.RelativePosition);
            args.Handle();
            return;
        }

        _drawing = true;
        ApplyAt(args.RelativePosition);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        _drawing = false;
        _lastPixel = null;
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (!_drawing || ReadOnly || Tool == SignboardTool.Text)
            return;

        ApplyAt(args.RelativePosition);
    }

    private void PlaceTextAt(Vector2 pos)
    {
        if (string.IsNullOrWhiteSpace(PendingText))
            return;

        var cell = CellSize;
        var px = (int)(pos.X / cell);
        var py = (int)(pos.Y / cell);
        if (px < 0 || py < 0 || px >= FullWidth || py >= PixelHeight)
            return;

        var pixels = _frames[_activeFrame].Pixels;
        SignboardTextStamp.Stamp(pixels, FullWidth, PixelHeight, PendingText, BrushColor, TextScale, TextFontStyle, px, py);
        PixelsModified?.Invoke();
        TextPlaced?.Invoke();
    }

    private void ApplyAt(Vector2 pos)
    {
        var cell = CellSize;
        var px = (int)(pos.X / cell);
        var py = (int)(pos.Y / cell);
        if (px < 0 || py < 0 || px >= FullWidth || py >= PixelHeight)
            return;

        var pixel = new Vector2i(px, py);
        if (_lastPixel == pixel)
            return;

        if (_lastPixel != null && Tool is SignboardTool.Brush or SignboardTool.Eraser)
            DrawLine(_lastPixel.Value, pixel);
        else
            ApplyTool(pixel);

        _lastPixel = pixel;
        PixelsModified?.Invoke();
    }

    private void DrawLine(Vector2i from, Vector2i to)
    {
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        var sx = from.X < to.X ? 1 : -1;
        var sy = from.Y < to.Y ? 1 : -1;
        var err = dx - dy;
        var x = from.X;
        var y = from.Y;

        while (true)
        {
            ApplyTool(new Vector2i(x, y));
            if (x == to.X && y == to.Y)
                break;
            var e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
    }

    private void ApplyTool(Vector2i pixel)
    {
        var pixels = _frames[_activeFrame].Pixels;
        switch (Tool)
        {
            case SignboardTool.Brush:
                Stamp(pixels, pixel, BrushColor);
                break;
            case SignboardTool.Eraser:
                Stamp(pixels, pixel, Color.Transparent);
                break;
            case SignboardTool.Fill:
                FloodFill(pixels, pixel, BrushColor);
                break;
            case SignboardTool.Eyedropper:
            {
                var i = (pixel.Y * FullWidth + pixel.X) * 4;
                ColorPicked?.Invoke(new Color(pixels[i] / 255f, pixels[i + 1] / 255f, pixels[i + 2] / 255f, pixels[i + 3] / 255f));
                break;
            }
        }
    }

    private void Stamp(byte[] pixels, Vector2i center, Color color)
    {
        var r = Math.Max(0, BrushSize / 2);
        for (var y = center.Y - r; y <= center.Y + r; y++)
        {
            for (var x = center.X - r; x <= center.X + r; x++)
            {
                if (x < 0 || y < 0 || x >= FullWidth || y >= PixelHeight)
                    continue;
                var i = (y * FullWidth + x) * 4;
                pixels[i] = (byte)(color.R * 255);
                pixels[i + 1] = (byte)(color.G * 255);
                pixels[i + 2] = (byte)(color.B * 255);
                pixels[i + 3] = (byte)(color.A * 255);
            }
        }
    }

    private void FloodFill(byte[] pixels, Vector2i start, Color fill)
    {
        var w = FullWidth;
        var h = PixelHeight;
        var si = (start.Y * w + start.X) * 4;
        var tr = pixels[si];
        var tg = pixels[si + 1];
        var tb = pixels[si + 2];
        var ta = pixels[si + 3];
        var fr = (byte)(fill.R * 255);
        var fg = (byte)(fill.G * 255);
        var fb = (byte)(fill.B * 255);
        var fa = (byte)(fill.A * 255);
        if (tr == fr && tg == fg && tb == fb && ta == fa)
            return;

        var stack = new Stack<Vector2i>();
        stack.Push(start);
        var visited = new bool[w * h];

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h)
                continue;
            var idx = p.Y * w + p.X;
            if (visited[idx])
                continue;
            visited[idx] = true;

            var i = idx * 4;
            if (pixels[i] != tr || pixels[i + 1] != tg || pixels[i + 2] != tb || pixels[i + 3] != ta)
                continue;

            pixels[i] = fr;
            pixels[i + 1] = fg;
            pixels[i + 2] = fb;
            pixels[i + 3] = fa;

            stack.Push(new Vector2i(p.X + 1, p.Y));
            stack.Push(new Vector2i(p.X - 1, p.Y));
            stack.Push(new Vector2i(p.X, p.Y + 1));
            stack.Push(new Vector2i(p.X, p.Y - 1));
        }
    }
}
