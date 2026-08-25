using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One reversible edit. Apply and Revert are exact inverses over immutable snapshots: both take a
/// document and return a new one, never mutating anything (AGENTS.md invariant).
/// </summary>
public interface IEditCommand
{
    string Name { get; }

    MapDocument Apply(MapDocument document);

    MapDocument Revert(MapDocument document);
}

// ---------------------------------------------------------------- object commands

/// <summary>Adds one object to the end of one object layer. Revert removes it by id.</summary>
public sealed class AddObjectCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly SceneObject _added;

    public AddObjectCommand(LayerId layerId, SceneObject added)
    {
        ArgumentNullException.ThrowIfNull(added);
        _layerId = layerId;
        _added = added;
    }

    public string Name => $"Add {_added.Kind}";

    public ObjectId AddedObjectId => _added.Id;

    public MapDocument Apply(MapDocument document) =>
        document.WithLayer(_layerId, layer => RequireObjects(layer) with
        {
            Objects = [.. RequireObjects(layer).Objects, _added],
        });

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(_layerId, layer => RequireObjects(layer) with
        {
            Objects = [.. RequireObjects(layer).Objects.Where(o => o.Id != _added.Id)],
        });

    private static ObjectLayer RequireObjects(MapLayer layer) =>
        layer as ObjectLayer
        ?? throw new InvalidOperationException($"Layer {layer.Id} does not hold objects.");
}

/// <summary>Adds many objects as ONE entry - the scatter gesture.</summary>
public sealed class AddObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<SceneObject> _added;

    public AddObjectsCommand(LayerId layerId, IReadOnlyList<SceneObject> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        if (added.Count == 0)
        {
            throw new ArgumentException("A scatter adds at least one object.", nameof(added));
        }

        _layerId = layerId;
        _added = [.. added];
    }

    public string Name => $"Scatter {_added.Count} objects";

    public int Count => _added.Count;

    public MapDocument Apply(MapDocument document) =>
        document.WithLayer(_layerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects, .. _added] };
        });

    public MapDocument Revert(MapDocument document)
    {
        var ids = _added.Select(o => o.Id).ToHashSet();
        return document.WithLayer(_layerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects.Where(o => !ids.Contains(o.Id))] };
        });
    }
}

/// <summary>
/// A stamp placement/scatter plus its project asset reference as one reversible transaction.
/// The reference is removed on undo only when this command introduced it and no remaining stamp
/// uses it; content identity therefore stays stable across redo and repeated placement.
/// </summary>
public sealed class PlaceAssetObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly MapAssetReference _asset;
    private readonly IReadOnlyList<SceneObject> _objects;
    private bool _introducedAsset;

    public PlaceAssetObjectsCommand(
        LayerId layerId, MapAssetReference asset, IReadOnlyList<SceneObject> objects)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0
            || objects.Any(item => item.Kind != SceneObjectKind.Stamp
                || item.Stamp?.AssetSha256 != asset.Sha256))
        {
            throw new ArgumentException("Every placed object must reference the supplied stamp asset.");
        }

        _layerId = layerId;
        _asset = asset;
        _objects = [.. objects];
    }

    public string Name => _objects.Count == 1 ? "Place stamp" : $"Scatter {_objects.Count} stamps";
    public int Count => _objects.Count;

    public MapDocument Apply(MapDocument document)
    {
        _introducedAsset = document.Assets.All(asset => asset.Sha256 != _asset.Sha256);
        var withAsset = _introducedAsset
            ? document.WithAssets([.. document.Assets, _asset])
            : document;
        return withAsset.WithLayer(_layerId, layer =>
        {
            var objects = layer as ObjectLayer
                ?? throw new InvalidOperationException($"Layer {_layerId} does not hold objects.");
            return objects with { Objects = [.. objects.Objects, .. _objects] };
        });
    }

    public MapDocument Revert(MapDocument document)
    {
        var ids = _objects.Select(item => item.Id).ToHashSet();
        var withoutObjects = document.WithLayer(_layerId, layer =>
        {
            var objects = (ObjectLayer)layer;
            return objects with { Objects = [.. objects.Objects.Where(item => !ids.Contains(item.Id))] };
        });

        var stillUsed = withoutObjects.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Any(item => item.Stamp?.AssetSha256 == _asset.Sha256);
        return _introducedAsset && !stillUsed
            ? withoutObjects.WithAssets(
                [.. withoutObjects.Assets.Where(asset => asset.Sha256 != _asset.Sha256)])
            : withoutObjects;
    }
}

