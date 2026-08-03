using System.Numerics;
using Content.Shared._CitadelStation.Painting;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.Client._CitadelStation.Painting.UI;

public enum CanvasTool : byte
{
    Brush,
    Eraser,
    Fill,
    Eyedropper
}

/// <summary>
/// Fixed-size editor surface. Pixel resolution can be 64/128/256; UI size stays EditorDisplaySize.
/// Draws composite of all layers, edits only the active layer.
/// </summary>
public sealed class CanvasDrawControl : Control
{
    private int _size = CanvasComponent.DefaultSize;
    private readonly List<(string Name, byte[] Pixels, bool Visible, float Opacity)> _layers = [];
    private int _activeLayer;
    private bool _drawing;
    private Vector2i? _lastPixel;

    public Color BrushColor { get; set; } = Color.Black;
    public int BrushSize { get; set; } = 1;
    public CanvasTool Tool { get; set; } = CanvasTool.Brush;
    public bool ReadOnly { get; set; }

    public event Action? PixelsModified;
    public event Action<Color>? ColorPicked;

    public new int PixelSize => _size;
    public int ActiveLayer => _activeLayer;
    public int LayerCount => _layers.Count;

    private float CellSize => CanvasComponent.EditorDisplaySize / (float)_size;

    public CanvasDrawControl()
    {
        MinSize = new Vector2(CanvasComponent.EditorDisplaySize, CanvasComponent.EditorDisplaySize);
        MaxSize = MinSize;
        MouseFilter = MouseFilterMode.Stop;
        EnsureDefaultLayer();
    }

    public void LoadFromState(int size, List<CanvasLayerData> layers, int activeLayer)
    {
        _size = size;
        _layers.Clear();
        var expected = SharedCanvasSystem.RawByteLength(size);

        foreach (var layer in layers)
        {
            if (!SharedCanvasSystem.TryDecompressPixels(layer.PixelData, expected, out var rgba))
                rgba = SharedCanvasSystem.CreateBlankPixels(size, opaqueWhite: false);

            _layers.Add((layer.Name, rgba, layer.Visible, layer.Opacity));
        }

        if (_layers.Count == 0)
            EnsureDefaultLayer();

        _activeLayer = Math.Clamp(activeLayer, 0, _layers.Count - 1);
        MinSize = new Vector2(CanvasComponent.EditorDisplaySize, CanvasComponent.EditorDisplaySize);
        MaxSize = MinSize;
    }

    public List<CanvasLayerData> ExportLayers()
    {
        var list = new List<CanvasLayerData>(_layers.Count);
        foreach (var (name, pixels, visible, opacity) in _layers)
        {
            list.Add(new CanvasLayerData
            {
                Name = name,
                PixelData = SharedCanvasSystem.CompressPixels(pixels),
                Visible = visible,
                Opacity = opacity
            });
        }

        return list;
    }

    public IReadOnlyList<(string Name, bool Visible, float Opacity)> GetLayerMeta()
    {
        var list = new List<(string, bool, float)>(_layers.Count);
        foreach (var (name, _, visible, opacity) in _layers)
            list.Add((name, visible, opacity));
        return list;
    }

    public void SetActiveLayer(int index)
    {
        if (index < 0 || index >= _layers.Count)
            return;
        _activeLayer = index;
    }

    public bool TryAddLayer(string name)
    {
        if (_layers.Count >= CanvasComponent.MaxLayers)
            return false;

        _layers.Add((name, SharedCanvasSystem.CreateBlankPixels(_size, opaqueWhite: false), true, 1f));
        _activeLayer = _layers.Count - 1;
        PixelsModified?.Invoke();
        return true;
    }

    public bool TryRemoveActiveLayer()
    {
        if (_layers.Count <= 1)
            return false;

        _layers.RemoveAt(_activeLayer);
        _activeLayer = Math.Clamp(_activeLayer, 0, _layers.Count - 1);
        PixelsModified?.Invoke();
        return true;
    }

    public void SetLayerVisible(int index, bool visible)
    {
        if (index < 0 || index >= _layers.Count)
            return;
        var layer = _layers[index];
        _layers[index] = (layer.Name, layer.Pixels, visible, layer.Opacity);
        PixelsModified?.Invoke();
    }

    public void SetLayerOpacity(int index, float opacity)
    {
        if (index < 0 || index >= _layers.Count)
            return;
        var layer = _layers[index];
        _layers[index] = (layer.Name, layer.Pixels, layer.Visible, Math.Clamp(opacity, 0f, 1f));
        PixelsModified?.Invoke();
    }

    public void SetLayerName(int index, string name)
    {
        if (index < 0 || index >= _layers.Count)
            return;
        var layer = _layers[index];
        _layers[index] = (name, layer.Pixels, layer.Visible, layer.Opacity);
        PixelsModified?.Invoke();
    }

    private void EnsureDefaultLayer()
    {
        _layers.Clear();
        _layers.Add(("Background", SharedCanvasSystem.CreateBlankPixels(_size, opaqueWhite: true), true, 1f));
        _activeLayer = 0;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var cell = CellSize;
        var display = CanvasComponent.EditorDisplaySize;

        // Checkerboard for transparency
        const int check = 8;
        for (var y = 0; y < display; y += check)
        {
            for (var x = 0; x < display; x += check)
            {
                var light = ((x / check) + (y / check)) % 2 == 0;
                handle.DrawRect(
                    UIBox2.FromDimensions(x, y, check, check),
                    light ? Color.FromHex("#CCCCCC") : Color.FromHex("#999999"));
            }
        }

        var composite = CompositeLocal();
        for (var y = 0; y < _size; y++)
        {
            for (var x = 0; x < _size; x++)
            {
                var i = (y * _size + x) * 4;
                if (composite[i + 3] == 0)
                    continue;

                var color = new Color(composite[i], composite[i + 1], composite[i + 2], composite[i + 3]);
                handle.DrawRect(UIBox2.FromDimensions(x * cell, y * cell, cell, cell), color);
            }
        }

        if (_size <= 64)
        {
            var grid = Color.Black.WithAlpha(0.08f);
            for (var i = 0; i <= _size; i++)
            {
                handle.DrawLine(new Vector2(i * cell, 0), new Vector2(i * cell, display), grid);
                handle.DrawLine(new Vector2(0, i * cell), new Vector2(display, i * cell), grid);
            }
        }
    }

