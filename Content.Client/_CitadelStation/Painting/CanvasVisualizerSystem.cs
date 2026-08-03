using System.Numerics;
using Content.Shared._CitadelStation.Painting;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._CitadelStation.Painting;

public sealed class CanvasVisualizerSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedEaselSystem _easel = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CanvasComponent, AfterAutoHandleStateEvent>(OnCanvasState);
        SubscribeLocalEvent<CanvasComponent, ComponentStartup>(OnCanvasStartup);
        SubscribeLocalEvent<CanvasComponent, ComponentShutdown>(OnCanvasShutdown);
        SubscribeLocalEvent<EaselComponent, AfterAutoHandleStateEvent>(OnEaselState);
        SubscribeLocalEvent<EaselComponent, ComponentStartup>(OnEaselStartup);
    }

    private void OnCanvasStartup(EntityUid uid, CanvasComponent component, ComponentStartup args) =>
        UpdateCanvasVisuals(uid, component);

    private void OnCanvasState(EntityUid uid, CanvasComponent component, ref AfterAutoHandleStateEvent args)
    {
        UpdateCanvasVisuals(uid, component);

        if (_containers.TryGetContainingContainer(uid, out var container)
            && container.ID == EaselComponent.ContainerId
            && TryComp(container.Owner, out EaselComponent? easel))
        {
            UpdateEaselVisuals(container.Owner, easel);
        }
    }

    private void OnCanvasShutdown(EntityUid uid, CanvasComponent component, ComponentShutdown args)
    {
        if (TryComp(uid, out CanvasTextureComponent? textureComp))
            textureComp.Texture = null;
    }

    private void OnEaselStartup(EntityUid uid, EaselComponent component, ComponentStartup args) =>
        UpdateEaselVisuals(uid, component);

    private void OnEaselState(EntityUid uid, EaselComponent component, ref AfterAutoHandleStateEvent args) =>
        UpdateEaselVisuals(uid, component);

    private void UpdateCanvasVisuals(EntityUid uid, CanvasComponent component)
    {
        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        ApplyFrame(uid, sprite, component.FrameId);
        ApplyArtwork(uid, sprite, CanvasVisualLayers.Artwork, component);
    }

    private void UpdateEaselVisuals(EntityUid uid, EaselComponent component)
    {
        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        var active = _easel.GetActivePainting((uid, component));
        if (active == null || !TryComp(active.Value, out CanvasComponent? canvas))
        {
            _sprite.LayerSetVisible((uid, sprite), EaselVisualLayers.Artwork, false);
            return;
        }

        ApplyArtwork(uid, sprite, EaselVisualLayers.Artwork, canvas,
            displaySize: CanvasComponent.EaselArtworkDisplaySize,
            offset: new Vector2(0f, CanvasComponent.EaselArtworkOffsetY));
    }

    private void ApplyFrame(EntityUid uid, SpriteComponent sprite, ProtoId<PaintingFramePrototype>? frameId)
    {
        if (frameId != null && _prototype.TryIndex(frameId.Value, out PaintingFramePrototype? frame))
        {
            _sprite.LayerSetSprite((uid, sprite), CanvasVisualLayers.Frame, frame.Sprite);
            _sprite.LayerSetVisible((uid, sprite), CanvasVisualLayers.Frame, true);
        }
        else
        {
            _sprite.LayerSetVisible((uid, sprite), CanvasVisualLayers.Frame, false);
        }
    }

    private void ApplyArtwork(
        EntityUid uid,
        SpriteComponent sprite,
        Enum layerKey,
        CanvasComponent canvas,
        int displaySize = CanvasComponent.DisplaySize,
        Vector2? offset = null)
    {
        if (canvas.Layers.Count == 0)
            return;

        var rgba = SharedCanvasSystem.CompositeLayers(canvas.Size, canvas.Layers);
        using var image = BuildImage(rgba, canvas.Size);
        var texture = Texture.LoadFromImage(image, $"canvas-{uid}-{layerKey}");

        var texComp = EnsureComp<CanvasTextureComponent>(uid);
        texComp.Texture = texture;

        var scale = displaySize / (float)canvas.Size;
        _sprite.LayerSetTexture((uid, sprite), layerKey, texture);
        _sprite.LayerSetScale((uid, sprite), layerKey, new Vector2(scale, scale));
        _sprite.LayerSetOffset((uid, sprite), layerKey, offset ?? Vector2.Zero);
        _sprite.LayerSetVisible((uid, sprite), layerKey, true);
    }

    private static Image<Rgba32> BuildImage(byte[] rgba, int size)
    {
        var image = new Image<Rgba32>(Configuration.Default, size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                image[x, y] = new Rgba32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            }
        }

        return image;
    }
}

[RegisterComponent]
public sealed partial class CanvasTextureComponent : Component
{
    public Texture? Texture;
}