/// <summary>Explicit missing-asset replacement: changes the hash and adds metadata in one undo step.</summary>
public sealed class ReplaceStampAssetCommand : IEditCommand
{
    private readonly SceneObject _before;
    private readonly SceneObject _after;
    private readonly MapAssetReference _asset;
    private bool _introducedAsset;

    public ReplaceStampAssetCommand(SceneObject before, MapAssetReference replacement)
    {
        if (before.Stamp is null)
        {
            throw new ArgumentException("The selected object is not a stamp.", nameof(before));
        }

        _before = before;
        _asset = replacement;
        _after = before with
        {
            Stamp = before.Stamp with { AssetSha256 = replacement.Sha256 },
            Name = replacement.DisplayName,
        };
    }

    public string Name => "Replace stamp asset";

    public MapDocument Apply(MapDocument document)
    {
        _introducedAsset = document.Assets.All(asset => asset.Sha256 != _asset.Sha256);
        var withAsset = _introducedAsset
            ? document.WithAssets([.. document.Assets, _asset])
            : document;
        return DocumentEditor.ReplaceObject(withAsset, _after);
    }

    public MapDocument Revert(MapDocument document)
    {
        var reverted = DocumentEditor.ReplaceObject(document, _before);
        var stillUsed = reverted.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
            .Any(item => item.Stamp?.AssetSha256 == _asset.Sha256);
        return _introducedAsset && !stillUsed
            ? reverted.WithAssets([.. reverted.Assets.Where(asset => asset.Sha256 != _asset.Sha256)])
            : reverted;
    }
}

/// <summary>
/// Replaces the captured objects with their edited counterparts, wherever they live. Covers one
/// completed transform/style gesture over one object or a whole group - one history entry.
/// </summary>
public sealed class ReplaceObjectsCommand : IEditCommand
{
    private readonly IReadOnlyList<SceneObject> _before;
    private readonly IReadOnlyList<SceneObject> _after;

    public ReplaceObjectsCommand(IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count == 0 || before.Count != after.Count
            || before.Zip(after).Any(p => p.First.Id != p.Second.Id))
        {
            throw new ArgumentException("Before/after must describe the same objects.", nameof(after));
        }

        _before = [.. before];
        _after = [.. after];
        Name = name;
    }

    public string Name { get; }

    public MapDocument Apply(MapDocument document) => Replace(document, _after);

    public MapDocument Revert(MapDocument document) => Replace(document, _before);

    private static MapDocument Replace(MapDocument document, IReadOnlyList<SceneObject> replacements)
    {
        var byId = replacements.ToDictionary(o => o.Id);
        var layers = document.Layers.Select(layer =>
        {
            if (layer is not ObjectLayer objectLayer
                || !objectLayer.Objects.Any(o => byId.ContainsKey(o.Id)))
            {
                return layer;
            }

            return (MapLayer)(objectLayer with
            {
                Objects = [.. objectLayer.Objects.Select(o => byId.GetValueOrDefault(o.Id, o))],
            });
        }).ToList();

        var replaced = document.WithLayers(layers);
        if (replacements.Any(r => DocumentEditor.FindObject(replaced, r.Id) is null))
        {
            throw new InvalidOperationException("A replaced object is not part of this document.");
        }

        return replaced;
    }
}

/// <summary>Legacy single-object transform wrapper kept for the editor's drag/inspector path.</summary>
public sealed class TransformObjectCommand : IEditCommand
{
    private readonly ReplaceObjectsCommand _inner;

    public TransformObjectCommand(SceneObject before, SceneObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        _inner = new ReplaceObjectsCommand([before], [after], "Transform object");
        ObjectId = before.Id;
    }

    public string Name => _inner.Name;

    public ObjectId ObjectId { get; }

    public MapDocument Apply(MapDocument document) => _inner.Apply(document);

    public MapDocument Revert(MapDocument document) => _inner.Revert(document);
}

