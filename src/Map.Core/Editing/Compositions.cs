using Map.Core.Documents;

namespace Map.Core.Editing;

public sealed record CompositionCaptureOptions
{
    public bool IncludeBackground { get; init; }
    public bool IncludeGrids { get; init; }
    public bool IncludeFilters { get; init; }
    public double ReferenceGridScale { get; init; } = 1;
}

public sealed record CompositionPackage(
    EditableComposition Composition, IReadOnlyList<MapAssetReference> Assets);

public static class CompositionTransfer
{
    public static CompositionPackage Copy(
        MapDocument source, IReadOnlyCollection<ObjectId> ids,
        CompositionCaptureOptions? options = null, string name = "Composition")
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(ids);
        options ??= new CompositionCaptureOptions();
        var selected = source.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
            .Where(item => ids.Contains(item.Id)).ToList();
        if (selected.Count == 0) throw new ArgumentException("Copy needs at least one selected object.", nameof(ids));

        var bounds = selected.Select(SceneGeometry.BoundsOf).Aggregate((a, b) => new WorldBounds(
            Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        var relative = selected.Select(item => VectorGeometry.WithNewIds(
            SceneObjectClone.At(item, item.X - bounds.Left, item.Y - bounds.Top))).ToList();
        var hashes = UsedAssetHashes(relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = source.Assets.Where(asset => hashes.Contains(asset.Sha256)).ToList();
        var composition = new EditableComposition(
            CompositionId.New(), source.Id, name,
            Math.Max(0.001, bounds.Right - bounds.Left), Math.Max(0.001, bounds.Bottom - bounds.Top), relative)
        {
            ReferenceGridScale = Math.Max(0.001, options.ReferenceGridScale),
            Assets = [.. assets.Select(asset => new CompositionAssetProvenance(
                asset.Sha256, asset.DisplayName, asset.Provenance, asset.License))],
            IncludeBackground = options.IncludeBackground,
            BackgroundColor = options.IncludeBackground ? source.BaseColor.Color : null,
            IncludeGrids = options.IncludeGrids,
            Grids = options.IncludeGrids
                ? [.. source.Layers.OfType<GridLayer>().Where(layer => layer.Visible).SelectMany(layer => layer.Grids)
                    .Select(grid => grid with { OffsetX = grid.OffsetX - bounds.Left, OffsetY = grid.OffsetY - bounds.Top })]
                : [],
            IncludeFilters = options.IncludeFilters,
            Filters = options.IncludeFilters
                ? [.. source.Layers.OfType<FilterLayer>().Where(layer => layer.Visible).Select(layer => layer.Settings)]
                : [],
        };
        return new(composition, assets);
    }

    public static SceneObject CreateInstance(
        CompositionPackage package, double anchorWorldX, double anchorWorldY,
        double? targetReferenceGridScale = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var remapped = Remap(package.Composition, package.Assets);
        var scale = Math.Max(0.001, targetReferenceGridScale ?? remapped.ReferenceGridScale)
            / Math.Max(0.001, remapped.ReferenceGridScale);
        var width = remapped.SourceWidth * scale; var height = remapped.SourceHeight * scale;
        return new SceneObject(ObjectId.New(), SceneObjectKind.Composition, remapped.Name,
            anchorWorldX - remapped.AnchorX * width, anchorWorldY - remapped.AnchorY * height,
            width, height, 0, "#00000000", "#FF8BC7FF", 1, true, false)
        { Composition = remapped };
    }

    public static IReadOnlySet<string> UsedAssetHashes(IEnumerable<SceneObject> objects)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(SceneObject item)
        {
            if (item.Stamp?.AssetSha256 is { } stamp) result.Add(stamp);
            if (item.Stamp?.MaskAssetSha256 is { } mask) result.Add(mask);
            if (item.Line?.Stroke.AssetSha256 is { } line) result.Add(line);
            if (item.Shape?.Stroke.AssetSha256 is { } shape) result.Add(shape);
            if (item.Shape?.Fill.TextureAssetSha256 is { } texture) result.Add(texture);
            if (item.Room?.CapAssetSha256 is { } cap) result.Add(cap);
            if (item.Composition is { } composition)
            {
                foreach (var child in composition.Items) Visit(child);
                foreach (var filterTexture in composition.Filters
                             .Select(filter => filter.TextureAssetSha256)
                             .Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
                {
                    result.Add(filterTexture!);
                }
            }
        }
        foreach (var item in objects) Visit(item);
        return result;
    }

    private static EditableComposition Remap(
        EditableComposition source, IReadOnlyList<MapAssetReference> assets)
    {
        var available = assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SceneObject Rewrite(SceneObject item)
        {
            var copy = VectorGeometry.WithNewIds(item);
            if (copy.Composition is { } nested)
            {
                copy = copy with { Composition = nested with { Items = [.. nested.Items.Select(Rewrite)] } };
            }
            if (copy.Stamp is { } stamp
                && (!available.Contains(stamp.AssetSha256)
                    || stamp.MaskAssetSha256 is { } mask && !available.Contains(mask)))
            {
                copy = SceneObjectClone.Kind(copy, SceneObjectKind.Placeholder,
                    $"Missing pasted asset {stamp.AssetSha256}");
            }
            return copy;
        }
        return source with
        {
            Id = CompositionId.New(), Items = [.. source.Items.Select(Rewrite)],
            Grids = [.. source.Grids.Select(grid => grid with { Id = GridId.New() })],
        };
    }
}

public static class SceneObjectClone
{
    public static SceneObject At(SceneObject source, double x, double y, double? width = null, double? height = null) =>
        Copy(source, source.Kind, x, y, width ?? source.Width, height ?? source.Height, source.PlaceholderReason);

    public static SceneObject Kind(SceneObject source, SceneObjectKind kind, string? reason = null) =>
        Copy(source, kind, source.X, source.Y, source.Width, source.Height, reason);

    private static SceneObject Copy(
        SceneObject source, SceneObjectKind kind, double x, double y, double width, double height, string? reason) =>
        new(source.Id, kind, source.Name, x, y, width, height, source.RotationDegrees,
            source.FillColor, source.StrokeColor, source.StrokeWidth, source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = reason,
            OpaquePayload = kind == SceneObjectKind.Placeholder ? source.OpaquePayload : null,
            ObjectOpacity = source.ObjectOpacity,
        };
}

public sealed class ReplaceObjectsWithCompositionCommand : IEditCommand
{
    private readonly LayerId _layerId; private readonly ObjectLayer _before; private readonly ObjectLayer _after;
    public ReplaceObjectsWithCompositionCommand(MapDocument document, LayerId layerId,
        IReadOnlyCollection<ObjectId> ids, SceneObject composition)
    {
        _layerId = layerId; _before = (ObjectLayer)(document.FindLayer(layerId)
            ?? throw new InvalidOperationException("Composition target layer is absent."));
        var selected = _before.Objects.Where(item => ids.Contains(item.Id)).ToList();
        if (selected.Count == 0 || composition.Composition is null)
            throw new ArgumentException("Editable composition needs selected source objects.");
        var index = _before.Objects.ToList().FindIndex(item => ids.Contains(item.Id));
        var objects = _before.Objects.Where(item => !ids.Contains(item.Id)).ToList();
        objects.Insert(index, composition); _after = _before with { Objects = objects };
    }
    public string Name => "Create editable composition";
    public MapDocument Apply(MapDocument document) => document.WithLayer(_layerId, _ => _after);
    public MapDocument Revert(MapDocument document) => document.WithLayer(_layerId, _ => _before);
}

public sealed class PasteCompositionCommand(
    LayerId layerId, SceneObject instance, IReadOnlyList<MapAssetReference> assets) : IEditCommand
{
    private IReadOnlySet<string> _introduced = new HashSet<string>();
    public string Name => "Paste editable composition";
    public ObjectId InstanceId => instance.Id;
    public MapDocument Apply(MapDocument document)
    {
        _introduced = assets.Where(asset => document.Assets.All(current => current.Sha256 != asset.Sha256))
            .Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = _introduced.Count == 0 ? document : document.WithAssets(
            [.. document.Assets, .. assets.Where(asset => _introduced.Contains(asset.Sha256))]);
        return new AddObjectCommand(layerId, instance).Apply(result);
    }
    public MapDocument Revert(MapDocument document)
    {
        var result = new AddObjectCommand(layerId, instance).Revert(document);
        return _introduced.Count == 0 ? result : result.WithAssets(
            [.. result.Assets.Where(asset => !_introduced.Contains(asset.Sha256))]);
    }
}

public sealed class FlattenCompositionCommand(
    SceneObject source, SceneObject flattened, MapAssetReference asset) : IEditCommand
{
    private bool _introduced;
    public string Name => "Flatten composition";
    public MapDocument Apply(MapDocument document)
    {
        _introduced = document.Assets.All(item => item.Sha256 != asset.Sha256);
        var withAsset = _introduced ? document.WithAssets([.. document.Assets, asset]) : document;
        return new ReplaceObjectsCommand([source], [flattened], Name).Apply(withAsset);
    }
    public MapDocument Revert(MapDocument document)
    {
        var restored = new ReplaceObjectsCommand([source], [flattened], Name).Revert(document);
        return _introduced ? restored.WithAssets([.. restored.Assets.Where(item => item.Sha256 != asset.Sha256)]) : restored;
    }
}

public sealed class UpdateFilterCommand(FilterLayer before, FilterLayer after) : IEditCommand
{
    public string Name => "Edit filter";
    public MapDocument Apply(MapDocument document) => document.WithLayer(before.Id, _ => after);
    public MapDocument Revert(MapDocument document) => document.WithLayer(before.Id, _ => before);
}

public sealed class UpdateFilterWithAssetCommand(
    FilterLayer before, FilterLayer after, MapAssetReference asset) : IEditCommand
{
    private bool _introduced;
    public string Name => "Set filter texture";
    public MapDocument Apply(MapDocument document)
    {
        _introduced = document.Assets.All(item => item.Sha256 != asset.Sha256);
        var withAsset = _introduced ? document.WithAssets([.. document.Assets, asset]) : document;
        return withAsset.WithLayer(before.Id, _ => after);
    }
    public MapDocument Revert(MapDocument document)
    {
        var restored = document.WithLayer(before.Id, _ => before);
        return _introduced ? restored.WithAssets([.. restored.Assets.Where(item => item.Sha256 != asset.Sha256)]) : restored;
    }
}
