using Map.Core.Documents;

namespace Map.Core.Editing;

public enum ShapeBooleanOperation { Add, Subtract, Merge }

/// <summary>One vector cut replaces one object with multiple pieces and undoes as one step.</summary>
public sealed class ReplaceObjectWithManyCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly int _index;
    private readonly SceneObject _before;
    private readonly IReadOnlyList<SceneObject> _after;

    public ReplaceObjectWithManyCommand(
        MapDocument document, LayerId layerId, ObjectId beforeId, IReadOnlyList<SceneObject> after)
    {
        var layer = document.FindLayer(layerId) as ObjectLayer
            ?? throw new InvalidOperationException("Vector cut requires an object layer.");
        _index = layer.Objects.ToList().FindIndex(item => item.Id == beforeId);
        if (_index < 0 || after.Count < 2) throw new ArgumentException("A cut replaces one object with at least two pieces.");
        _layerId = layerId; _before = layer.Objects[_index]; _after = [.. after];
    }

    public string Name => "Cut vector at node";
    public MapDocument Apply(MapDocument document) => Rewrite(document, [_before.Id], _after);
    public MapDocument Revert(MapDocument document) => Rewrite(document, [.. _after.Select(item => item.Id)], [_before]);

    private MapDocument Rewrite(
        MapDocument document, IReadOnlyCollection<ObjectId> removed, IReadOnlyList<SceneObject> inserted) =>
        document.WithLayer(_layerId, layer =>
        {
            var objects = ((ObjectLayer)layer).Objects.Where(item => !removed.Contains(item.Id)).ToList();
            objects.InsertRange(Math.Min(_index, objects.Count), inserted);
            return ((ObjectLayer)layer) with { Objects = objects };
        });
}

/// <summary>Attaches a content-addressed asset to a vector style in the same undo transaction.</summary>
public sealed class VectorAssetStyleCommand(
    SceneObject before, SceneObject after, MapAssetReference asset) : IEditCommand
{
    private bool _introduced;
    public string Name => "Use asset stroke";

    public MapDocument Apply(MapDocument document)
    {
        _introduced = document.Assets.All(item => item.Sha256 != asset.Sha256);
        var withAsset = _introduced ? document.WithAssets([.. document.Assets, asset]) : document;
        return DocumentEditor.ReplaceObject(withAsset, after);
    }

    public MapDocument Revert(MapDocument document)
    {
        var reverted = DocumentEditor.ReplaceObject(document, before);
        var used = reverted.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).Any(item =>
            item.Stamp?.AssetSha256 == asset.Sha256
            || item.Line?.Stroke.AssetSha256 == asset.Sha256
            || item.Shape?.Stroke.AssetSha256 == asset.Sha256
            || item.Shape?.Fill.TextureAssetSha256 == asset.Sha256);
        return _introduced && !used
            ? reverted.WithAssets([.. reverted.Assets.Where(item => item.Sha256 != asset.Sha256)])
            : reverted;
    }
}

/// <summary>Pure immutable edits for stable vector nodes and portals.</summary>
public static class VectorObjectEditor
{
    public static SceneObject MoveNode(SceneObject source, VectorNodeId id, double localX, double localY)
    {
        if (!double.IsFinite(localX) || !double.IsFinite(localY))
        {
            throw new ArgumentOutOfRangeException(nameof(localX));
        }

        var moved = false;
        VectorNode Rewrite(VectorNode node)
        {
            if (node.Id != id) return node;
            moved = true;
            return node with { X = localX, Y = localY };
        }

        var result = source.Line is { } line
            ? source with { Line = new LineStyle([.. line.Nodes.Select(Rewrite)], line.Stroke, line.Portals, line.Closed) }
            : source.Shape is { } shape
                ? source with
                {
                    Shape = new ShapeStyle(
                        [.. shape.Regions.Select(region => new ShapeRegion(
                            region.Id, region.Mode, region.Primitive, [.. region.Nodes.Select(Rewrite)]))],
                        shape.Fill, shape.Stroke, shape.Portals),
                }
                : throw new ArgumentException("The object is not editable vector geometry.", nameof(source));
        return moved ? result : throw new InvalidOperationException($"Node {id} is not part of the object.");
    }

    public static SceneObject AddLineNode(SceneObject source, double localX, double localY)
    {
        var line = source.Line ?? throw new ArgumentException("The object is not a line.", nameof(source));
        return source with
        {
            Line = new LineStyle(
                [.. line.Nodes, new VectorNode(VectorNodeId.New(), localX, localY)],
                line.Stroke, line.Portals, line.Closed),
        };
    }

    public static SceneObject DeleteLineNode(SceneObject source, VectorNodeId id)
    {
        var line = source.Line ?? throw new ArgumentException("The object is not a line.", nameof(source));
        var nodes = line.Nodes.Where(node => node.Id != id).ToList();
        if (nodes.Count == line.Nodes.Count) throw new InvalidOperationException($"Node {id} is absent.");
        return source with { Line = new LineStyle(nodes, line.Stroke, line.Portals, line.Closed) };
    }