/// <summary>Removes objects (delete / move-to-layer source side) as one entry.</summary>
public sealed class RemoveObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<(int Index, SceneObject Object)> _removed;

    public RemoveObjectsCommand(MapDocument document, LayerId layerId, IReadOnlyCollection<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _layerId = layerId;

        var layer = document.FindLayer(layerId) as ObjectLayer
            ?? throw new InvalidOperationException($"Layer {layerId} does not hold objects.");

        _removed = [.. layer.Objects
            .Select((o, i) => (Index: i, Object: o))
            .Where(p => ids.Contains(p.Object.Id))];

        if (_removed.Count != ids.Count)
        {
            throw new InvalidOperationException("Some objects to remove are not on the layer.");
        }
    }

    public string Name => $"Delete {_removed.Count} object(s)";

    public MapDocument Apply(MapDocument document)
    {
        var ids = _removed.Select(r => r.Object.Id).ToHashSet();
        return document.WithLayer(_layerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects.Where(o => !ids.Contains(o.Id))] };
        });
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(_layerId, layer =>
        {
            var objects = ((ObjectLayer)layer).Objects.ToList();
            foreach (var (index, sceneObject) in _removed.OrderBy(r => r.Index))
            {
                objects.Insert(Math.Min(index, objects.Count), sceneObject);
            }

            return ((ObjectLayer)layer) with { Objects = objects };
        });
}

/// <summary>Moves objects from one object layer to the end of another, preserving relative order.</summary>
public sealed class MoveObjectsToLayerCommand : IEditCommand
{
    private readonly RemoveObjectsCommand _remove;
    private readonly LayerId _targetLayerId;
    private readonly IReadOnlyList<SceneObject> _moved;

    public MoveObjectsToLayerCommand(
        MapDocument document, LayerId sourceLayerId, LayerId targetLayerId, IReadOnlyCollection<ObjectId> ids)
    {
        if (sourceLayerId == targetLayerId)
        {
            throw new ArgumentException("Source and target layers are the same.", nameof(targetLayerId));
        }

        if (document.FindLayer(targetLayerId) is not ObjectLayer)
        {
            throw new InvalidOperationException($"Layer {targetLayerId} does not hold objects.");
        }

        _remove = new RemoveObjectsCommand(document, sourceLayerId, ids);
        _targetLayerId = targetLayerId;

        var source = (ObjectLayer)document.FindLayer(sourceLayerId)!;
        _moved = [.. source.Objects.Where(o => ids.Contains(o.Id))];
    }

    public string Name => $"Move {_moved.Count} object(s) to another layer";

    public MapDocument Apply(MapDocument document)
    {
        var removed = _remove.Apply(document);
        return removed.WithLayer(_targetLayerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects, .. _moved] };
        });
    }

    public MapDocument Revert(MapDocument document)
    {
        var ids = _moved.Select(o => o.Id).ToHashSet();
        var withoutTarget = document.WithLayer(_targetLayerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects.Where(o => !ids.Contains(o.Id))] };
        });

        return _remove.Revert(withoutTarget);
    }
}

/// <summary>Assigns (group) or clears (ungroup) a shared group id on the captured objects.</summary>
public sealed class SetGroupCommand : IEditCommand
{
    private readonly ReplaceObjectsCommand _inner;

    private SetGroupCommand(ReplaceObjectsCommand inner) => _inner = inner;

    public static SetGroupCommand Group(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        var groupId = Guid.NewGuid();
        var before = Resolve(document, ids);
        return new SetGroupCommand(new ReplaceObjectsCommand(
            before, [.. before.Select(o => o with { GroupId = groupId })], "Group objects"));
    }

    public static SetGroupCommand Ungroup(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        var before = Resolve(document, ids);
        return new SetGroupCommand(new ReplaceObjectsCommand(
            before, [.. before.Select(o => o with { GroupId = null })], "Ungroup objects"));
    }

    private static IReadOnlyList<SceneObject> Resolve(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        var objects = ids
            .Select(id => DocumentEditor.FindObject(document, id)
                ?? throw new InvalidOperationException($"Object {id} is not part of this document."))
            .ToList();

        if (objects.Count < 1)
        {
            throw new ArgumentException("Grouping needs at least one object.", nameof(ids));
        }

        return objects;
    }

    public string Name => _inner.Name;

    public MapDocument Apply(MapDocument document) => _inner.Apply(document);

    public MapDocument Revert(MapDocument document) => _inner.Revert(document);
}

// ---------------------------------------------------------------- layer commands

