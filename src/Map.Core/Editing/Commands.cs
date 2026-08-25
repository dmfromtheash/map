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

/// <summary>Adds one object to the end of one layer. Revert removes it by id.</summary>
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
        DocumentEditor.WithLayer(document, _layerId, layer => new Layer(
            layer.Id, layer.Name, layer.Visible, layer.Locked, layer.Opacity,
            [.. layer.Objects, _added]));

    public MapDocument Revert(MapDocument document) =>
        DocumentEditor.WithLayer(document, _layerId, layer => new Layer(
            layer.Id, layer.Name, layer.Visible, layer.Locked, layer.Opacity,
            [.. layer.Objects.Where(o => o.Id != _added.Id)]));
}

/// <summary>
/// One completed transform gesture (move, resize, rotate or a combination): before and after of
/// exactly one object. Preview during a drag never goes through here - only the final state does,
/// so one gesture is one history entry.
/// </summary>
public sealed class TransformObjectCommand : IEditCommand
{
    private readonly SceneObject _before;
    private readonly SceneObject _after;

    public TransformObjectCommand(SceneObject before, SceneObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (before.Id != after.Id)
        {
            throw new ArgumentException("A transform command must describe one object.", nameof(after));
        }

        _before = before;
        _after = after;
    }

    public string Name => "Transform object";

    public ObjectId ObjectId => _before.Id;

    public MapDocument Apply(MapDocument document) => DocumentEditor.ReplaceObject(document, _after);

    public MapDocument Revert(MapDocument document) => DocumentEditor.ReplaceObject(document, _before);
}

/// <summary>Snapshot surgery shared by the commands.</summary>
public static class DocumentEditor
{
    public static MapDocument WithLayer(MapDocument document, LayerId layerId, Func<Layer, Layer> rewrite)
    {
        var layers = document.Layers.Select(layer => layer.Id == layerId ? rewrite(layer) : layer).ToList();
        if (layers.Zip(document.Layers).All(p => ReferenceEquals(p.First, p.Second)))
        {
            throw new InvalidOperationException($"Layer {layerId} is not part of this document.");
        }

        return new MapDocument(document.Id, document.Title, document.CreatedUtc, document.Canvas, layers);
    }

    public static MapDocument ReplaceObject(MapDocument document, SceneObject replacement)
    {
        var replaced = false;
        var layers = document.Layers.Select(layer =>
        {
            if (layer.Objects.All(o => o.Id != replacement.Id))
            {
                return layer;
            }

            replaced = true;
            return new Layer(
                layer.Id, layer.Name, layer.Visible, layer.Locked, layer.Opacity,
                [.. layer.Objects.Select(o => o.Id == replacement.Id ? replacement : o)]);
        }).ToList();

        if (!replaced)
        {
            throw new InvalidOperationException($"Object {replacement.Id} is not part of this document.");
        }

        return new MapDocument(document.Id, document.Title, document.CreatedUtc, document.Canvas, layers);
    }

    public static SceneObject? FindObject(MapDocument document, ObjectId id) =>
        document.Layers.SelectMany(l => l.Objects).FirstOrDefault(o => o.Id == id);

    public static Layer? FindLayerOf(MapDocument document, ObjectId id) =>
        document.Layers.FirstOrDefault(l => l.Objects.Any(o => o.Id == id));
}
