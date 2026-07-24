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
    private const int Steps = 40;

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

        var baseColor = Color.FromHex("#02030A");

        for (var i = 0; i < Steps; i++)
        {
            var t = i / (float) (Steps - 1); // 0 = left, 1 = right
            // CSS: linear-gradient(to left, rgba(...,0.94) 55%, transparent 100%)
            // → solid from ~45% onward, fade in across the left 45%.
            var alpha = t < 0.45f ? t / 0.45f * 0.94f : 0.94f;
            var x0 = w * i / Steps;
            var x1 = w * (i + 1) / Steps;
            handle.DrawRect(new UIBox2(x0, 0, x1, h), baseColor.WithAlpha(alpha));
        }

        // Hairline gold edge
        handle.DrawRect(new UIBox2(0, 0, MathF.Max(1f, UIScale), h), Color.FromHex("#F0A830").WithAlpha(0.1f));
    }
}