/// <summary>Inserts a new layer above the given anchor (or on top when null).</summary>
public sealed class AddLayerCommand : IEditCommand
{
    private readonly MapLayer _layer;
    private readonly LayerId? _aboveLayerId;

    public AddLayerCommand(MapLayer layer, LayerId? aboveLayerId = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer is BaseColorLayer)
        {
            throw new ArgumentException("The base colour layer is unique and cannot be added.", nameof(layer));
        }

        _layer = layer;
        _aboveLayerId = aboveLayerId;
    }

    public string Name => $"Add layer \"{_layer.Name}\"";

    public LayerId LayerId => _layer.Id;

    public MapDocument Apply(MapDocument document)
    {
        var layers = document.Layers.ToList();
        var index = _aboveLayerId is { } anchor
            ? layers.FindIndex(l => l.Id == anchor) + 1
            : layers.Count;

        if (index <= 0)
        {
            index = layers.Count;
        }

        layers.Insert(index, _layer);
        return document.WithLayers(layers);
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _layer.Id)]);
}

/// <summary>Deletes a layer. The base colour layer refuses. Revert restores content and position.</summary>
public sealed class DeleteLayerCommand : IEditCommand
{
    private readonly MapLayer _deleted;
    private readonly int _index;

    public DeleteLayerCommand(MapDocument document, LayerId layerId)
    {
        var index = document.Layers.ToList().FindIndex(l => l.Id == layerId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Layer {layerId} is not part of this document.");
        }

        if (document.Layers[index] is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer cannot be deleted.");
        }

        _deleted = document.Layers[index];
        _index = index;
    }

    public string Name => $"Delete layer \"{_deleted.Name}\"";

    public MapDocument Apply(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _deleted.Id)]);

    public MapDocument Revert(MapDocument document)
    {
        var layers = document.Layers.ToList();
        layers.Insert(Math.Min(_index, layers.Count), _deleted);
        return document.WithLayers(layers);
    }
}

/// <summary>Moves a layer one step up or down. The base colour layer stays at the bottom.</summary>
public sealed class ReorderLayerCommand(LayerId layerId, bool up) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly int _delta = up ? 1 : -1;

    public string Name => "Reorder layer";

    public MapDocument Apply(MapDocument document) => Shift(document, _delta);

    public MapDocument Revert(MapDocument document) => Shift(document, -_delta);

    private MapDocument Shift(MapDocument document, int delta)
    {
        var layers = document.Layers.ToList();
        var index = layers.FindIndex(l => l.Id == _layerId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Layer {_layerId} is not part of this document.");
        }

        if (layers[index] is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer keeps the bottom position.");
        }

        var target = index + delta;
        if (target < 1 || target >= layers.Count)
        {
            return document; // no-op at the edges; Revert of a no-op is a no-op
        }

        (layers[index], layers[target]) = (layers[target], layers[index]);
        return document.WithLayers(layers);
    }
}

