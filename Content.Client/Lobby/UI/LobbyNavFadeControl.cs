using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Soft right-rail fade matching the Figma mock:
/// solid dark on the right, transparent toward the left.
/// </summary>
public sealed class LobbyNavFadeControl : Control
{
    private const int Steps = 48;

    /// <summary>Hairline / edge accent (mode palette).</summary>
    public Color EdgeColor { get; set; } = Color.FromHex("#E03030");

    /// <summary>Rail fill color.</summary>
    public Color BaseColor { get; set; } = Color.FromHex("#030101");

    public LobbyNavFadeControl()
    {
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var w = (float) PixelWidth;
        var h = (float) PixelHeight;
        if (w <= 0 || h <= 0)
            return;

        // Steep ramp: left edge transparent, solid opaque from ~40% — UI stays crisp.
        for (var i = 0; i < Steps; i++)
        {
            var t = i / (float) (Steps - 1);
            float alpha;
            if (t < 0.38f)
                alpha = MathF.Pow(t / 0.38f, 1.6f) * 0.92f;
            else
                alpha = 0.92f + (t - 0.38f) / 0.62f * 0.08f; // → 1.0

            var x0 = w * i / Steps;
            var x1 = w * (i + 1) / Steps;
            handle.DrawRect(new UIBox2(x0, 0, x1, h), BaseColor.WithAlpha(alpha));
        }

        handle.DrawRect(new UIBox2(0, 0, MathF.Max(1f, UIScale), h), EdgeColor.WithAlpha(0.18f));
    }
}
