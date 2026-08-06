using System.IO;
using System.Numerics;
using System.Text;
using Content.Client.Inventory;
using Content.Shared._Arcane.ERP.Clothing;
using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Arcane.ERP.Clothing;

/// <summary>
/// Expands jumpsuit into body + breast cloth as TWO Dir4 states so bounce can move
/// only the breast overlay without jiggling arms/legs.
/// </summary>
public sealed class ErpJumpsuitBreastStretchSystem : EntitySystem
{
    private static readonly string[] SizeStates = ["aa", "b", "c", "d"];

    private static readonly HumanoidVisualLayers[] BodyLayersUnderJumpsuit =
    [
        HumanoidVisualLayers.Chest,
        HumanoidVisualLayers.LArm,
        HumanoidVisualLayers.RArm,
        HumanoidVisualLayers.LHand,
        HumanoidVisualLayers.RHand,
        HumanoidVisualLayers.LLeg,
        HumanoidVisualLayers.RLeg,
        HumanoidVisualLayers.LFoot,
        HumanoidVisualLayers.RFoot,
    ];

    private const string BodyStateName = "equipped";
    private const string BreastStateName = "breast";
    private const byte AlphaThreshold = 40;
    private const byte SolidClothAlpha = 200;

    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private readonly MemoryContentRoot _memoryRoot = new();
    private bool _rootMounted;