/// <summary>Rename / visibility / lock / opacity / colour: one small reversible property change.</summary>
public sealed class UpdateLayerCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Func<MapLayer, MapLayer> _forward;
    private readonly Func<MapLayer, MapLayer> _backward;

    private UpdateLayerCommand(
        string name, LayerId layerId, Func<MapLayer, MapLayer> forward, Func<MapLayer, MapLayer> backward)
    {
        Name = name;
        _layerId = layerId;
        _forward = forward;
        _backward = backward;
    }

    public string Name { get; }

    public static UpdateLayerCommand Rename(MapLayer layer, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var oldName = layer.Name;
        return new UpdateLayerCommand(
            "Rename layer", layer.Id,
            l => Mutate(l, name: newName),
            l => Mutate(l, name: oldName));
    }

    public static UpdateLayerCommand SetVisible(MapLayer layer, bool visible) => new(
        visible ? "Show layer" : "Hide layer", layer.Id,
        l => Mutate(l, visible: visible),
        l => Mutate(l, visible: layer.Visible));

    public static UpdateLayerCommand SetLocked(MapLayer layer, bool locked) => new(
        locked ? "Lock layer" : "Unlock layer", layer.Id,
        l => Mutate(l, locked: locked),
        l => Mutate(l, locked: layer.Locked));

    public static UpdateLayerCommand SetOpacity(MapLayer layer, double opacity) => new(
        "Layer opacity", layer.Id,
        l => Mutate(l, opacity: opacity),
        l => Mutate(l, opacity: layer.Opacity));

    public static UpdateLayerCommand SetBlend(MapLayer layer, string blend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blend);
        return new UpdateLayerCommand(
            "Layer blend", layer.Id,
            l => Mutate(l, blend: blend),
            l => Mutate(l, blend: layer.Blend));
    }

    public static UpdateLayerCommand SetBaseColor(BaseColorLayer layer, string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        var oldColor = layer.Color;
        return new UpdateLayerCommand(
            "Background colour", layer.Id,
            l => ((BaseColorLayer)l) with { Color = color },
            l => ((BaseColorLayer)l) with { Color = oldColor });
    }

    private static MapLayer Mutate(
        MapLayer layer,
        string? name = null,
        bool? visible = null,
        bool? locked = null,
        double? opacity = null,
        string? blend = null)
        => layer switch
        {
            BaseColorLayer b => b with
            {
                Name = name ?? b.Name,
                Visible = visible ?? b.Visible,
                Locked = locked ?? b.Locked,
                Opacity = opacity ?? b.Opacity,
                Blend = blend ?? b.Blend,
            },
            ObjectLayer o => o with
            {
                Name = name ?? o.Name,
                Visible = visible ?? o.Visible,
                Locked = locked ?? o.Locked,
                Opacity = opacity ?? o.Opacity,
                Blend = blend ?? o.Blend,
            },
            BrushLayer br => br with
            {
                Name = name ?? br.Name,
                Visible = visible ?? br.Visible,
                Locked = locked ?? br.Locked,
                Opacity = opacity ?? br.Opacity,
                Blend = blend ?? br.Blend,
            },
            LandLayer land => land with
            {
                Name = name ?? land.Name,
                Visible = visible ?? land.Visible,
                Locked = locked ?? land.Locked,
                Opacity = opacity ?? land.Opacity,
                Blend = blend ?? land.Blend,
            },
            GridLayer grid => grid with
            {
                Name = name ?? grid.Name,
                Visible = visible ?? grid.Visible,
                Locked = locked ?? grid.Locked,
                Opacity = opacity ?? grid.Opacity,
                Blend = blend ?? grid.Blend,
            },
            FilterLayer filter => filter with
            {
                Name = name ?? filter.Name,
                Visible = visible ?? filter.Visible,
                Locked = locked ?? filter.Locked,
                Opacity = opacity ?? filter.Opacity,
                Blend = blend ?? filter.Blend,
            },
            _ => throw new InvalidOperationException($"Unknown layer type {layer.GetType().Name}."),
        };

    public MapDocument Apply(MapDocument document) => document.WithLayer(_layerId, _forward);

    public MapDocument Revert(MapDocument document) => document.WithLayer(_layerId, _backward);
}

/// <summary>Duplicates a layer (fresh ids) right above the original.</summary>
public sealed class DuplicateLayerCommand(LayerId sourceId) : IEditCommand
{
    private readonly LayerId _sourceId = sourceId;
    private MapLayer? _copy;

    public string Name => "Duplicate layer";

    public LayerId? CopyLayerId => _copy?.Id;

    public MapDocument Apply(MapDocument document)
    {
        var source = document.FindLayer(_sourceId)
            ?? throw new InvalidOperationException($"Layer {_sourceId} is not part of this document.");

        if (source is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer is unique.");
        }

        // The copy's ids are minted once so redo restores the same identities.
        _copy ??= source.WithNewIds() with { Name = source.Name + " (копия)" };

        var layers = document.Layers.ToList();
        layers.Insert(layers.FindIndex(l => l.Id == _sourceId) + 1, _copy);
        return document.WithLayers(layers);
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _copy!.Id)]);
}

// ---------------------------------------------------------------- tile commands

/// <summary>
/// One completed raster gesture over one brush layer: before/after of ONLY the touched tiles
/// (docs/05 raster rule) - never a full-canvas snapshot.
/// </summary>
public sealed class BrushStrokeCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after,
    string name) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before = before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after = after;

    public string Name { get; } = name;

    public int TouchedTileCount => _before.Count;

    public long ApproximateMemoryBytes =>
        _before.Values.Sum(v => (long)(v?.Length ?? 0)) + _after.Values.Sum(v => (long)(v?.Length ?? 0));

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(_layerId, layer => layer is IRasterContentLayer raster
            ? raster.WithContentTiles(raster.ContentTiles.WithMany(tiles))
            : throw new InvalidOperationException($"Layer {_layerId} holds no raster tiles."));
}