    public static SceneObject SetNodeCurve(SceneObject source, VectorNodeId id, bool curved)
    {
        VectorNode Rewrite(VectorNode node) => node.Id == id
            ? node with
            {
                Kind = curved ? VectorNodeKind.Curve : VectorNodeKind.Corner,
                ControlInX = curved ? -0.08 : 0,
                ControlOutX = curved ? 0.08 : 0,
            }
            : node;
        var line = source.Line ?? throw new ArgumentException("The object is not a line.", nameof(source));
        if (line.Nodes.All(node => node.Id != id)) throw new InvalidOperationException($"Node {id} is absent.");
        return source with { Line = new LineStyle([.. line.Nodes.Select(Rewrite)], line.Stroke, line.Portals, line.Closed) };
    }

    public static SceneObject AddPortal(SceneObject source, double position, double width)
    {
        var portal = new LinePortal(PortalId.New(), position, width);
        return source.Line is { } line
            ? source with { Line = new LineStyle(line.Nodes, line.Stroke, [.. line.Portals, portal], line.Closed) }
            : source.Shape is { } shape
                ? source with { Shape = new ShapeStyle(shape.Regions, shape.Fill, shape.Stroke, [.. shape.Portals, portal]) }
                : throw new ArgumentException("Only line and shape strokes have portals.", nameof(source));
    }

    public static SceneObject MovePortal(SceneObject source, PortalId id, double position)
    {
        if (position is < 0 or > 1 || !double.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        var found = false;
        LinePortal Rewrite(LinePortal portal)
        {
            if (portal.Id != id) return portal;
            found = true;
            return portal with { Position = position };
        }

        var result = source.Line is { } line
            ? source with { Line = new LineStyle(line.Nodes, line.Stroke, [.. line.Portals.Select(Rewrite)], line.Closed) }
            : source.Shape is { } shape
                ? source with { Shape = new ShapeStyle(shape.Regions, shape.Fill, shape.Stroke, [.. shape.Portals.Select(Rewrite)]) }
                : throw new ArgumentException("Only line and shape strokes have portals.", nameof(source));
        return found ? result : throw new InvalidOperationException($"Portal {id} is absent.");
    }

    public static SceneObject DeletePortal(SceneObject source, PortalId id)
    {
        if (source.Line is { } line)
        {
            var portals = line.Portals.Where(p => p.Id != id).ToList();
            if (portals.Count == line.Portals.Count) throw new InvalidOperationException($"Portal {id} is absent.");
            return source with { Line = new LineStyle(line.Nodes, line.Stroke, portals, line.Closed) };
        }

        if (source.Shape is { } shape)
        {
            var portals = shape.Portals.Where(p => p.Id != id).ToList();
            if (portals.Count == shape.Portals.Count) throw new InvalidOperationException($"Portal {id} is absent.");
            return source with { Shape = new ShapeStyle(shape.Regions, shape.Fill, shape.Stroke, portals) };
        }

        throw new ArgumentException("Only line and shape strokes have portals.", nameof(source));
    }
}

/// <summary>
/// Nondestructive boolean: operands become ordered add/subtract regions. No intersection vertices
/// are manufactured, so touching and near-collinear input cannot create invalid rings.
/// </summary>
public sealed class ShapeBooleanCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly SceneObject _first;
    private readonly SceneObject _second;
    private readonly SceneObject _result;
    private readonly int _firstIndex;
    private readonly int _secondIndex;

    public ShapeBooleanCommand(
        MapDocument document, LayerId layerId, ObjectId firstId, ObjectId secondId,
        ShapeBooleanOperation operation)
    {
        var layer = document.FindLayer(layerId) as ObjectLayer
            ?? throw new InvalidOperationException("Boolean operands must share an object layer.");
        _layerId = layerId;
        _firstIndex = IndexOf(layer, firstId); _secondIndex = IndexOf(layer, secondId);
        _first = layer.Objects[_firstIndex]; _second = layer.Objects[_secondIndex];
        if (_first.Shape is null || _second.Shape is null || firstId == secondId)
        {
            throw new ArgumentException("Boolean operands are two distinct shapes.");
        }

        _result = Combine(_first, _second, operation);
    }

    public string Name => "Shape boolean";
    public ObjectId ResultId => _result.Id;

    public MapDocument Apply(MapDocument document) => document.WithLayer(_layerId, layer =>
    {
        var objects = ((ObjectLayer)layer).Objects
            .Where(item => item.Id != _first.Id && item.Id != _second.Id).ToList();
        objects.Insert(Math.Min(Math.Min(_firstIndex, _secondIndex), objects.Count), _result);
        return ((ObjectLayer)layer) with { Objects = objects };
    });

