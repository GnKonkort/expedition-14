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

        // Vignette: darken edges, keep center (black hole at ~32% x) clearer.
        // Approximate radial falloff with concentric rect rings (cheap, no shader).
        const int rings = 18;
        for (var i = 0; i < rings; i++)
        {
            var t = (i + 1) / (float) rings;
            // Transparent until ~30%, then ramp to 0.75 at edges.
            var alpha = t < 0.35f ? 0f : (t - 0.35f) / 0.65f * 0.55f;
            if (alpha <= 0.01f)
                continue;

            var insetX = w * 0.32f * (1f - t);
            var insetY = h * 0.5f * (1f - t);
            // Draw as frame (outer ring only) — four rectangles.
            var color = Color.Black.WithAlpha(alpha / rings * 3f);
            var outer = new UIBox2(0, 0, w, h);
            var inner = new UIBox2(insetX, insetY, w - insetX * 0.5f, h - insetY);
            // Top
            handle.DrawRect(new UIBox2(outer.Left, outer.Top, outer.Right, inner.Top), color);
            // Bottom
            handle.DrawRect(new UIBox2(outer.Left, inner.Bottom, outer.Right, outer.Bottom), color);
            // Left
            handle.DrawRect(new UIBox2(outer.Left, inner.Top, inner.Left, inner.Bottom), color);
            // Right
            handle.DrawRect(new UIBox2(inner.Right, inner.Top, outer.Right, inner.Bottom), color);
        }
    }
}
