using System.Numerics;
using Content.Shared._CitadelStation.Signboard;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._CitadelStation.Signboard;

public sealed class SignboardVisualizerSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedSignboardSystem _signboard = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SignboardComponent, AfterAutoHandleStateEvent>(OnState);
        SubscribeLocalEvent<SignboardComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(EntityUid uid, SignboardComponent component, ComponentStartup args) =>
        UpdateVisuals(uid, component);

    private void OnState(EntityUid uid, SignboardComponent component, ref AfterAutoHandleStateEvent args) =>
        UpdateVisuals(uid, component);

    public override void FrameUpdate(float frameTime)
    {
        // Keep multi-panel slaves in sync even if only the master PlaybackFrame changed.
        var query = EntityQueryEnumerator<SignboardComponent, SignboardTextureComponent>();
        while (query.MoveNext(out var uid, out var comp, out var tex))
        {
            var master = _signboard.ResolveMaster((uid, comp));
            if (master == null)
                continue;

            var m = master.Value.Comp;
            var key = HashCode.Combine(m.PlaybackFrame, m.Powered, m.Playing, m.Neon, m.Frames.Count, m.GridWidth, m.GridHeight);
            if (tex.VisualKey == key)
                continue;

            UpdateVisuals(uid, comp);
        }
    }

    private void UpdateVisuals(EntityUid uid, SignboardComponent component)
    {
        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        var masterEnt = _signboard.ResolveMaster((uid, component));
        if (masterEnt == null)
            return;

        var master = masterEnt.Value.Comp;
        var panelX = component.PanelX;
        var panelY = component.PanelY;
        var gridW = master.GridWidth;
        var gridH = master.GridHeight;
        var size = master.Size;

        ApplyFrame(uid, sprite, master.FrameStyleId, panelX, panelY, gridW, gridH);

        var texComp = EnsureComp<SignboardTextureComponent>(uid);
        texComp.VisualKey = HashCode.Combine(master.PlaybackFrame, master.Powered, master.Playing, master.Neon, master.Frames.Count, gridW, gridH);

        if (!master.Powered)
        {
            ApplySolidArtwork(uid, sprite, texComp, size, Robust.Shared.Maths.Color.Black);
            return;
        }

        if (master.Frames.Count == 0)
        {
            ApplySolidArtwork(uid, sprite, texComp, size, Robust.Shared.Maths.Color.Black);
            return;
        }

        var frameIdx = Math.Clamp(master.PlaybackFrame, 0, master.Frames.Count - 1);
        var fullW = size * gridW;
        var fullH = size * gridH;
        var expected = SignboardPixels.ByteLength(fullW, fullH);
        if (!SignboardPixels.TryDecompress(master.Frames[frameIdx].PixelData, expected, out var full))
        {
            ApplySolidArtwork(uid, sprite, texComp, size, Robust.Shared.Maths.Color.Black);
            return;
        }

        var slice = SignboardPixels.SlicePanel(full, fullW, fullH, panelX, panelY, size);
        ApplyArtworkTexture(uid, sprite, texComp, slice, size, master.Neon);
    }

    private void ApplySolidArtwork(
        EntityUid uid,
        SpriteComponent sprite,
        SignboardTextureComponent texComp,
        int size,
        Robust.Shared.Maths.Color color)
    {
        var rgba = new byte[size * size * 4];
        var r = (byte)(color.R * 255);
        var g = (byte)(color.G * 255);
        var b = (byte)(color.B * 255);
        var a = (byte)(color.A * 255);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }

        ApplyArtworkTexture(uid, sprite, texComp, rgba, size, neon: false);
    }

    private void ApplyArtworkTexture(
        EntityUid uid,
        SpriteComponent sprite,
        SignboardTextureComponent texComp,
        byte[] rgba,
        int size,
        bool neon)
    {
        using var image = BuildImage(rgba, size, size);
        var texture = Texture.LoadFromImage(image, $"signboard-{uid}");
        texComp.Texture = texture;

        var scale = SignboardComponent.DisplaySize / (float)size;
        _sprite.LayerSetTexture((uid, sprite), SignboardVisualLayers.Artwork, texture);
        _sprite.LayerSetScale((uid, sprite), SignboardVisualLayers.Artwork, new Vector2(scale, scale));
        _sprite.LayerSetVisible((uid, sprite), SignboardVisualLayers.Artwork, true);

        if (neon)
        {
            sprite.LayerSetShader(SignboardVisualLayers.Artwork, "unshaded");
        }
        else if (sprite.LayerMapTryGet(SignboardVisualLayers.Artwork, out var artLayer, true))
        {
            sprite.LayerSetShader(artLayer, null, null);
        }
    }

    private void ApplyFrame(
        EntityUid uid,
        SpriteComponent sprite,
        ProtoId<SignboardFramePrototype>? styleId,
        int panelX,
        int panelY,
        int gridW,
        int gridH)
    {
        if (styleId == null || !_prototype.TryIndex(styleId.Value, out SignboardFramePrototype? proto))
        {
            _sprite.LayerSetVisible((uid, sprite), SignboardVisualLayers.Frame, false);
            return;
        }

        var state = SignboardPixels.FrameState(panelX, panelY, gridW, gridH);
        var rsi = new SpriteSpecifier.Rsi(proto.Sprite.RsiPath, state);
        _sprite.LayerSetSprite((uid, sprite), SignboardVisualLayers.Frame, rsi);
        _sprite.LayerSetVisible((uid, sprite), SignboardVisualLayers.Frame, true);
    }

    private static Image<Rgba32> BuildImage(byte[] rgba, int width, int height)
    {
        var image = new Image<Rgba32>(Configuration.Default, width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[x, y] = new Rgba32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            }
        }

        return image;
    }
}

[RegisterComponent]
public sealed partial class SignboardTextureComponent : Component
{
    public Texture? Texture;
    public int VisualKey;
}