/// <summary>One completed mask gesture: before/after of the touched mask tiles only.</summary>
public sealed class MaskStrokeCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before = before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after = after;

    public string Name => "Mask stroke";

    public int TouchedTileCount => _before.Count;

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(_layerId, layer =>
        {
            var mask = layer.Mask
                ?? throw new InvalidOperationException($"Layer {_layerId} has no mask.");
            return WithMask(layer, mask with { Tiles = mask.Tiles.WithMany(tiles) });
        });

    internal static MapLayer WithMask(MapLayer layer, LayerMask? mask) => layer.WithMask(mask);
}

/// <summary>Add, remove, enable/disable or copy a layer mask - one reversible step each.</summary>
public sealed class UpdateMaskCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly LayerMask? _before;
    private readonly LayerMask? _after;

    private UpdateMaskCommand(string name, LayerId layerId, LayerMask? before, LayerMask? after)
    {
        Name = name;
        _layerId = layerId;
        _before = before;
        _after = after;
    }

    public string Name { get; }

    public static UpdateMaskCommand AddMask(MapLayer layer) =>
        new("Add mask", layer.Id, layer.Mask, LayerMask.CreateEmpty());

    public static UpdateMaskCommand RemoveMask(MapLayer layer) =>
        new("Remove mask", layer.Id, layer.Mask, null);

    public static UpdateMaskCommand SetEnabled(MapLayer layer, bool enabled)
    {
        var mask = layer.Mask ?? throw new InvalidOperationException("The layer has no mask.");
        return new UpdateMaskCommand(
            enabled ? "Enable mask" : "Disable mask", layer.Id, mask, mask with { Enabled = enabled });
    }

    /// <summary>Copies the source layer's mask onto the target layer as the target's own mask.</summary>
    public static UpdateMaskCommand CopyMask(MapLayer source, MapLayer target)
    {
        var mask = source.Mask ?? throw new InvalidOperationException("The source layer has no mask.");
        return new UpdateMaskCommand("Copy mask", target.Id, target.Mask, mask);
    }

    public MapDocument Apply(MapDocument document) =>
        document.WithLayer(_layerId, layer => MaskStrokeCommand.WithMask(layer, _after));

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(_layerId, layer => MaskStrokeCommand.WithMask(layer, _before));
}

/// <summary>Changes evaluated coast effects without touching the source coverage tiles.</summary>
public sealed class UpdateLandStyleCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly LandEdgeStyle _before;
    private readonly LandEdgeStyle _after;

    public UpdateLandStyleCommand(LandLayer layer, LandEdgeStyle after)
    {
        _layerId = layer.Id;
        _before = layer.EdgeStyle;
        _after = after;
    }

    public string Name => "Land edge style";

    public MapDocument Apply(MapDocument document) => Write(document, _after);
    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, LandEdgeStyle style) =>
        document.WithLayer(_layerId, layer => layer is LandLayer land
            ? land with { EdgeStyle = style }
            : throw new InvalidOperationException($"Layer {_layerId} is not land."));
}

// ---------------------------------------------------------------- helpers

/// <summary>Snapshot lookups shared by commands and the editor.</summary>
public static class DocumentEditor
{
    public static SceneObject? FindObject(MapDocument document, ObjectId id) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).FirstOrDefault(o => o.Id == id);

    public static ObjectLayer? FindLayerOf(MapDocument document, ObjectId id) =>
        document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.Objects.Any(o => o.Id == id));

    /// <summary>Replaces one object wherever it lives - preview substitution for the canvas.</summary>
    public static MapDocument ReplaceObject(MapDocument document, SceneObject replacement)
    {
        var layer = FindLayerOf(document, replacement.Id)
            ?? throw new InvalidOperationException($"Object {replacement.Id} is not part of this document.");

        return document.WithLayer(layer.Id, l =>
        {
            var objectLayer = (ObjectLayer)l;
            return objectLayer with
            {
                Objects = [.. objectLayer.Objects.Select(o => o.Id == replacement.Id ? replacement : o)],
            };
        });
    }
}
