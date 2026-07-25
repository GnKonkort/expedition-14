using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Scanlines + vignette overlays from the Figma Make mock (non-interactive).
/// </summary>
public sealed class LobbyFxOverlay : Control
{
    public LobbyFxOverlay()
    {
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var w = (float) PixelWidth;
        var h = (float) PixelHeight;
        if (w <= 0 || h <= 0)
            return;

        // Scanlines: every 4px, 1px dark (matching repeating-linear-gradient 3px / 1px)
        var line = MathF.Max(1f, UIScale);
        var period = 4f * UIScale;
        for (var y = 0f; y < h; y += period)
        {
            handle.DrawRect(new UIBox2(0, y + period - line, w, y + period), Color.Black.WithAlpha(0.07f));
        }

        // Soft vignette — clear center, darker edges so UI chrome stays readable.
        const int rings = 22;
        for (var i = 0; i < rings; i++)
        {
            var t = (i + 1) / (float) rings;
            var alpha = t < 0.22f ? 0f : (t - 0.22f) / 0.78f * 0.68f;
            if (alpha <= 0.01f)
                continue;

            var insetX = w * 0.38f * (1f - t);
            var insetY = h * 0.48f * (1f - t);
            var color = Color.Black.WithAlpha(alpha / rings * 3.2f);
            var outer = new UIBox2(0, 0, w, h);
            var inner = new UIBox2(insetX, insetY, w - insetX * 0.55f, h - insetY);
            handle.DrawRect(new UIBox2(outer.Left, outer.Top, outer.Right, inner.Top), color);
            handle.DrawRect(new UIBox2(outer.Left, inner.Bottom, outer.Right, outer.Bottom), color);
            handle.DrawRect(new UIBox2(outer.Left, inner.Top, inner.Left, inner.Bottom), color);
            handle.DrawRect(new UIBox2(inner.Right, inner.Top, outer.Right, inner.Bottom), color);
        }
    }
}
