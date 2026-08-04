using System.IO;
using System.IO.Compression;

namespace Content.Shared._CitadelStation.Signboard;

/// <summary>Rectangular RGBA helpers for signboard frames (width×height).</summary>
public static class SignboardPixels
{
    public static int ByteLength(int width, int height) => width * height * 4;

    public static bool IsValidSize(int size) => size is 64 or 128 or 256;

    public static byte[] CreateBlank(int width, int height, bool opaqueBlack)
    {
        var pixels = new byte[ByteLength(width, height)];
        if (!opaqueBlack)
            return pixels;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    public static byte[] Compress(byte[] rgba)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(rgba, 0, rgba.Length);
        return output.ToArray();
    }

    public static bool TryDecompress(byte[] compressed, int expectedLength, out byte[] rgba)
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

    public static SignboardFrameData CreateFrame(string name, int width, int height, bool opaqueBlack = true)
    {
        return new SignboardFrameData
        {
            Name = name,
            PixelData = Compress(CreateBlank(width, height, opaqueBlack))
        };
    }

    /// <summary>Nearest-neighbor resize of an RGBA buffer.</summary>
    public static byte[] Resize(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[ByteLength(dstW, dstH)];
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            return dst;

        for (var y = 0; y < dstH; y++)
        {
            var srcY = y * srcH / dstH;
            for (var x = 0; x < dstW; x++)
            {
                var srcX = x * srcW / dstW;
                var si = (srcY * srcW + srcX) * 4;
                var di = (y * dstW + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    /// <summary>Extract a panelSize×panelSize slice from a wide/tall frame.</summary>
    public static byte[] SlicePanel(byte[] src, int fullWidth, int fullHeight, int panelX, int panelY, int panelSize)
    {
        var dst = new byte[ByteLength(panelSize, panelSize)];
        var startX = panelX * panelSize;
        var startY = panelY * panelSize;

        for (var y = 0; y < panelSize; y++)
        {
            var sy = startY + y;
            if (sy < 0 || sy >= fullHeight)
                continue;

            for (var x = 0; x < panelSize; x++)
            {
                var sx = startX + x;
                if (sx < 0 || sx >= fullWidth)
                    continue;

                var si = (sy * fullWidth + sx) * 4;
                var di = (y * panelSize + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    /// <summary>RSI state for a panel based on which sides still need a bezel (no neighbor).</summary>
    public static string FrameState(int panelX, int panelY, int gridW, int gridH)
    {
        var left = panelX == 0;
        var right = panelX == gridW - 1;
        var top = panelY == 0;
        var bottom = panelY == gridH - 1;

        return (left, right, top, bottom, gridW, gridH) switch
        {
            (_, _, _, _, 1, 1) => "solo",
            (true, false, true, true, _, 1) => "left",
            (false, true, true, true, _, 1) => "right",
            (false, false, true, true, _, 1) => "mid",
            (true, true, true, false, 1, _) => "top",
            (true, true, false, true, 1, _) => "bottom",
            (true, true, false, false, 1, _) => "vmid",
            (true, false, true, false, _, _) => "nw",
            (false, true, true, false, _, _) => "ne",
            (true, false, false, true, _, _) => "sw",
            (false, true, false, true, _, _) => "se",
            (false, false, true, false, _, _) => "n",
            (false, false, false, true, _, _) => "s",
            (true, false, false, false, _, _) => "w",
            (false, true, false, false, _, _) => "e",
            (true, true, true, true, _, _) => "solo",
            (true, false, true, true, _, _) => "left",
            (false, true, true, true, _, _) => "right",
            (false, false, true, true, _, _) => "mid",
            (true, true, true, false, _, _) => "top",
            (true, true, false, true, _, _) => "bottom",
            (true, true, false, false, _, _) => "vmid",
            _ => "center"
        };
    }
}