    private byte[] CompositeLocal()
    {
        var length = SharedCanvasSystem.RawByteLength(_size);
        var result = new byte[length];

        foreach (var (_, pixels, visible, opacity) in _layers)
        {
            if (!visible || opacity <= 0.001f)
                continue;

            var op = Math.Clamp(opacity, 0f, 1f);
            for (var i = 0; i < length; i += 4)
            {
                var srcA = (pixels[i + 3] / 255f) * op;
                if (srcA <= 0.001f)
                    continue;

                var dstA = result[i + 3] / 255f;
                var outA = srcA + dstA * (1f - srcA);
                if (outA <= 0.001f)
                    continue;

                for (var c = 0; c < 3; c++)
                {
                    var s = pixels[i + c] / 255f;
                    var d = result[i + c] / 255f;
                    result[i + c] = (byte)Math.Clamp((int)(((s * srcA + d * dstA * (1f - srcA)) / outA) * 255f + 0.5f), 0, 255);
                }

                result[i + 3] = (byte)Math.Clamp((int)(outA * 255f + 0.5f), 0, 255);
            }
        }

        return result;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (ReadOnly || args.Function != EngineKeyFunctions.UIClick)
            return;

        _drawing = true;
        _lastPixel = null;
        HandlePointer(args.RelativePosition);
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
        if (!_drawing || ReadOnly)
            return;

        HandlePointer(args.RelativePosition);
    }

    private void HandlePointer(Vector2 relative)
    {
        var cell = CellSize;
        var px = (int)(relative.X / cell);
        var py = (int)(relative.Y / cell);
        if (px < 0 || py < 0 || px >= _size || py >= _size)
            return;

        var pixel = new Vector2i(px, py);

        switch (Tool)
        {
            case CanvasTool.Eyedropper:
                ColorPicked?.Invoke(GetCompositePixel(pixel));
                break;
            case CanvasTool.Fill:
                FloodFill(pixel, BrushColor);
                PixelsModified?.Invoke();
                break;
            case CanvasTool.Brush:
                if (_lastPixel != null)
                    DrawLine(_lastPixel.Value, pixel, BrushColor);
                else
                    Stamp(pixel, BrushColor);
                _lastPixel = pixel;
                PixelsModified?.Invoke();
                break;
            case CanvasTool.Eraser:
                var clear = Color.Transparent;
                if (_lastPixel != null)
                    DrawLine(_lastPixel.Value, pixel, clear);
                else
                    Stamp(pixel, clear);
                _lastPixel = pixel;
                PixelsModified?.Invoke();
                break;
        }
    }

    private void DrawLine(Vector2i from, Vector2i to, Color color)
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
            Stamp(new Vector2i(x, y), color);
            if (x == to.X && y == to.Y)
                break;

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private void Stamp(Vector2i center, Color color)
    {
        var radius = Math.Max(0, (BrushSize - 1) / 2);
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (x < 0 || y < 0 || x >= _size || y >= _size)
                    continue;
                SetPixel(new Vector2i(x, y), color);
            }
        }
    }

    private void FloodFill(Vector2i start, Color fillColor)
    {
        var pixels = _layers[_activeLayer].Pixels;
        var target = GetLayerPixel(start);
        if (ColorsEqual(target, fillColor))
            return;

        var stack = new Stack<Vector2i>();
        stack.Push(start);
        var visited = new bool[_size * _size];

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            var idx = p.Y * _size + p.X;
            if (visited[idx])
                continue;
            visited[idx] = true;

            if (!ColorsEqual(GetLayerPixel(p), target))
                continue;

            SetPixel(p, fillColor);
            TryPush(p.X + 1, p.Y);
            TryPush(p.X - 1, p.Y);
            TryPush(p.X, p.Y + 1);
            TryPush(p.X, p.Y - 1);
        }

        void TryPush(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size)
                return;
            stack.Push(new Vector2i(x, y));
        }
    }

    private Color GetLayerPixel(Vector2i p)
    {
        var pixels = _layers[_activeLayer].Pixels;
        var i = (p.Y * _size + p.X) * 4;
        return new Color(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private Color GetCompositePixel(Vector2i p)
    {
        var composite = CompositeLocal();
        var i = (p.Y * _size + p.X) * 4;
        return new Color(composite[i], composite[i + 1], composite[i + 2], composite[i + 3]);
    }

    private void SetPixel(Vector2i p, Color color)
    {
        var pixels = _layers[_activeLayer].Pixels;
        var i = (p.Y * _size + p.X) * 4;
        pixels[i] = color.RByte;
        pixels[i + 1] = color.GByte;
        pixels[i + 2] = color.BByte;
        pixels[i + 3] = color.AByte;
    }

    private static bool ColorsEqual(Color a, Color b) =>
        a.RByte == b.RByte && a.GByte == b.GByte && a.BByte == b.BByte && a.AByte == b.AByte;
}