    private const int StretchGenVersion = 8;
    private readonly Dictionary<(string rsi, string state, string breastRsi, int size, string species, Sex sex, int gen), ResPath> _rsiCache = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ErpJumpsuitStretchComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _memoryRoot.Clear();
    }

    private void OnShutdown(Entity<ErpJumpsuitStretchComponent> ent, ref ComponentShutdown args)
    {
        if (TryComp<SpriteComponent>(ent, out var sprite))
            RemoveBounceLayer(ent, sprite);

        ent.Comp.LayerKeys.Clear();
        ent.Comp.BounceLayerKeyActive = null;
        ent.Comp.GeneratedRsiPath = null;
    }

    public void Clear(EntityUid uid)
    {
        if (TryComp<SpriteComponent>(uid, out var sprite))
            RemoveBounceLayer(uid, sprite);

        if (HasComp<ErpJumpsuitStretchComponent>(uid))
            RemComp<ErpJumpsuitStretchComponent>(uid);
    }

    public void TryApply(EntityUid uid)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite) ||
            !TryComp<InventorySlotsComponent>(uid, out var slots) ||
            !TryComp<ErpOrganVisualsComponent>(uid, out var visuals) ||
            !ErpJumpsuitDisplacement.TryGetBreastSize(visuals, out var breastSize))
        {
            Clear(uid);
            return;
        }

        if (!slots.VisualLayerKeys.TryGetValue(ErpJumpsuitDisplacement.Slot, out var layerKeys) ||
            layerKeys.Count == 0)
        {
            Clear(uid);
            return;
        }

        string? rsiPath = null;
        string? state = null;
        string? primaryKey = null;

        foreach (var key in layerKeys)
        {
            if (key.EndsWith("-displacement", StringComparison.Ordinal))
                continue;

            if (!_sprite.LayerMapTryGet((uid, sprite), key, out var idx, false))
                continue;

            var layer = sprite[idx];
            if (layer.ActualRsi == null || layer.RsiState.Name == null)
                continue;

            rsiPath = layer.ActualRsi.Path.ToString();
            state = layer.RsiState.Name;
            primaryKey = key;
            break;
        }

        if (rsiPath == null || state == null || primaryKey == null)
        {
            Clear(uid);
            return;
        }

        var humanoid = CompOrNull<HumanoidAppearanceComponent>(uid);
        var species = humanoid?.Species.Id ?? "Human";
        var sex = humanoid?.Sex ?? Sex.Female;
        var breastRsi = ResolveBreastRsi(species);
        var cacheKey = (rsiPath, state, breastRsi, breastSize, species, sex, StretchGenVersion);
        if (!_rsiCache.TryGetValue(cacheKey, out var generatedPath))
        {
            if (!TryBuildGeneratedRsi(rsiPath, state, breastRsi, breastSize, species, sex, out generatedPath))
                return;

            _rsiCache[cacheKey] = generatedPath;
        }

        var stretch = EnsureComp<ErpJumpsuitStretchComponent>(uid);
        stretch.BreastSize = breastSize;
        stretch.SourceRsiPath = rsiPath;
        stretch.SourceState = state;
        stretch.GeneratedRsiPath = generatedPath;
        stretch.LayerKeys.Clear();
        stretch.LayerKeys.Add(primaryKey);

        // Body stays put; breast cloth is a sibling overlay.
        _sprite.LayerSetRsi((uid, sprite), primaryKey, generatedPath, BodyStateName);
        EnsureBounceLayer(uid, sprite, primaryKey, generatedPath);
        stretch.BounceLayerKeyActive = ErpJumpsuitStretchComponent.BounceLayerKey;
    }

    private void EnsureBounceLayer(EntityUid uid, SpriteComponent sprite, string primaryKey, ResPath generatedPath)
    {
        var primaryColor = Robust.Shared.Maths.Color.White;
        var insertAfter = (int?) null;
        if (_sprite.LayerMapTryGet((uid, sprite), primaryKey, out var primaryIdx, false))
        {
            insertAfter = primaryIdx + 1;
            primaryColor = sprite[primaryIdx].Color;
        }

        if (!_sprite.LayerMapTryGet((uid, sprite), ErpJumpsuitStretchComponent.BounceLayerKey, out var bounceIdx, false))
        {
            bounceIdx = _sprite.AddLayer(
                (uid, sprite),
                new SpriteSpecifier.Rsi(generatedPath, BreastStateName),
                insertAfter);
            _sprite.LayerMapSet((uid, sprite), ErpJumpsuitStretchComponent.BounceLayerKey, bounceIdx);
        }

        _sprite.LayerSetRsi((uid, sprite), bounceIdx, generatedPath, BreastStateName);
        _sprite.LayerSetColor((uid, sprite), bounceIdx, primaryColor);
        _sprite.LayerSetVisible((uid, sprite), bounceIdx, true);
        _sprite.LayerSetOffset((uid, sprite), bounceIdx, Vector2.Zero);
        _sprite.LayerSetScale((uid, sprite), bounceIdx, Vector2.One);
        _sprite.LayerSetRotation((uid, sprite), bounceIdx, Angle.Zero);
    }

    private void RemoveBounceLayer(EntityUid uid, SpriteComponent sprite)
    {
        if (!_sprite.LayerMapTryGet((uid, sprite), ErpJumpsuitStretchComponent.BounceLayerKey, out var idx, false))
            return;

        _sprite.LayerSetVisible((uid, sprite), idx, false);
        _sprite.LayerMapRemove((uid, sprite), ErpJumpsuitStretchComponent.BounceLayerKey);
        _sprite.RemoveLayer((uid, sprite), idx);
    }

    private string ResolveBreastRsi(string species)
    {
        ErpOrganVisualPrototype? fallback = null;

        foreach (var proto in _proto.EnumeratePrototypes<ErpOrganVisualPrototype>())
        {
            if (proto.Slot != ErpOrganSlots.Breasts)
                continue;

            if (proto.Species.Count == 0)
            {
                fallback = proto;
                continue;
            }

            if (proto.Species.Contains(species))
                return proto.Rsi.TrimStart('/');
        }

        return fallback?.Rsi.TrimStart('/') ?? "Textures/_Arcane/ERP/Mobs/Breasts/human.rsi";
    }

    private bool TryBuildGeneratedRsi(
        string rsiPath,
        string state,
        string breastRsiPath,
        int size,
        string species,
        Sex sex,
        out ResPath generatedRsiPath)
    {
        generatedRsiPath = default;
        EnsureMemoryRoot();

        var sizeState = SizeStates[Math.Clamp(size, 1, 4) - 1];
        var clothingPath = NormalizeTexturePath(rsiPath) / $"{state}.png";
        var breastPath = NormalizeTexturePath(breastRsiPath) / $"{sizeState}.png";

        if (!_resources.TryContentFileRead(clothingPath, out var clothStream) ||
            !_resources.TryContentFileRead(breastPath, out var breastStream))
            return false;

        using var clothingSource = Image.Load<Rgba32>(clothStream);
        using var breast = Image.Load<Rgba32>(breastStream);
        clothStream.Dispose();
        breastStream.Dispose();

        if (clothingSource.Width < 32 || clothingSource.Height < 32 || breast.Width < 32 || breast.Height < 32)
            return false;

        using var bodySilhouette = BuildBodySilhouette(clothingSource.Width, clothingSource.Height, species, sex);

        using var bodyClothing = clothingSource.Clone();
        ExpandClothingOverSilhouette(bodyClothing, bodySilhouette, breast: null);

        using var fullClothing = clothingSource.Clone();
        using var fullSilhouette = bodySilhouette.Clone();
        OrAlphaInto(fullSilhouette, breast);
        ExpandClothingOverSilhouette(fullClothing, fullSilhouette, breast);

        // Breast cloth only on overlay; punch that region out of the body suit.
        using var breastOverlay = ExtractBreastOverlay(fullClothing, bodyClothing, breast);
        ClearWhereMasked(bodyClothing, breast);

        var id = HashCode.Combine(rsiPath, state, breastRsiPath, size, species, sex, StretchGenVersion).ToString("x8");
        var folderRel = new ResPath($"Textures/_Arcane/ERP/Generated/jumpsuit-stretch/{id}.rsi");
        var metaRel = folderRel / "meta.json";
        var bodyPngRel = folderRel / $"{BodyStateName}.png";
        var breastPngRel = folderRel / $"{BreastStateName}.png";

        var metaJson =
            "{\n" +
            "  \"version\": 1,\n" +
            "  \"license\": \"CC-BY-SA-4.0\",\n" +
            "  \"copyright\": \"runtime generated erp jumpsuit stretch\",\n" +
            "  \"size\": { \"x\": 32, \"y\": 32 },\n" +
            "  \"states\": [\n" +
            "    { \"name\": \"" + BodyStateName + "\", \"directions\": 4 },\n" +
            "    { \"name\": \"" + BreastStateName + "\", \"directions\": 4 }\n" +
            "  ]\n" +
            "}\n";

        _memoryRoot.AddOrUpdateFile(metaRel, Encoding.UTF8.GetBytes(metaJson));

        using (var ms = new MemoryStream())
        {
            bodyClothing.SaveAsPng(ms);
            _memoryRoot.AddOrUpdateFile(bodyPngRel, ms.ToArray());
        }

        using (var ms = new MemoryStream())
        {
            breastOverlay.SaveAsPng(ms);
            _memoryRoot.AddOrUpdateFile(breastPngRel, ms.ToArray());
        }

        generatedRsiPath = new ResPath($"_Arcane/ERP/Generated/jumpsuit-stretch/{id}.rsi");
        var cachePath = SpriteSystem.TextureRoot / generatedRsiPath;
        _cache.ReloadResource<RSIResource>(cachePath);
        return _cache.TryGetResource<RSIResource>(cachePath, out _);
    }

    /// <summary>
    /// Cloth on the breast mask from the full stretch. Prefers full-stretch pixels; falls back
    /// to body-stretch samples so the overlay is never an empty/white plate.
    /// </summary>
    private static Image<Rgba32> ExtractBreastOverlay(
        Image<Rgba32> fullClothing,
        Image<Rgba32> bodyClothing,
        Image<Rgba32> breast)
    {
        var overlay = new Image<Rgba32>(fullClothing.Width, fullClothing.Height);
        var w = Math.Min(fullClothing.Width, breast.Width);
        var h = Math.Min(fullClothing.Height, breast.Height);
        var fullSpan = fullClothing.GetPixelSpan();
        var bodySpan = bodyClothing.GetPixelSpan();
        var breastSpan = breast.GetPixelSpan();
        var outSpan = overlay.GetPixelSpan();

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var bIdx = y * breast.Width + x;
            if (breastSpan[bIdx].A < AlphaThreshold)
                continue;

            var cIdx = y * fullClothing.Width + x;
            var full = fullSpan[cIdx];
            if (full.A >= AlphaThreshold)
            {
                outSpan[cIdx] = full;
                continue;
            }

            // Should be rare — keep shape filled with body cloth rather than leaving a hole.
            var body = bodySpan[cIdx];
            if (body.A >= AlphaThreshold)
                outSpan[cIdx] = body;
        }

        return overlay;
    }

    private static void ClearWhereMasked(Image<Rgba32> image, Image<Rgba32> mask)
    {
        var w = Math.Min(image.Width, mask.Width);
        var h = Math.Min(image.Height, mask.Height);
        var imgSpan = image.GetPixelSpan();
        var maskSpan = mask.GetPixelSpan();

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (maskSpan[y * mask.Width + x].A < AlphaThreshold)
                continue;

            imgSpan[y * image.Width + x] = default;
        }
    }

    private Image<Rgba32> BuildBodySilhouette(int width, int height, string species, Sex sex)
    {
        var silhouette = new Image<Rgba32>(width, height);

        if (_proto.TryIndex<SpeciesPrototype>(species, out var speciesProto) &&
            _proto.TryIndex(speciesProto.SpriteSet, out HumanoidSpeciesBaseSpritesPrototype? spriteSet))
        {
            foreach (var layer in BodyLayersUnderJumpsuit)
            {
                if (!spriteSet.Sprites.TryGetValue(layer, out var baseId) || string.IsNullOrEmpty(baseId))
                    continue;

                var morphId = HumanoidVisualLayersExtension.GetSexMorph(layer, sex, baseId);
                if (!_proto.TryIndex<HumanoidSpeciesSpriteLayer>(morphId, out var layerProto) &&
                    !_proto.TryIndex(baseId, out layerProto))
                    continue;

                if (layerProto.BaseSprite is not SpriteSpecifier.Rsi rsiSpec)
                    continue;

                var partPath = NormalizeTexturePath(rsiSpec.RsiPath.ToString()) / $"{rsiSpec.RsiState}.png";
                if (!_resources.TryContentFileRead(partPath, out var partStream))
                    continue;

                using var part = Image.Load<Rgba32>(partStream);
                partStream.Dispose();
                OrAlphaInto(silhouette, part);
            }
        }

        return silhouette;
    }

    private static void OrAlphaInto(Image<Rgba32> dest, Image<Rgba32> src)
    {
        var w = Math.Min(dest.Width, src.Width);
        var h = Math.Min(dest.Height, src.Height);
        var destSpan = dest.GetPixelSpan();
        var srcSpan = src.GetPixelSpan();

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var s = srcSpan[y * src.Width + x];
            if (s.A < AlphaThreshold)
                continue;

            var idx = y * dest.Width + x;
            var d = destSpan[idx];
            if (s.A > d.A)
                destSpan[idx] = new Rgba32(255, 255, 255, s.A);
        }
    }

    private void EnsureMemoryRoot()
    {
        if (_rootMounted)
            return;

        _resources.AddRoot(ResPath.Root, _memoryRoot);
        _rootMounted = true;
    }

    /// <summary>
    /// Grow clothing into the silhouette with pattern extrapolation.
    /// When <paramref name="breast"/> is set, the original jumpsuit outline that cuts through
    /// breasts is moved onto the breast edge.
    /// </summary>
    public static void ExpandClothingOverSilhouette(
        Image<Rgba32> clothing,
        Image<Rgba32> silhouette,
        Image<Rgba32>? breast)
    {
        var w = Math.Min(clothing.Width, silhouette.Width);
        var h = Math.Min(clothing.Height, silhouette.Height);
        const int tile = 32;
        var dirsX = Math.Max(1, w / tile);
        var dirsY = Math.Max(1, h / tile);

        // Snapshot before expansion — pattern / outline sampling must use original cloth only.
        using var original = clothing.Clone();
        var clothSpan = clothing.GetPixelSpan();
        var origSpan = original.GetPixelSpan();
        var silSpan = silhouette.GetPixelSpan();

        for (var ty = 0; ty < dirsY; ty++)
        for (var tx = 0; tx < dirsX; tx++)
        {
            var ox = tx * tile;
            var oy = ty * tile;
            ExpandTile(clothSpan, origSpan, silSpan, clothing.Width, original.Width, silhouette.Width,
                clothing.Height, silhouette.Height, ox, oy, tile);

            if (breast == null)
                continue;

            var breastSpan = breast.GetPixelSpan();
            // Move the old suit outline off the breast mass onto the breast perimeter.
            RelocateOutlineOntoBreastEdge(clothSpan, origSpan, breastSpan, clothing.Width, original.Width,
                breast.Width, clothing.Height, breast.Height, ox, oy, tile);
        }
    }

    private static void ExpandTile(
        Span<Rgba32> clothing,
        Span<Rgba32> original,
        Span<Rgba32> silhouette,
        int clothW,
        int origW,
        int silW,
        int clothH,
        int silH,
        int ox,
        int oy,
        int tile)
    {
        var pending = new List<(int idx, Rgba32 color)>(64);
        var fillColor = EstimateFillColor(original, origW, ox, oy, tile);

        for (var iter = 0; iter < 32; iter++)
        {
            pending.Clear();

            for (var y = 0; y < tile; y++)
            for (var x = 0; x < tile; x++)
            {
                var px = ox + x;
                var py = oy + y;
                if (px >= clothW || py >= clothH || px >= silW || py >= silH)
                    continue;

                if (silhouette[py * silW + px].A < AlphaThreshold)
                    continue;

                var cIdx = py * clothW + px;
                if (clothing[cIdx].A >= AlphaThreshold)
                    continue;

                if (!TrySamplePatternCloth(original, origW, ox, oy, tile, px, py, fillColor, out var sample))
                    continue;

                sample.A = 255;
                pending.Add((cIdx, sample));
            }

            if (pending.Count == 0)
                break;

            foreach (var (idx, color) in pending)
                clothing[idx] = color;
        }
    }

    /// <summary>
    /// Erase the original jumpsuit outline where it crosses the breast, then redraw it
    /// on the outer breast perimeter. Only touches the breast bounding box — never pants/skirt.
    /// </summary>
    private static void RelocateOutlineOntoBreastEdge(
        Span<Rgba32> clothing,
        Span<Rgba32> original,
        Span<Rgba32> breast,
        int clothW,
        int origW,
        int breastW,
        int clothH,
        int breastH,
        int ox,
        int oy,
        int tile)
    {
        if (!TryGetBreastBounds(breast, breastW, breastH, ox, oy, tile,
                out var bMinX, out var bMinY, out var bMaxX, out var bMaxY))
            return;

        // Pad 2px — old front-hem often sits 1px beside breast alpha on side views.
        var minX = Math.Max(ox, bMinX - 2);
        var minY = Math.Max(oy, bMinY - 2);
        var maxX = Math.Min(ox + tile - 1, Math.Min(clothW - 1, bMaxX + 2));
        var maxY = Math.Min(oy + tile - 1, Math.Min(clothH - 1, bMaxY + 2));

        // Fill colour from the chest band only — pants/skirt must not skew the sample.
        var chestY1 = Math.Min(oy + tile, oy + 18);
        var fillColor = EstimateFillColorInBand(original, origW, ox, oy, tile, oy, chestY1);
        if (fillColor.A < AlphaThreshold)
            fillColor = EstimateFillColor(original, origW, ox, oy, tile);

        var outline = EstimateOutlineColorInBand(original, origW, ox, oy, tile, oy, chestY1, fillColor);
        if (outline.A < AlphaThreshold)
            outline = Darken(fillColor, 0.72f);
        outline.A = 255;

        var tileCx = ox + 15.5f;
        var breastCx = (bMinX + bMaxX) * 0.5f;
        // Body sits on the chest side of the breast (opposite the tip). Fixed tile-centre
        // broke West: the body crease sat on centre and was treated as an outer edge.
        var bodyCx = breastCx < tileCx - 0.25f
            ? bMaxX + 2.5f
            : breastCx > tileCx + 0.25f
                ? bMinX - 2.5f
                : tileCx;
        var bodyCy = oy + 12.5f;

        // Wipe only the tip-side silhouette edge of the old suit (East→right, West→left).
        var wipeLeft = breastCx <= tileCx;
        var wipeRight = breastCx >= tileCx;
        for (var py = bMinY; py <= bMaxY; py++)
        {
            if (py < oy || py >= oy + tile || py >= clothH)
                continue;

            int? leftEdge = null;
            int? rightEdge = null;
            for (var x = ox; x < ox + tile && x < clothW; x++)
            {
                if (original[py * origW + x].A < AlphaThreshold)
                    continue;
                leftEdge ??= x;
                rightEdge = x;
            }

            if (wipeLeft && leftEdge is { } lx && lx >= minX && lx <= maxX)
                PaintFillAt(clothing, original, clothW, origW, ox, oy, tile, lx, py, fillColor);
            if (wipeRight && rightEdge is { } rx && rx >= minX && rx <= maxX && rx != leftEdge)
                PaintFillAt(clothing, original, clothW, origW, ox, oy, tile, rx, py, fillColor);
        }

        // 1) Under the breast: always repaint with cloth fill/pattern so the old suit
        //    front-hem cannot survive as a vertical seam (grey-on-grey outlines included).
        //    Near-breast pad: only clear outline-like pixels (do not eat arms/collar).
        for (var py = minY; py <= maxY; py++)
        for (var px = minX; px <= maxX; px++)
        {
            var underBreast = px < breastW && py < breastH
                              && breast[py * breastW + px].A >= AlphaThreshold;
            var nearBreast = underBreast
                             || HasBreastNeighbor(breast, breastW, breastH, ox, oy, tile, px, py);

            if (!nearBreast)
                continue;

            var cIdx = py * clothW + px;
            var cur = clothing[cIdx];

            if (underBreast)
            {
                if (!TrySamplePatternCloth(original, origW, ox, oy, tile, px, py, fillColor, out var breastFill))
                    breastFill = fillColor;
                breastFill.A = 255;
                clothing[cIdx] = breastFill;
                continue;
            }

            // Pad only: remove leftover hem next to breast alpha.
            if (cur.A < AlphaThreshold)
                continue;

            var wasOrigOutline = IsOriginalOutlinePixel(original, origW, ox, oy, tile, px, py)
                                 || IsTonalOutlinePixel(original, origW, ox, oy, tile, px, py, fillColor);
            var looksLikeOutline = fillColor.A >= AlphaThreshold
                                   && Luminance(fillColor) - Luminance(cur) >= 14f;

            if (!wasOrigOutline && !looksLikeOutline)
                continue;

            if (IsClothingOuterRim(clothing, clothW, clothH, ox, oy, tile, px, py))
                continue;

            if (!TrySamplePatternCloth(original, origW, ox, oy, tile, px, py, fillColor, out var sample))
                sample = fillColor;
            sample.A = 255;
            clothing[cIdx] = sample;
        }

        // 2) Redraw outline on the outer-facing breast perimeter only.
        for (var py = bMinY; py <= bMaxY; py++)
        for (var px = bMinX; px <= bMaxX; px++)
        {
            if (px >= breastW || py >= breastH || px >= clothW || py >= clothH)
                continue;

            if (breast[py * breastW + px].A < AlphaThreshold)
                continue;

            if (!IsBreastPerimeter4(breast, breastW, breastH, ox, oy, tile, px, py))
                continue;

            if (!IsOuterFacingBreastEdge(breast, breastW, breastH, ox, oy, tile, px, py, bodyCx, bodyCy))
                continue;

            clothing[py * clothW + px] = outline;
        }
    }

    private static void PaintFillAt(
        Span<Rgba32> clothing,
        Span<Rgba32> original,
        int clothW,
        int origW,
        int ox,
        int oy,
        int tile,
        int px,
        int py,
        Rgba32 fillColor)
    {
        if (!TrySamplePatternCloth(original, origW, ox, oy, tile, px, py, fillColor, out var sample))
            sample = fillColor;
        sample.A = 255;
        clothing[py * clothW + px] = sample;
    }

    private static bool TryGetBreastBounds(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        out int minX,
        out int minY,
        out int maxX,
        out int maxY)
    {
        minX = int.MaxValue;
        minY = int.MaxValue;
        maxX = int.MinValue;
        maxY = int.MinValue;
        var found = false;

        for (var y = 0; y < tile; y++)
        for (var x = 0; x < tile; x++)
        {
            var px = ox + x;
            var py = oy + y;
            if (px >= breastW || py >= breastH)
                continue;
            if (breast[py * breastW + px].A < AlphaThreshold)
                continue;

            found = true;
            if (px < minX) minX = px;
            if (py < minY) minY = py;
            if (px > maxX) maxX = px;
            if (py > maxY) maxY = py;
        }

        return found;
    }

    private static bool HasBreastNeighbor(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        int px,
        int py)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0)
                continue;
            var nx = px + dx;
            var ny = py + dy;
            if (nx < ox || ny < oy || nx >= ox + tile || ny >= oy + tile)
                continue;
            if (nx >= breastW || ny >= breastH)
                continue;
            if (breast[ny * breastW + nx].A >= AlphaThreshold)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a transparent neighbour lies further from the torso centre than this pixel
    /// (breast tip / lower hem), not the crease against the body.
    /// </summary>
    private static bool IsOuterFacingBreastEdge(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        int px,
        int py,
        float bodyCx,
        float bodyCy)
    {
        var distHere = Dist2(px + 0.5f, py + 0.5f, bodyCx, bodyCy);

        if (TryOuterNeighbor(breast, breastW, breastH, ox, oy, tile, px + 1, py, bodyCx, bodyCy, distHere))
            return true;
        if (TryOuterNeighbor(breast, breastW, breastH, ox, oy, tile, px - 1, py, bodyCx, bodyCy, distHere))
            return true;
        if (TryOuterNeighbor(breast, breastW, breastH, ox, oy, tile, px, py + 1, bodyCx, bodyCy, distHere))
            return true;
        if (TryOuterNeighbor(breast, breastW, breastH, ox, oy, tile, px, py - 1, bodyCx, bodyCy, distHere))
            return true;

        return false;
    }

    private static bool TryOuterNeighbor(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        int nx,
        int ny,
        float bodyCx,
        float bodyCy,
        float distHere)
    {
        if (!IsBreastTransparentOrOob(breast, breastW, breastH, ox, oy, tile, nx, ny))
            return false;

        var distOut = Dist2(nx + 0.5f, ny + 0.5f, bodyCx, bodyCy);
        return distOut >= distHere - 0.01f;
    }

    private static float Dist2(float x, float y, float cx, float cy)
    {
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy;
    }

    private static bool IsClothingOuterRim(
        Span<Rgba32> clothing,
        int clothW,
        int clothH,
        int ox,
        int oy,
        int tile,
        int px,
        int py)
    {
        if (clothing[py * clothW + px].A < AlphaThreshold)
            return false;

        return IsClothTransparentOrOob(clothing, clothW, clothH, ox, oy, tile, px + 1, py)
               || IsClothTransparentOrOob(clothing, clothW, clothH, ox, oy, tile, px - 1, py)
               || IsClothTransparentOrOob(clothing, clothW, clothH, ox, oy, tile, px, py + 1)
               || IsClothTransparentOrOob(clothing, clothW, clothH, ox, oy, tile, px, py - 1);
    }

    private static bool IsClothTransparentOrOob(
        Span<Rgba32> clothing,
        int clothW,
        int clothH,
        int ox,
        int oy,
        int tile,
        int nx,
        int ny)
    {
        if (nx < ox || ny < oy || nx >= ox + tile || ny >= oy + tile ||
            nx >= clothW || ny >= clothH)
            return true;

        return clothing[ny * clothW + nx].A < AlphaThreshold;
    }

    private static bool TileHasBreast(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile)
    {
        return TryGetBreastBounds(breast, breastW, breastH, ox, oy, tile, out _, out _, out _, out _);
    }

    private static bool IsBreastPerimeter4(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        int px,
        int py)
    {
        // 4-neighbour only (no ReadOnlySpan collection expr — breaks Robust ILVerify).
        return IsBreastTransparentOrOob(breast, breastW, breastH, ox, oy, tile, px + 1, py)
               || IsBreastTransparentOrOob(breast, breastW, breastH, ox, oy, tile, px - 1, py)
               || IsBreastTransparentOrOob(breast, breastW, breastH, ox, oy, tile, px, py + 1)
               || IsBreastTransparentOrOob(breast, breastW, breastH, ox, oy, tile, px, py - 1);
    }

    private static bool IsBreastTransparentOrOob(
        Span<Rgba32> breast,
        int breastW,
        int breastH,
        int ox,
        int oy,
        int tile,
        int nx,
        int ny)
    {
        if (nx < ox || ny < oy || nx >= ox + tile || ny >= oy + tile ||
            nx >= breastW || ny >= breastH)
            return true;

        return breast[ny * breastW + nx].A < AlphaThreshold;
    }

    /// <summary>
    /// Extrapolate cloth pattern past the edge: mirror sample across nearest original pixel.
    /// Never copies outline/fringe colours into the breast fill.
    /// </summary>
    private static bool TrySamplePatternCloth(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int px,
        int py,
        Rgba32 fillFallback,
        out Rgba32 sample)
    {
        sample = default;

        if (!TryFindNearestOriginal(original, origW, ox, oy, tile, px, py, out var qx, out var qy))
        {
            if (fillFallback.A < AlphaThreshold)
                return false;
            sample = fillFallback;
            return true;
        }

        // Mirror across the edge: continues stripes / panels outside the original silhouette.
        var mx = 2 * qx - px;
        var my = 2 * qy - py;
        if (mx >= ox && my >= oy && mx < ox + tile && my < oy + tile)
        {
            var mirrored = original[my * origW + mx];
            if (IsUsableFillSample(original, origW, ox, oy, tile, mx, my, mirrored, fillFallback))
            {
                sample = mirrored;
                sample.A = 255;
                return true;
            }
        }

        // Walk from Q further into the cloth (away from P) for a solid non-outline fill.
        var idx = qx - px;
        var idy = qy - py;
        var len = MathF.Sqrt(idx * idx + idy * idy);
        if (len > 0.001f)
        {
            var sx = idx / len;
            var sy = idy / len;
            for (var step = 0; step <= 12; step++)
            {
                var x = (int) MathF.Round(qx + sx * step);
                var y = (int) MathF.Round(qy + sy * step);
                if (x < ox || y < oy || x >= ox + tile || y >= oy + tile)
                    break;

                var p = original[y * origW + x];
                if (!IsUsableFillSample(original, origW, ox, oy, tile, x, y, p, fillFallback))
                    continue;

                sample = p;
                sample.A = 255;
                return true;
            }
        }

        var q = original[qy * origW + qx];
        if (IsUsableFillSample(original, origW, ox, oy, tile, qx, qy, q, fillFallback))
        {
            sample = q;
            sample.A = 255;
            return true;
        }

        if (fillFallback.A < AlphaThreshold)
            return false;

        sample = fillFallback;
        return true;
    }

    private static bool IsUsableFillSample(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int x,
        int y,
        Rgba32 p,
        Rgba32 fill)
    {
        if (p.A < SolidClothAlpha)
            return false;

        if (IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
            return false;

        // Reject tonal outline (much darker than typical fill).
        if (fill.A >= AlphaThreshold && Luminance(fill) - Luminance(p) >= 28f)
            return false;

        return true;
    }

    private static bool TryFindNearestOriginal(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int px,
        int py,
        out int qx,
        out int qy)
    {
        qx = 0;
        qy = 0;
        var best = int.MaxValue;

        for (var y = oy; y < oy + tile; y++)
        for (var x = ox; x < ox + tile; x++)
        {
            if (original[y * origW + x].A < AlphaThreshold)
                continue;

            var d = (x - px) * (x - px) + (y - py) * (y - py);
            if (d >= best)
                continue;

            best = d;
            qx = x;
            qy = y;
        }

        return best != int.MaxValue;
    }

    private static bool IsOriginalOutlinePixel(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int x,
        int y)
    {
        var p = original[y * origW + x];
        if (p.A < AlphaThreshold)
            return false;

        // Geometric outline: touches transparency.
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0)
                continue;

            var nx = x + dx;
            var ny = y + dy;
            if (nx < ox || ny < oy || nx >= ox + tile || ny >= oy + tile)
                return true;

            if (original[ny * origW + nx].A < AlphaThreshold)
                return true;
        }

        return false;
    }

    private static bool IsTonalOutlinePixel(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int x,
        int y,
        Rgba32 fill)
    {
        if (fill.A < AlphaThreshold)
            return false;

        var p = original[y * origW + x];
        if (p.A < AlphaThreshold)
            return false;

        if (IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
            return true;

        return Luminance(fill) - Luminance(p) >= 28f;
    }

    private static Rgba32 EstimateOutlineColor(Span<Rgba32> original, int origW, int ox, int oy, int tile)
    {
        var fill = EstimateFillColor(original, origW, ox, oy, tile);
        var sumR = 0;
        var sumG = 0;
        var sumB = 0;
        var count = 0;

        for (var y = oy; y < oy + tile; y++)
        for (var x = ox; x < ox + tile; x++)
        {
            if (!IsTonalOutlinePixel(original, origW, ox, oy, tile, x, y, fill) &&
                !IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
                continue;

            var p = original[y * origW + x];
            if (p.A < AlphaThreshold)
                continue;

            sumR += p.R;
            sumG += p.G;
            sumB += p.B;
            count++;
        }

        if (count == 0)
            return Darken(fill, 0.72f);

        return new Rgba32(
            (byte) (sumR / count),
            (byte) (sumG / count),
            (byte) (sumB / count),
            255);
    }

    private static Rgba32 EstimateFillColor(Span<Rgba32> original, int origW, int ox, int oy, int tile)
    {
        var sumR = 0;
        var sumG = 0;
        var sumB = 0;
        var count = 0;

        for (var y = oy; y < oy + tile; y++)
        for (var x = ox; x < ox + tile; x++)
        {
            var p = original[y * origW + x];
            if (p.A < SolidClothAlpha)
                continue;
            // Geometric edge only — avoid recursion with tonal outline.
            if (IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
                continue;

            sumR += p.R;
            sumG += p.G;
            sumB += p.B;
            count++;
        }

        if (count == 0)
        {
            for (var y = oy; y < oy + tile; y++)
            for (var x = ox; x < ox + tile; x++)
            {
                var p = original[y * origW + x];
                if (p.A < SolidClothAlpha)
                    continue;
                sumR += p.R;
                sumG += p.G;
                sumB += p.B;
                count++;
            }
        }

        if (count == 0)
            return default;

        return new Rgba32(
            (byte) (sumR / count),
            (byte) (sumG / count),
            (byte) (sumB / count),
            255);
    }

    private static Rgba32 EstimateFillColorInBand(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int y0,
        int y1Exclusive)
    {
        var sumR = 0;
        var sumG = 0;
        var sumB = 0;
        var count = 0;
        var yStart = Math.Max(oy, y0);
        var yEnd = Math.Min(oy + tile, y1Exclusive);

        for (var y = yStart; y < yEnd; y++)
        for (var x = ox; x < ox + tile; x++)
        {
            var p = original[y * origW + x];
            if (p.A < SolidClothAlpha)
                continue;
            if (IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
                continue;

            sumR += p.R;
            sumG += p.G;
            sumB += p.B;
            count++;
        }

        if (count == 0)
            return default;

        return new Rgba32(
            (byte) (sumR / count),
            (byte) (sumG / count),
            (byte) (sumB / count),
            255);
    }

    private static Rgba32 EstimateOutlineColorInBand(
        Span<Rgba32> original,
        int origW,
        int ox,
        int oy,
        int tile,
        int y0,
        int y1Exclusive,
        Rgba32 fill)
    {
        var sumR = 0;
        var sumG = 0;
        var sumB = 0;
        var count = 0;
        var yStart = Math.Max(oy, y0);
        var yEnd = Math.Min(oy + tile, y1Exclusive);

        for (var y = yStart; y < yEnd; y++)
        for (var x = ox; x < ox + tile; x++)
        {
            if (!IsTonalOutlinePixel(original, origW, ox, oy, tile, x, y, fill) &&
                !IsOriginalOutlinePixel(original, origW, ox, oy, tile, x, y))
                continue;

            var p = original[y * origW + x];
            if (p.A < AlphaThreshold)
                continue;

            sumR += p.R;
            sumG += p.G;
            sumB += p.B;
            count++;
        }

        if (count == 0)
            return Darken(fill, 0.72f);

        return new Rgba32(
            (byte) (sumR / count),
            (byte) (sumG / count),
            (byte) (sumB / count),
            255);
    }

    private static float Luminance(Rgba32 p) => 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;

    private static Rgba32 Darken(Rgba32 c, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return new Rgba32(
            (byte) (c.R * factor),
            (byte) (c.G * factor),
            (byte) (c.B * factor),
            255);
    }

    private static ResPath NormalizeTexturePath(string path)
    {
        path = path.Replace('\\', '/').Trim('/');
        if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            return new ResPath("/" + path);

        if (path.StartsWith("/Textures/", StringComparison.OrdinalIgnoreCase))
            return new ResPath(path);

        return new ResPath("/Textures/") / path;
    }
}
