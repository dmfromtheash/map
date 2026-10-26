using Map.Core.Documents;

namespace Map.Core.Editing;

public enum CanvasAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    CenterLeft,
    Center,
    CenterRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>An integer, axis-aligned region in document pixel coordinates.</summary>
public readonly record struct CanvasRegion
{
    public CanvasRegion(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ = checked(x + width);
        _ = checked(y + height);
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public WorldBounds Bounds => new(X, Y, Right, Bottom);

    public static CanvasRegion FromCorners(double firstX, double firstY, double secondX, double secondY)
    {
        if (!double.IsFinite(firstX) || !double.IsFinite(firstY)
            || !double.IsFinite(secondX) || !double.IsFinite(secondY))
        {
            throw new ArgumentOutOfRangeException(nameof(firstX), "Region coordinates must be finite.");
        }

        var left = ToInt(Math.Min(firstX, secondX));
        var top = ToInt(Math.Min(firstY, secondY));
        var right = ToInt(Math.Max(firstX, secondX));
        var bottom = ToInt(Math.Max(firstY, secondY));
        return new CanvasRegion(left, top, checked(right - left), checked(bottom - top));
    }

    internal static int ToInt(double value)
    {
        var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        if (rounded is < int.MinValue or > int.MaxValue)
        {
            throw new OverflowException("A canvas boundary is outside the supported integer range.");
        }

        return (int)rounded;
    }
}

public readonly record struct CanvasContentCounts(int Inside, int Intersecting, int Outside);

/// <summary>
/// The exact integer result shared by preview and Apply. NewBoundsInSource is the proposed page
/// boundary before content moves; OldBoundsInResult is the old page after the same offset is
/// applied to every coordinate-bearing value.
/// </summary>
public sealed record CanvasResizePlan(
    int RequestedWidth,
    int RequestedHeight,
    int Width,
    int Height,
    int OffsetX,
    int OffsetY,
    CanvasAnchor Anchor,
    bool Snapped,
    WorldBounds NewBoundsInSource,
    WorldBounds OldBoundsInResult,
    CanvasContentCounts ObjectCounts);

/// <summary>
/// Pure resize/region operations. Work is proportional to scene objects and materialized tiles;
/// no operation allocates a surface based on the logical canvas area.
/// </summary>
public static class CanvasTransformService
{
    public static CanvasResizePlan PlanResize(
        MapDocument document,
        int requestedWidth,
        int requestedHeight,
        CanvasAnchor anchor,
        GridDefinition? snapGrid = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDimensions(requestedWidth, requestedHeight);

        var (horizontal, vertical) = AnchorAxes(anchor);
        var offsetX = AxisOffset(document.Canvas.Width, requestedWidth, horizontal);
        var offsetY = AxisOffset(document.Canvas.Height, requestedHeight, vertical);
        var boundary = new CanvasRegion(-offsetX, -offsetY, requestedWidth, requestedHeight);

        if (snapGrid is not null)
        {
            boundary = SnapRegion(boundary, snapGrid);
            ValidateDimensions(boundary.Width, boundary.Height);
            offsetX = checked(-boundary.X);
            offsetY = checked(-boundary.Y);
        }

        var resultBounds = new WorldBounds(0, 0, boundary.Width, boundary.Height);
        var counts = CountObjects(document, offsetX, offsetY, resultBounds);
        return new CanvasResizePlan(
            requestedWidth,
            requestedHeight,
            boundary.Width,
            boundary.Height,
            offsetX,
            offsetY,
            anchor,
            snapGrid is not null,
            boundary.Bounds,
            new WorldBounds(
                offsetX,
                offsetY,
                checked(offsetX + document.Canvas.Width),
                checked(offsetY + document.Canvas.Height)),
            counts);
    }

    public static CanvasRegion SnapRegion(CanvasRegion region, GridDefinition grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var first = GridGeometryContract.Snap(grid, new VectorPoint(region.X, region.Y));
        var second = GridGeometryContract.Snap(grid, new VectorPoint(region.Right, region.Bottom));
        try
        {
            return CanvasRegion.FromCorners(first.X, first.Y, second.X, second.Y);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                "После привязки границы совпали; увеличьте размер региона.", ex);
        }
    }

    public static MapDocument ApplyResize(MapDocument document, CanvasResizePlan plan)
    {
        RejectEditablePaths(document);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDimensions(plan.Width, plan.Height);

        var layers = document.Layers
            .Select(layer => TranslateLayer(layer, plan.OffsetX, plan.OffsetY, clip: null))
            .ToList();
        var notes = document.Notes.Select(note => note.AnchorKind == NoteAnchorKind.World
            ? note with
            {
                WorldX = note.WorldX + plan.OffsetX,
                WorldY = note.WorldY + plan.OffsetY,
            }
            : note).ToList();

        return new MapDocument(
            document.Id,
            document.Title,
            document.CreatedUtc,
            document.Canvas.Resized(plan.Width, plan.Height),
            layers,
            document.Assets,
            notes,
            document.Checkpoints,
            document.StampForeground is { } foreground
                ? foreground with
                {
                    Tiles = TranslateTiles(foreground.Tiles, plan.OffsetX, plan.OffsetY),
                }
                : null,
            document.Category)
        {
            // The map-wide sections stay with the map; its lights move with the content.
            Atlas = document.Atlas,
            Lighting = ShiftLights(document.Lighting, plan.OffsetX, plan.OffsetY),
            Style = document.Style,
            Labels = document.Labels,
            Fog = ShiftFog(document.Fog, plan.OffsetX, plan.OffsetY),
            // Transitions move with the content; ends on other maps stay where they are.
            Transitions = [.. document.Transitions.Select(t => t with
            {
                From = t.From with { X = t.From.X + plan.OffsetX, Y = t.From.Y + plan.OffsetY },
                To = t.To is { MapId: null } to ? to with { X = to.X + plan.OffsetX, Y = to.Y + plan.OffsetY } : t.To,
            })],
            // Cards stay with the map; their points move with the content.
            Entities = [.. document.Entities.Select(e => e with { Bindings = [.. e.Bindings.Select(b => b.MapId is null && b.Kind == EntityTargetKind.Point
                ? b with { X = b.X + plan.OffsetX, Y = b.Y + plan.OffsetY } : b)] })],
        };
    }

    /// <summary>
    /// The fog moved with the content when the offset is whole cells; otherwise what was seen can
    /// not be kept exactly and the fog starts hidden again (still on, nothing revealed).
    /// </summary>
    internal static MapFog ShiftFog(MapFog fog, double dx, double dy)
    {
        if (fog.Revealed.IsEmpty || (dx == 0 && dy == 0)) return fog;
        if (dx % MapFog.TileWorld != 0 || dy % MapFog.TileWorld != 0) return MapFogEditor.HideAll(fog);
        var (sx, sy) = ((int)(dx / MapFog.TileWorld), (int)(dy / MapFog.TileWorld));
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<long, System.Collections.Immutable.ImmutableArray<ulong>>();
        foreach (var (key, rows) in fog.Revealed)
        {
            var (x, y) = MapFog.Unkey(key);
            if (x + sx >= 0 && y + sy >= 0) builder[MapFog.Key(x + sx, y + sy)] = rows;
        }
        return fog with { Revealed = builder.ToImmutable() };
    }

    /// <summary>The map's light with its sources moved by the content's offset; null stays null.</summary>
    internal static MapLighting? ShiftLights(MapLighting? lighting, double dx, double dy) =>
        lighting is null || (dx == 0 && dy == 0) ? lighting
            : lighting with { Sources = [.. lighting.Sources.Select(s => s with { X = s.X + dx, Y = s.Y + dy })] };

    public static MapDocument ExtractRegion(
        MapDocument source,
        CanvasRegion region,
        string title,
        DocumentId newDocumentId,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ValidateDimensions(region.Width, region.Height);

        var intersecting = source.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Where(item => IntersectsArea(SceneGeometry.BoundsOf(item), region.Bounds))
            .ToList();
        var includedGroups = intersecting
            .Where(item => item.GroupId is not null)
            .Select(item => item.GroupId!.Value)
            .ToHashSet();
        var includedIds = source.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Where(item => intersecting.Contains(item)
                || item.GroupId is { } groupId && includedGroups.Contains(groupId))
            .Select(item => item.Id)
            .ToHashSet();

        var clip = new CanvasRegion(0, 0, region.Width, region.Height);
        var layers = source.Layers.Select(layer => ExtractLayer(
            layer, includedIds, -region.X, -region.Y, clip)).ToList();
        var notes = source.Notes
            .Where(note => note.AnchorKind == NoteAnchorKind.Object
                ? note.ObjectId is { } objectId && includedIds.Contains(objectId)
                : ContainsPoint(region, note.WorldX, note.WorldY))
            .Select(note => note.AnchorKind == NoteAnchorKind.World
                ? note with { WorldX = note.WorldX - region.X, WorldY = note.WorldY - region.Y }
                : note)
            .ToList();

        var usedAssets = UsedAssetHashes(layers);
        var assets = source.Assets
            .Where(asset => usedAssets.Contains(asset.Sha256))
            .Select(CloneAsset)
            .ToList();

        return new MapDocument(
            newDocumentId,
            title.Trim(),
            createdUtc,
            source.Canvas.Resized(region.Width, region.Height),
            layers,
            assets,
            notes,
            checkpoints: [],
            stampForeground: source.StampForeground is { } foreground
                ? foreground with { Tiles = ExtractTiles(foreground.Tiles, region) }
                : null,
            category: source.Category)
        {
            // A part of the map keeps its look: the light (with the lights inside the part) and the style.
            Lighting = ShiftLights(source.Lighting, -region.X, -region.Y) is { } light
                ? light with { Sources = [.. light.Sources.Where(s => s.X >= 0 && s.Y >= 0 && s.X < region.Width && s.Y < region.Height)] }
                : null,
            Style = source.Style,
        };
    }

    public static TileMap TranslateTiles(TileMap source, int offsetX, int offsetY) =>
        TransformTiles(source, offsetX, offsetY, clip: null);

    private static void RejectEditablePaths(MapDocument document)
    {
        if (document.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty || !l.PlacementAreas.IsEmpty))
            throw new InvalidOperationException("Перед изменением размера или извлечением региона превратите пути и области в обычные объекты.");
    }

    public static TileMap ExtractTiles(TileMap source, CanvasRegion sourceRegion) =>
        TransformTiles(
            source,
            checked(-sourceRegion.X),
            checked(-sourceRegion.Y),
            new CanvasRegion(0, 0, sourceRegion.Width, sourceRegion.Height),
            sourceRegion);

    private static MapLayer ExtractLayer(
        MapLayer layer,
        IReadOnlySet<ObjectId> includedIds,
        int offsetX,
        int offsetY,
        CanvasRegion clip)
    {
        var translated = TranslateLayer(layer, offsetX, offsetY, clip);
        // Editable paths and areas regenerate over their whole outline, which a region cuts, so
        // the new map keeps their stamps as ordinary objects (the same result as the "turn into
        // ordinary objects" command). Draw order is already stored in the object list, so the
        // picture is unchanged; the source map keeps its editable constructions.
        return translated is ObjectLayer objects
            ? objects with
            {
                Objects = [.. objects.Objects.Where(item => includedIds.Contains(item.Id))],
                PlacementPaths = [],
                PlacementAreas = [],
            }
            : translated;
    }

    private static MapLayer TranslateLayer(
        MapLayer layer,
        int offsetX,
        int offsetY,
        CanvasRegion? clip)
    {
        var mask = layer.Mask is null
            ? null
            : layer.Mask with
            {
                Tiles = TransformTiles(layer.Mask.Tiles, offsetX, offsetY, clip),
            };

        return layer switch
        {
            BaseColorLayer baseColor => baseColor,
            ObjectLayer objects => (objects with
            {
                Objects = [.. objects.Objects.Select(item => TranslateObject(item, offsetX, offsetY))],
                // Rivers move with the canvas; a region keeps only rivers that reach into it.
                Rivers = [.. objects.Rivers
                    .Where(river => clip is null || RiverGeometry.Evaluate(river).Bounds.Intersects(
                        new WorldBounds(-offsetX, -offsetY, -offsetX + clip.Value.Width, -offsetY + clip.Value.Height)))
                    .Select(river => TranslateRiver(river, offsetX, offsetY))],
                // Roads move the same way; a region keeps only roads that reach into it.
                Roads = [.. objects.Roads
                    .Where(road => clip is null || RoadGeometry.Evaluate(road).Bounds.Intersects(
                        new WorldBounds(-offsetX, -offsetY, -offsetX + clip.Value.Width, -offsetY + clip.Value.Height)))
                    .Select(road => RoadEditor.Translate(road, offsetX, offsetY))],
            }).WithMask(mask),
            BrushLayer brush => (brush with
            {
                Tiles = TransformTiles(brush.Tiles, offsetX, offsetY, clip),
            }).WithMask(mask),
            LandLayer land => (land with
            {
                GeometryTiles = TransformTiles(land.GeometryTiles, offsetX, offsetY, clip),
                PaintTiles = TransformTiles(land.PaintTiles, offsetX, offsetY, clip),
                CliffWallTiles = TransformTiles(land.CliffWallTiles, offsetX, offsetY, clip),
                CliffBeachTiles = TransformTiles(land.CliffBeachTiles, offsetX, offsetY, clip),
                ReliefTiles = TransformTiles(land.ReliefTiles, offsetX, offsetY, clip),
            }).WithMask(mask),
            GridLayer grids => (grids with
            {
                Grids = [.. grids.Grids.Select(grid => grid with
                {
                    OffsetX = grid.OffsetX + offsetX,
                    OffsetY = grid.OffsetY + offsetY,
                })],
            }).WithMask(mask),
            FilterLayer filter => filter.WithMask(mask),
            // Hexes move with the canvas as a whole board; a region keeps the board and its cells.
            HexLayer hexes => (hexes with { Board = hexes.Board.Translated(offsetX, offsetY) }).WithMask(mask),
            _ => throw new InvalidOperationException($"Unknown layer type {layer.GetType().Name}."),
        };
    }

    private static SceneObject TranslateObject(SceneObject source, int offsetX, int offsetY) =>
        SceneObjectClone.At(source, source.X + offsetX, source.Y + offsetY);

    /// <summary>Rivers keep their ids, junctions and widths; only the node coordinates shift.</summary>
    private static River TranslateRiver(River river, int offsetX, int offsetY) => river with
    {
        Channels = [.. river.Channels.Select(channel => channel with
        {
            Nodes = [.. channel.Nodes.Select(node => node with { X = node.X + offsetX, Y = node.Y + offsetY })],
        })],
    };

    private static TileMap TransformTiles(
        TileMap source,
        int offsetX,
        int offsetY,
        CanvasRegion? clip,
        CanvasRegion? sourceClip = null)
    {
        var output = new Dictionary<TileKey, byte[]>();
        var bytesPerPixel = source.BytesPerPixel;
        IEnumerable<TileKey> keys = sourceClip is { } sourceBounds
            ? source.KeysIntersecting(
                sourceBounds.X,
                sourceBounds.Y,
                sourceBounds.Right - 1,
                sourceBounds.Bottom - 1)
            : source.Keys;

        foreach (var key in keys)
        {
            var pixels = source.TryGet(key)!;
            var sourceBaseX = (long)key.X * TileMap.TileSize;
            var sourceBaseY = (long)key.Y * TileMap.TileSize;
            for (var localY = 0; localY < TileMap.TileSize; localY++)
            {
                var sourceY = sourceBaseY + localY;
                if (sourceClip is { } sourceBoundsY
                    && (sourceY < sourceBoundsY.Y || sourceY >= sourceBoundsY.Bottom))
                {
                    continue;
                }

                for (var localX = 0; localX < TileMap.TileSize; localX++)
                {
                    var sourceX = sourceBaseX + localX;
                    if (sourceClip is { } sourceBoundsX
                        && (sourceX < sourceBoundsX.X || sourceX >= sourceBoundsX.Right))
                    {
                        continue;
                    }

                    var sourceIndex = ((localY * TileMap.TileSize) + localX) * bytesPerPixel;
                    var nonZero = false;
                    for (var channel = 0; channel < bytesPerPixel; channel++)
                    {
                        nonZero |= pixels[sourceIndex + channel] != 0;
                    }

                    if (!nonZero)
                    {
                        continue;
                    }

                    var destinationX = checked(sourceX + offsetX);
                    var destinationY = checked(sourceY + offsetY);
                    if (clip is { } destinationClip
                        && (destinationX < destinationClip.X || destinationX >= destinationClip.Right
                            || destinationY < destinationClip.Y || destinationY >= destinationClip.Bottom))
                    {
                        continue;
                    }

                    var destinationTileX = FloorDiv(destinationX, TileMap.TileSize);
                    var destinationTileY = FloorDiv(destinationY, TileMap.TileSize);
                    if (destinationTileX is < int.MinValue or > int.MaxValue
                        || destinationTileY is < int.MinValue or > int.MaxValue)
                    {
                        throw new OverflowException("Translated raster tile is outside the supported key range.");
                    }

                    var destinationKey = new TileKey((int)destinationTileX, (int)destinationTileY);
                    if (!output.TryGetValue(destinationKey, out var destination))
                    {
                        destination = new byte[source.TileByteLength];
                        output.Add(destinationKey, destination);
                    }

                    var destinationLocalX = (int)(destinationX - destinationTileX * TileMap.TileSize);
                    var destinationLocalY = (int)(destinationY - destinationTileY * TileMap.TileSize);
                    var destinationIndex =
                        ((destinationLocalY * TileMap.TileSize) + destinationLocalX) * bytesPerPixel;
                    pixels.AsSpan(sourceIndex, bytesPerPixel)
                        .CopyTo(destination.AsSpan(destinationIndex, bytesPerPixel));
                }
            }
        }

        return TileMap.Empty(bytesPerPixel).WithMany(
            output.Select(pair => new KeyValuePair<TileKey, byte[]?>(pair.Key, pair.Value)));
    }

    private static long FloorDiv(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static CanvasContentCounts CountObjects(
        MapDocument document,
        int offsetX,
        int offsetY,
        WorldBounds canvas)
    {
        var inside = 0;
        var intersecting = 0;
        var outside = 0;
        foreach (var item in document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects))
        {
            var bounds = SceneGeometry.BoundsOf(TranslateObject(item, offsetX, offsetY));
            if (Contains(canvas, bounds))
            {
                inside++;
            }
            else if (IntersectsArea(canvas, bounds))
            {
                intersecting++;
            }
            else
            {
                outside++;
            }
        }

        return new CanvasContentCounts(inside, intersecting, outside);
    }

    private static IReadOnlySet<string> UsedAssetHashes(IReadOnlyList<MapLayer> layers)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in CompositionTransfer.UsedAssetHashes(
                     layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)))
        {
            result.Add(hash);
        }

        foreach (var hash in layers.OfType<FilterLayer>()
                     .Select(layer => layer.Settings.TextureAssetSha256)
                     .Where(hash => !string.IsNullOrWhiteSpace(hash)))
        {
            result.Add(hash!);
        }

        return result;
    }

    private static MapAssetReference CloneAsset(MapAssetReference asset) => new(
        asset.Sha256,
        asset.DisplayName,
        asset.MediaType,
        asset.Extension,
        asset.Storage,
        asset.ImportedUtc,
        asset.Provenance,
        asset.License,
        asset.EmbeddedContent,
        asset.CatalogId,
        asset.RelativePath,
        asset.FallbackThumbnailPng);

    private static bool ContainsPoint(CanvasRegion region, double x, double y) =>
        x >= region.X && x < region.Right && y >= region.Y && y < region.Bottom;

    private static bool Contains(WorldBounds outer, WorldBounds inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool IntersectsArea(WorldBounds first, WorldBounds second) =>
        first.Left < second.Right && first.Right > second.Left
        && first.Top < second.Bottom && first.Bottom > second.Top;

    private static (int Horizontal, int Vertical) AnchorAxes(CanvasAnchor anchor) => anchor switch
    {
        CanvasAnchor.TopLeft => (0, 0),
        CanvasAnchor.TopCenter => (1, 0),
        CanvasAnchor.TopRight => (2, 0),
        CanvasAnchor.CenterLeft => (0, 1),
        CanvasAnchor.Center => (1, 1),
        CanvasAnchor.CenterRight => (2, 1),
        CanvasAnchor.BottomLeft => (0, 2),
        CanvasAnchor.BottomCenter => (1, 2),
        CanvasAnchor.BottomRight => (2, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
    };

    private static int AxisOffset(int oldSize, int newSize, int anchorAxis) => anchorAxis switch
    {
        0 => 0,
        1 => (newSize - oldSize) / 2,
        2 => checked(newSize - oldSize),
        _ => throw new ArgumentOutOfRangeException(nameof(anchorAxis)),
    };

    private static void ValidateDimensions(int width, int height)
    {
        if (!CanvasDimensionPolicy.IsValid(width) || !CanvasDimensionPolicy.IsValid(height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Canvas dimensions must be within {CanvasDimensionPolicy.Minimum}..{CanvasDimensionPolicy.Maximum}.");
        }
    }
}

/// <summary>One history entry with exact immutable before/after snapshots.</summary>
public sealed class ResizeCanvasCommand : IEditCommand
{
    private readonly MapDocument _before;
    private readonly MapDocument _after;

    public ResizeCanvasCommand(MapDocument before, MapDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Id != after.Id)
        {
            throw new ArgumentException("Resize keeps the current document identity.", nameof(after));
        }

        _before = before;
        _after = after;
    }

    public string Name => "Resize map canvas";

    public MapDocument Apply(MapDocument document)
    {
        if (document.Id != _before.Id)
        {
            throw new InvalidOperationException("Resize command belongs to another document.");
        }

        return _after;
    }

    public MapDocument Revert(MapDocument document)
    {
        if (document.Id != _after.Id)
        {
            throw new InvalidOperationException("Resize command belongs to another document.");
        }

        return _before;
    }
}
