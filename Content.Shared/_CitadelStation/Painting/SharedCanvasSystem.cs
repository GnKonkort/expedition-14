using System.IO;
using System.IO.Compression;
using Content.Shared.Examine;
using Robust.Shared.Prototypes;

namespace Content.Shared._CitadelStation.Painting;

public abstract class SharedCanvasSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CanvasComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<CanvasComponent, ExaminedEvent>(OnExamined);
    }

    private void OnInit(EntityUid uid, CanvasComponent component, ComponentInit args)
    {
        if (!IsValidSize(component.Size))
            component.Size = CanvasComponent.DefaultSize;

        if (component.Layers.Count == 0)
        {
            component.Layers.Add(CreateLayer("Background", component.Size, opaqueWhite: true));
            component.ActiveLayer = 0;
            Dirty(uid, component);
        }
    }

    private void OnExamined(EntityUid uid, CanvasComponent component, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("canvas-examine-size", ("size", component.Size)));
        args.PushMarkup(Loc.GetString("canvas-examine-layers", ("count", component.Layers.Count)));

        if (!string.IsNullOrWhiteSpace(component.Title))
            args.PushMarkup(Loc.GetString("canvas-examine-title", ("title", component.Title)));

        if (!string.IsNullOrWhiteSpace(component.Signature))
            args.PushMarkup(Loc.GetString("canvas-examine-signature", ("author", component.Signature)));

        if (component.Locked)
            args.PushMarkup(Loc.GetString("canvas-examine-locked"));
        else if (component.FrameId != null)
            args.PushMarkup(Loc.GetString("canvas-examine-framed"));
        else
            args.PushMarkup(Loc.GetString("canvas-examine-blank"));
    }

    public static bool IsValidSize(int size) =>
        size is 64 or 128 or 256;

    public static int RawByteLength(int size) => size * size * 4;

    public static CanvasLayerData CreateLayer(string name, int size, bool opaqueWhite)
    {
        return new CanvasLayerData
        {
            Name = name,
            PixelData = CompressPixels(CreateBlankPixels(size, opaqueWhite)),
            Visible = true,
            Opacity = 1f
        };
    }

    public static byte[] CreateBlankPixels(int size, bool opaqueWhite)
    {
        var pixels = new byte[RawByteLength(size)];
        if (!opaqueWhite)
            return pixels; // fully transparent

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    public static byte[] CompressPixels(byte[] rgba)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(rgba, 0, rgba.Length);
        return output.ToArray();
    }

    public static bool TryDecompressPixels(byte[] compressed, int expectedLength, out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (compressed.Length == 0 || expectedLength <= 0)
            return false;

        try
        {
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedLength);
            deflate.CopyTo(output);
            rgba = output.ToArray();
            return rgba.Length == expectedLength;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Composite visible layers bottom→top into a single RGBA buffer.</summary>
    public static byte[] CompositeLayers(int size, IReadOnlyList<CanvasLayerData> layers)
    {
        var length = RawByteLength(size);
        var result = new byte[length];

        foreach (var layer in layers)
        {
            if (!layer.Visible || layer.Opacity <= 0.001f)
                continue;

            if (!TryDecompressPixels(layer.PixelData, length, out var src))
                continue;

            var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
            for (var i = 0; i < length; i += 4)
            {
                var srcA = (src[i + 3] / 255f) * opacity;
                if (srcA <= 0.001f)
                    continue;

                var dstA = result[i + 3] / 255f;
                var outA = srcA + dstA * (1f - srcA);
                if (outA <= 0.001f)
                    continue;

                for (var c = 0; c < 3; c++)
                {
                    var s = src[i + c] / 255f;
                    var d = result[i + c] / 255f;
                    var o = (s * srcA + d * dstA * (1f - srcA)) / outA;
                    result[i + c] = (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);
                }

                result[i + 3] = (byte)Math.Clamp((int)(outA * 255f + 0.5f), 0, 255);
            }
        }

        return result;
    }

    /// <summary>Nearest-neighbor resize of an RGBA buffer.</summary>
    public static byte[] ResizePixels(byte[] src, int srcSize, int dstSize)
    {
        var dst = new byte[RawByteLength(dstSize)];
        if (srcSize <= 0 || dstSize <= 0)
            return dst;

        for (var y = 0; y < dstSize; y++)
        {
            var srcY = y * srcSize / dstSize;
            for (var x = 0; x < dstSize; x++)
            {
                var srcX = x * srcSize / dstSize;
                var si = (srcY * srcSize + srcX) * 4;
                var di = (y * dstSize + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    public static bool TryResizeCanvas(CanvasComponent component, int newSize)
    {
        if (!IsValidSize(newSize) || newSize == component.Size)
            return false;

        var oldSize = component.Size;
        var oldLen = RawByteLength(oldSize);

        foreach (var layer in component.Layers)
        {
            if (!TryDecompressPixels(layer.PixelData, oldLen, out var rgba))
                rgba = CreateBlankPixels(oldSize, opaqueWhite: false);

            layer.PixelData = CompressPixels(ResizePixels(rgba, oldSize, newSize));
        }

        component.Size = newSize;
        return true;
    }

    public bool ValidateLayers(int size, List<CanvasLayerData> layers, out string? error)
    {
        error = null;
        if (!IsValidSize(size))
        {
            error = "bad-size";
            return false;
        }

        if (layers.Count is < 1 or > CanvasComponent.MaxLayers)
        {
            error = "bad-layer-count";
            return false;
        }

        var expected = RawByteLength(size);
        foreach (var layer in layers)
        {
            if (layer.PixelData.Length > expected)
            {
                error = "bad-payload";
                return false;
            }

            if (!TryDecompressPixels(layer.PixelData, expected, out _))
            {
                error = "bad-decompress";
                return false;
            }

            layer.Opacity = Math.Clamp(layer.Opacity, 0f, 1f);
            if (string.IsNullOrWhiteSpace(layer.Name))
                layer.Name = "Layer";
            else if (layer.Name.Length > 32)
                layer.Name = layer.Name[..32];
        }

        return true;
    }

    public List<CanvasLayerData> CloneLayers(IReadOnlyList<CanvasLayerData> source)
    {
        var list = new List<CanvasLayerData>(source.Count);
        foreach (var layer in source)
        {
            list.Add(new CanvasLayerData
            {
                Name = layer.Name,
                PixelData = (byte[])layer.PixelData.Clone(),
                Visible = layer.Visible,
                Opacity = layer.Opacity
            });
        }

        return list;
    }

    public string SanitizeTitle(string title)
    {
        title = title.Trim();
        if (title.Length > CanvasComponent.MaxTitleLength)
            title = title[..CanvasComponent.MaxTitleLength];
        return title;
    }

    public void CopyCanvasData(CanvasComponent from, CanvasComponent to)
    {
        to.Size = from.Size;
        to.Layers = CloneLayers(from.Layers);
        to.ActiveLayer = from.ActiveLayer;
        to.FrameId = from.FrameId;
        to.Title = from.Title;
        to.Signature = from.Signature;
        to.Locked = from.Locked;
    }

    public bool TryGetFrame(ProtoId<PaintingFramePrototype>? frameId, out PaintingFramePrototype frame)
    {
        frame = default!;
        if (frameId == null)
            return false;

        return _prototype.TryIndex(frameId.Value, out frame!);
    }
}