    public MapDocument Revert(MapDocument document) => document.WithLayer(_layerId, layer =>
    {
        var objects = ((ObjectLayer)layer).Objects.Where(item => item.Id != _result.Id).ToList();
        foreach (var item in new[] { (_firstIndex, _first), (_secondIndex, _second) }.OrderBy(item => item.Item1))
        {
            Insert(objects, item.Item1, item.Item2);
        }
        return ((ObjectLayer)layer) with { Objects = objects };
    });

    private static SceneObject Combine(
        SceneObject first, SceneObject second, ShapeBooleanOperation operation)
    {
        var firstBounds = SceneGeometry.BoundsOf(first);
        var secondBounds = SceneGeometry.BoundsOf(second);
        var bounds = new WorldBounds(
            Math.Min(firstBounds.Left, secondBounds.Left), Math.Min(firstBounds.Top, secondBounds.Top),
            Math.Max(firstBounds.Right, secondBounds.Right), Math.Max(firstBounds.Bottom, secondBounds.Bottom));
        var width = Math.Max(0.001, bounds.Right - bounds.Left);
        var height = Math.Max(0.001, bounds.Bottom - bounds.Top);
        var regions = new List<ShapeRegion>();
        regions.AddRange(Remap(first, first.Shape!.Regions, bounds, width, height, null));
        regions.AddRange(Remap(
            second, second.Shape!.Regions, bounds, width, height,
            operation == ShapeBooleanOperation.Subtract ? ShapeCombineMode.Subtract : ShapeCombineMode.Add));

        Validate(regions);
        return new SceneObject(
            ObjectId.New(), SceneObjectKind.Shape, $"{first.Name} boolean {second.Name}",
            bounds.Left, bounds.Top, width, height, 0,
            first.FillColor, first.StrokeColor, first.StrokeWidth, true, false)
        {
            Sublayer = Math.Max(first.Sublayer, second.Sublayer),
            Shape = new ShapeStyle(regions, first.Shape.Fill, first.Shape.Stroke),
            ObjectOpacity = first.ObjectOpacity,
        };
    }

    public static void Validate(IReadOnlyList<ShapeRegion> regions)
    {
        if (regions.Count == 0 || regions[0].Mode != ShapeCombineMode.Add)
        {
            throw new InvalidOperationException("Boolean expression starts with an additive region.");
        }

        foreach (var region in regions)
        {
            if (region.Nodes.Any(n => !double.IsFinite(n.X) || !double.IsFinite(n.Y))
                || region.Nodes.Zip(region.Nodes.Skip(1)).Any(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y))
            {
                throw new InvalidOperationException("Boolean expression contains invalid vector geometry.");
            }
        }
    }

    private static IEnumerable<ShapeRegion> Remap(
        SceneObject owner, IReadOnlyList<ShapeRegion> source, WorldBounds result,
        double width, double height, ShapeCombineMode? forceMode)
    {
        foreach (var region in source)
        {
            IReadOnlyList<VectorPoint> points = region.Primitive == ShapePrimitiveKind.Ellipse
                ? SampleEllipse(owner, region.Nodes)
                : VectorGeometry.Flatten(owner, region.Nodes, closed: true);
            points = [.. points.Select(point => Rotate(owner, point))];
            var nodes = points.Take(points.Count - 1).Select(point => new VectorNode(
                VectorNodeId.New(), (point.X - result.Left) / width, (point.Y - result.Top) / height)).ToList();
            yield return new ShapeRegion(
                region.Id, forceMode ?? region.Mode, ShapePrimitiveKind.Polygon, nodes);
        }
    }

    private static IReadOnlyList<VectorPoint> SampleEllipse(SceneObject owner, IReadOnlyList<VectorNode> nodes)
    {
        var a = nodes[0]; var b = nodes[1];
        var cx = (a.X + b.X) / 2; var cy = (a.Y + b.Y) / 2;
        var rx = Math.Abs(b.X - a.X) / 2; var ry = Math.Abs(b.Y - a.Y) / 2;
        var points = new List<VectorPoint>();
        for (var index = 0; index <= 32; index++)
        {
            var angle = index * Math.PI * 2 / 32;
            points.Add(new VectorPoint(
                owner.X + ((cx + Math.Cos(angle) * rx) * owner.Width),
                owner.Y + ((cy + Math.Sin(angle) * ry) * owner.Height)));
        }
        return points;
    }

    private static VectorPoint Rotate(SceneObject owner, VectorPoint point)
    {
        if (owner.RotationDegrees == 0) return point;
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var radians = owner.RotationDegrees * Math.PI / 180;
        var dx = point.X - cx; var dy = point.Y - cy;
        return new VectorPoint(
            cx + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            cy + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    private static int IndexOf(ObjectLayer layer, ObjectId id)
    {
        for (var i = 0; i < layer.Objects.Count; i++) if (layer.Objects[i].Id == id) return i;
        throw new InvalidOperationException($"Object {id} is not on the layer.");
    }

    private static void Insert(List<SceneObject> list, int index, SceneObject item) =>
        list.Insert(Math.Min(index, list.Count), item);
}
