using System.Numerics;
using Content.Shared.Maps;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._CitadelStation.SubGrid.Overlays;

/// <summary>
/// Redraws SubGrid floor tiles in the entity layer so they cover host sprites beneath the pad
/// (those sprites are temporarily drawn at <see cref="DrawDepth.BelowFloor"/>).
/// Pad occupants keep normal draw depth and appear above these tiles.
/// </summary>
public sealed class SubGridTileOverlay : Overlay
{
    private readonly IEntityManager _entManager;
    private readonly IResourceCache _resources;
    private readonly ITileDefinitionManager _tileDefs;
    private readonly SharedMapSystem _map;
    private readonly SharedTransformSystem _transform;
    private readonly EntityLookupSystem _lookup;

    private readonly Dictionary<ResPath, Texture> _textureCache = new();

    /// <summary>Interleaved with sprites by ZIndex / DrawDepth.</summary>
    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;

    public SubGridTileOverlay(
        IEntityManager entManager,
        IResourceCache resources,
        ITileDefinitionManager tileDefs,
        SharedMapSystem map,
        SharedTransformSystem transform,
        EntityLookupSystem lookup)
    {
        _entManager = entManager;
        _resources = resources;
        _tileDefs = tileDefs;
        _map = map;
        _transform = transform;
        _lookup = lookup;
        // Above BelowFloor-covered host entities; at FloorTiles so HighFloorObjects stairs stay visible.
        ZIndex = (int) DrawDepth.FloorTiles;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        var xforms = _entManager.GetEntityQuery<TransformComponent>();
        var query = _entManager.EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var grid, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            var (_, _, worldMatrix, invWorld) = _transform.GetWorldPositionRotationMatrixWithInv(xform, xforms);
            var localBounds = invWorld.TransformBox(args.WorldBounds).Enlarged(grid.TileSize);
            handle.SetTransform(worldMatrix);

            var tiles = _map.GetLocalTilesEnumerator(uid, grid, localBounds);
            while (tiles.MoveNext(out var tileRef))
            {
                if (tileRef.Tile.IsEmpty)
                    continue;

                if (_tileDefs[tileRef.Tile.TypeId] is not ContentTileDefinition def || def.Sprite is not { } spritePath)
                    continue;

                if (!TryGetTexture(spritePath, out var texture))
                    continue;

                var bounds = _lookup.GetLocalBounds(tileRef, grid.TileSize);
                handle.DrawTextureRect(texture, bounds);
            }
        }

        handle.SetTransform(Matrix3x2.Identity);
    }

    private bool TryGetTexture(ResPath path, out Texture texture)
    {
        if (_textureCache.TryGetValue(path, out texture!))
            return true;

        if (!_resources.TryGetResource<TextureResource>(path, out var resource))
        {
            texture = default!;
            return false;
        }

        texture = resource.Texture;
        _textureCache[path] = texture;
        return true;
    }
}
