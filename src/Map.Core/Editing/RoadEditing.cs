using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Pure immutable edits of a road network; every result still has to pass <see cref="Road.Validate"/>.
/// Nodes are shared: an edit of a node reaches every branch through it, an edit of a branch
/// leaves the other branches and their vertices untouched.
/// </summary>
public static class RoadEditor
{
    public static Road ReplaceBranch(Road road, RoadBranch branch) =>
        road with { Branches = [.. road.Branches.Select(b => b.Id == branch.Id ? branch : b)] };

    private static RoadBranch RequireBranch(Road road, Guid branchId) =>
        road.FindBranch(branchId) ?? throw new InvalidOperationException("Участок дороги не найден.");

    private static RoadNode RequireNode(Road road, Guid nodeId) =>
        road.FindNode(nodeId) ?? throw new InvalidOperationException("Узел дороги не найден.");

    /// <summary>Drops the nodes no branch uses any more.</summary>
    public static Road Prune(Road road)
    {
        var used = road.Branches.SelectMany(b => b.Vertices).Select(v => v.NodeId).ToHashSet();
        return used.Count == road.Nodes.Length ? road : road with { Nodes = [.. road.Nodes.Where(n => used.Contains(n.Id))] };
    }

    /// <summary>A road of one open branch through new nodes at the given points, all of one width.</summary>
    public static Road NewRoad(IReadOnlyList<VectorPoint> points, double width, RoadStyle? style = null, string name = "Дорога", int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        var nodes = points.Select(p => new RoadNode(Guid.NewGuid(), p.X, p.Y)).ToImmutableArray();
        var branch = new RoadBranch { Name = "Участок 1", Vertices = [.. nodes.Select(n => new RoadVertex(n.Id, width))] };
        var road = new Road { Name = name, Nodes = nodes, Branches = [branch], Style = style ?? new RoadStyle() };
        return road with { Seed = seed ?? road.Id.GetHashCode() };
    }

    public static Road MoveNode(Road road, Guid nodeId, double x, double y)
    {
        RequireNode(road, nodeId);
        return road with { Nodes = [.. road.Nodes.Select(n => n.Id == nodeId ? n with { X = x, Y = y } : n)] };
    }

    public static Road Translate(Road road, double dx, double dy) => road with
    {
        Nodes = [.. road.Nodes.Select(n => n with { X = n.X + dx, Y = n.Y + dy })],
    };

    public static Road SetVertexWidth(Road road, Guid branchId, int index, double width)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Vertices = branch.Vertices.SetItem(index, branch.Vertices[index] with { Width = width }) });
    }

    /// <summary>One width for the whole branch, every vertex alike.</summary>
    public static Road SetBranchWidth(Road road, Guid branchId, double width)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Vertices = [.. branch.Vertices.Select(v => v with { Width = width })] });
    }

    public static Road SetWidthProfile(Road road, Guid branchId, double startWidth, double endWidth)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Vertices = RoadGeometry.WidthProfile(road, branch, startWidth, endWidth) });
    }

    public static Road SetVertexSharp(Road road, Guid branchId, int index, bool sharp)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Vertices = branch.Vertices.SetItem(index, branch.Vertices[index] with { Sharp = sharp }) });
    }

    public static Road SetVertexTension(Road road, Guid branchId, int index, double tension)
    {
        var branch = RequireBranch(road, branchId);
        var clamped = Math.Clamp(tension, RoadVertex.MinimumTension, RoadVertex.MaximumTension);
        return ReplaceBranch(road, branch with
        { Vertices = branch.Vertices.SetItem(index, branch.Vertices[index] with { Tension = clamped, Sharp = clamped <= 0 && branch.Vertices[index].Sharp }) });
    }

    /// <summary>One smoothness dial for the whole branch.</summary>
    public static Road SetBranchTension(Road road, Guid branchId, double tension)
    {
        var branch = RequireBranch(road, branchId);
        var clamped = Math.Clamp(tension, RoadVertex.MinimumTension, RoadVertex.MaximumTension);
        return ReplaceBranch(road, branch with { Vertices = [.. branch.Vertices.Select(v => v with { Tension = clamped })] });
    }

    /// <summary>The branch's own look, or null to draw it like the road again.</summary>
    public static Road SetBranchStyle(Road road, Guid branchId, RoadStyle? style)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Style = style });
    }

    public static Road SetBranchName(Road road, Guid branchId, string name)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Name = name });
    }

    /// <summary>
    /// A new shared node on a segment of a branch, at the interpolated width. This is how a
    /// branch is attached to the middle of an existing stretch: the node is real and editable.
    /// </summary>
    public static Road InsertVertex(Road road, Guid branchId, int segment, double fraction, VectorPoint point, out Guid nodeId)
    {
        var branch = RequireBranch(road, branchId);
        var geometry = RoadGeometry.Evaluate(road).Branches[branchId];
        var width = RoadGeometry.WidthAt(geometry, Math.Max(0, segment), fraction) / Math.Max(0.01, road.WidthScale);
        var node = new RoadNode(Guid.NewGuid(), point.X, point.Y);
        nodeId = node.Id;
        var at = Math.Clamp(segment + 1, 1, branch.Vertices.Length);
        if (branch.Closed && segment >= branch.Vertices.Length - 1) at = branch.Vertices.Length;
        var vertex = new RoadVertex(node.Id, Math.Clamp(width, Road.MinimumWidth, Road.MaximumWidth));
        return ReplaceBranch(road with { Nodes = road.Nodes.Add(node) }, branch with { Vertices = branch.Vertices.Insert(at, vertex) });
    }

    /// <summary>Removes one vertex from a branch; a node no branch uses any more is dropped with it.</summary>
    public static Road DeleteVertex(Road road, Guid branchId, int index)
    {
        var branch = RequireBranch(road, branchId);
        if (branch.Vertices.Length <= (branch.Closed ? 3 : 2))
            throw new InvalidOperationException(branch.Closed ? "У замкнутого участка должно остаться три узла." : "Оставьте два узла или удалите весь участок.");
        var vertices = branch.Vertices.RemoveAt(index);
        for (var i = 1; i < vertices.Length; i++)
            if (vertices[i].NodeId == vertices[i - 1].NodeId)
                throw new InvalidOperationException("После удаления участок дважды прошёл бы через один узел.");
        if (branch.Closed && vertices[0].NodeId == vertices[^1].NodeId)
            throw new InvalidOperationException("После удаления замкнутый участок повторил бы первый узел.");
        return Prune(ReplaceBranch(road, branch with { Vertices = vertices }));
    }

    /// <summary>
    /// A new branch: it starts at an existing node or at a new one, walks through new nodes at
    /// the given points, and ends at an existing node or at a new one. Every vertex takes one width.
    /// </summary>
    public static Road AddBranch(Road road, Guid? startNodeId, IReadOnlyList<VectorPoint> middle, Guid? endNodeId, double width,
        out Guid branchId, string? name = null, RoadStyle? style = null, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(middle);
        var nodes = road.Nodes.ToBuilder();
        var vertices = ImmutableArray.CreateBuilder<RoadVertex>();
        if (startNodeId is { } start)
        {
            RequireNode(road, start);
            vertices.Add(new RoadVertex(start, width));
        }
        foreach (var point in middle)
        {
            var node = new RoadNode(Guid.NewGuid(), point.X, point.Y);
            nodes.Add(node);
            vertices.Add(new RoadVertex(node.Id, width));
        }
        if (endNodeId is { } end)
        {
            RequireNode(road, end);
            if (vertices.Count > 0 && vertices[^1].NodeId == end) throw new InvalidOperationException("Участок не может дважды подряд пройти через один узел.");
            vertices.Add(new RoadVertex(end, width));
        }
        var branch = new RoadBranch
        {
            Name = name ?? $"Участок {road.Branches.Length + 1}", Vertices = vertices.ToImmutable(), Style = style, Closed = closed,
        };
        branchId = branch.Id;
        return road with { Nodes = nodes.ToImmutable(), Branches = road.Branches.Add(branch) };
    }

    /// <summary>
    /// Replaces the node an open branch ends on: the end now shares the given node. The old node
    /// is dropped when nothing else uses it. Joining a branch to an existing road goes through this.
    /// </summary>
    public static Road JoinEnd(Road road, Guid branchId, bool atEnd, Guid nodeId)
    {
        var branch = RequireBranch(road, branchId);
        if (branch.Closed) throw new InvalidOperationException("Замкнутый участок не присоединяется концом.");
        RequireNode(road, nodeId);
        var index = atEnd ? branch.Vertices.Length - 1 : 0;
        var neighbour = branch.Vertices[atEnd ? index - 1 : 1];
        if (neighbour.NodeId == nodeId) throw new InvalidOperationException("Участок не может дважды подряд пройти через один узел.");
        var vertices = branch.Vertices.SetItem(index, branch.Vertices[index] with { NodeId = nodeId });
        return Prune(ReplaceBranch(road, branch with { Vertices = vertices }));
    }

    /// <summary>
    /// Extends an open branch by one vertex on an existing node: how a drafted course is joined to
    /// a road it was drawn towards. The branch now ends on the shared node.
    /// </summary>
    public static Road AppendVertex(Road road, Guid branchId, Guid nodeId, double width)
    {
        var branch = RequireBranch(road, branchId);
        if (branch.Closed) throw new InvalidOperationException("Замкнутый участок не продолжается.");
        RequireNode(road, nodeId);
        if (branch.Vertices.Length > 0 && branch.Vertices[^1].NodeId == nodeId)
            throw new InvalidOperationException("Участок не может дважды подряд пройти через один узел.");
        return ReplaceBranch(road, branch with { Vertices = branch.Vertices.Add(new RoadVertex(nodeId, width)) });
    }

    /// <summary>Splits an open branch at an interior vertex into two branches sharing that node; a loop opens there.</summary>
    public static Road SplitBranch(Road road, Guid branchId, int index, out Guid secondId)
    {
        var branch = RequireBranch(road, branchId);
        if (branch.Closed)
        {
            if (index < 0 || index >= branch.Vertices.Length) throw new InvalidOperationException("Нет такого узла.");
            var rotated = branch.Vertices.Skip(index).Concat(branch.Vertices.Take(index)).ToList();
            rotated.Add(rotated[0]);
            secondId = branch.Id;
            return ReplaceBranch(road, branch with { Closed = false, Vertices = [.. rotated] });
        }
        if (index <= 0 || index >= branch.Vertices.Length - 1) throw new InvalidOperationException("Разделить можно только во внутреннем узле.");
        var second = new RoadBranch
        {
            Name = branch.Name + " · далее", Vertices = [.. branch.Vertices.Skip(index)], Style = branch.Style,
        };
        secondId = second.Id;
        var first = branch with { Vertices = [.. branch.Vertices.Take(index + 1)] };
        var branches = road.Branches.Select(b => b.Id == branchId ? first : b).ToList();
        branches.Insert(branches.IndexOf(first) + 1, second);
        return road with { Branches = [.. branches] };
    }

    /// <summary>
    /// Takes one vertex of a branch off the shared node: the branch gets its own node at the same
    /// place, the other branches keep the old one. This is the explicit "disconnect" of a fork.
    /// </summary>
    public static Road DetachVertex(Road road, Guid branchId, int index)
    {
        var branch = RequireBranch(road, branchId);
        var vertex = branch.Vertices[index];
        if (!road.IsShared(vertex.NodeId) && branch.Vertices.Count(v => v.NodeId == vertex.NodeId) == 1) return road;
        var old = RequireNode(road, vertex.NodeId);
        var node = new RoadNode(Guid.NewGuid(), old.X, old.Y);
        return Prune(ReplaceBranch(road with { Nodes = road.Nodes.Add(node) },
            branch with { Vertices = branch.Vertices.SetItem(index, vertex with { NodeId = node.Id }) }));
    }

    /// <summary>Removes one branch; nodes used only by it go with it. The last branch cannot be removed here.</summary>
    public static Road DeleteBranch(Road road, Guid branchId)
    {
        if (road.Branches.Length <= 1) throw new InvalidOperationException("Удалите всю дорогу целиком.");
        RequireBranch(road, branchId);
        return Prune(road with { Branches = [.. road.Branches.Where(b => b.Id != branchId)] });
    }

    /// <summary>
    /// Moves one branch into a new independent road with copies of the nodes it used. Shared
    /// nodes stay with the remaining road; nothing keeps a hidden link between the two.
    /// </summary>
    public static (Road Remaining, Road Detached) DetachBranchAsRoad(Road road, Guid branchId, string detachedName)
    {
        var branch = RequireBranch(road, branchId);
        if (road.Branches.Length <= 1) throw new InvalidOperationException("Это единственный участок дороги.");
        var copies = new Dictionary<Guid, RoadNode>();
        foreach (var vertex in branch.Vertices)
            if (!copies.ContainsKey(vertex.NodeId))
            {
                var old = RequireNode(road, vertex.NodeId);
                copies[vertex.NodeId] = new RoadNode(Guid.NewGuid(), old.X, old.Y);
            }
        var moved = branch with { Id = Guid.NewGuid(), Style = null, Vertices = [.. branch.Vertices.Select(v => v with { NodeId = copies[v.NodeId].Id })] };
        var detached = new Road
        {
            Name = detachedName, Style = road.EffectiveStyle(branch), WidthScale = road.WidthScale, Level = road.Level,
            Nodes = [.. copies.Values], Branches = [moved],
        };
        detached = detached with { Seed = detached.Id.GetHashCode() };
        var remaining = Prune(road with { Branches = [.. road.Branches.Where(b => b.Id != branchId)] });
        return (remaining, detached);
    }

    public static Road SetClosed(Road road, Guid branchId, bool closed)
    {
        var branch = RequireBranch(road, branchId);
        if (branch.Closed == closed) return road;
        if (closed)
        {
            var vertices = branch.Vertices;
            // An open branch that already returns to its first node closes by dropping the repeat.
            if (vertices.Length >= 2 && vertices[0].NodeId == vertices[^1].NodeId) vertices = vertices.RemoveAt(vertices.Length - 1);
            if (vertices.Length < 3) throw new InvalidOperationException("Для замкнутого маршрута нужны хотя бы три узла.");
            return Prune(ReplaceBranch(road, branch with { Closed = true, Vertices = vertices }));
        }
        return ReplaceBranch(road, branch with { Closed = false });
    }

    public static Road Reverse(Road road, Guid branchId)
    {
        var branch = RequireBranch(road, branchId);
        return ReplaceBranch(road, branch with { Vertices = [.. branch.Vertices.Reverse()] });
    }

    /// <summary>
    /// Relaxes the course of a branch without adding a node: each of its own interior nodes moves
    /// a quarter of the way towards the midpoint of its neighbours. Shared nodes stay where the
    /// other branches expect them.
    /// </summary>
    public static Road Smooth(Road road, Guid branchId)
    {
        var branch = RequireBranch(road, branchId);
        var vertices = branch.Vertices;
        var count = vertices.Length;
        if (count < 3) return road;
        var moved = new Dictionary<Guid, RoadNode>();
        for (var i = 0; i < count; i++)
        {
            if (!branch.Closed && (i == 0 || i == count - 1)) continue;
            var id = vertices[i].NodeId;
            if (road.IsShared(id) || vertices[i].Sharp) continue;
            var current = RequireNode(road, id);
            var previous = RequireNode(road, vertices[(i - 1 + count) % count].NodeId);
            var next = RequireNode(road, vertices[(i + 1) % count].NodeId);
            moved[id] = current with { X = (previous.X + 2 * current.X + next.X) / 4, Y = (previous.Y + 2 * current.Y + next.Y) / 4 };
        }
        return moved.Count == 0 ? road : road with { Nodes = [.. road.Nodes.Select(n => moved.TryGetValue(n.Id, out var m) ? m : n)] };
    }

    /// <summary>
    /// Drops the vertices a straight-enough course does not need, within a world tolerance. Shared
    /// nodes, sharp vertices and both ends are kept, so no fork and no corner is lost.
    /// </summary>
    public static Road SimplifyBranch(Road road, Guid branchId, double tolerance)
    {
        var branch = RequireBranch(road, branchId);
        var minimum = branch.Closed ? 3 : 2;
        if (branch.Vertices.Length <= minimum || tolerance <= 0) return road;
        var points = branch.Vertices.Select(v => { var n = RequireNode(road, v.NodeId); return new VectorPoint(n.X, n.Y); }).ToList();
        var kept = RiverGeometry.SimplifyIndices(points, tolerance).ToHashSet();
        for (var i = 0; i < branch.Vertices.Length; i++)
            if (branch.Vertices[i].Sharp || road.IsShared(branch.Vertices[i].NodeId)) kept.Add(i);
        if (kept.Count < minimum || kept.Count == branch.Vertices.Length) return road;
        var vertices = branch.Vertices.Where((_, i) => kept.Contains(i)).ToImmutableArray();
        return Prune(ReplaceBranch(road, branch with { Vertices = vertices }));
    }

    /// <summary>
    /// Moves every node and branch of <paramref name="source"/> into <paramref name="target"/>.
    /// A branch whose look differed from the target's keeps it as its own style, so joining two
    /// roads never repaints either of them.
    /// </summary>
    public static Road Absorb(Road target, Road source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        // The source's widths were drawn through its own WidthScale; inside the target they go
        // through the target's, so each vertex keeps the width the user actually saw.
        var ratio = source.WidthScale / target.WidthScale;
        var branches = source.Branches.Select(b =>
        {
            var effective = source.EffectiveStyle(b);
            return b with
            {
                Style = effective == target.Style ? null : effective,
                Vertices = ratio == 1 ? b.Vertices : [.. b.Vertices.Select(v => v with { Width = Math.Clamp(v.Width * ratio, Road.MinimumWidth, Road.MaximumWidth) })],
            };
        });
        return target with { Nodes = [.. target.Nodes, .. source.Nodes], Branches = [.. target.Branches, .. branches] };
    }
}

/// <summary>Atomic replace/insert/delete of one road on one object layer; redo reuses the exact snapshot.</summary>
public sealed class RoadCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Guid _roadId;
    private readonly Road? _requested;
    private readonly Guid? _absorbed;
    private readonly Road? _additional;
    private MapDocument? _before, _after;

    public RoadCommand(LayerId layerId, Guid roadId, Road? requested, string name, Guid? absorbedRoadId = null, Road? additionalRoad = null)
    { _layerId = layerId; _roadId = roadId; _requested = requested; Name = name; _absorbed = absorbedRoadId; _additional = additionalRoad; }

    public string Name { get; }

    public long? MeasuredWorkingBytes
    {
        get
        {
            var nodes = 0L;
            foreach (var road in new[] { _requested, _additional })
                if (road is not null)
                    nodes += road.Nodes.Length + road.Branches.Sum(b => b.Vertices.Length);
            return RiverCommand.RiverCommandBaseBytes + nodes * RiverCommand.NodeWorkingBytes;
        }
    }

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var layer = document.FindLayer(_layerId) as ObjectLayer ?? throw new InvalidOperationException("Слой дороги не найден.");
        if (!layer.Visible || layer.Locked) throw new InvalidOperationException("Слой дороги скрыт или заблокирован.");
        var existing = layer.Roads.FirstOrDefault(r => r.Id == _roadId);
        if (existing is null && _requested is null) throw new InvalidOperationException("Дорога не найдена.");
        if (_requested is { } requested)
        {
            if (requested.Id != _roadId) throw new InvalidOperationException("Идентификатор дороги изменён.");
            requested.Validate();
        }
        _additional?.Validate();
        var roads = layer.Roads.Where(r => r.Id != _roadId && r.Id != _absorbed && r.Id != _additional?.Id).ToList();
        if (_requested is not null)
        {
            var index = layer.Roads.ToList().FindIndex(r => r.Id == _roadId);
            roads.Insert(index < 0 ? roads.Count : Math.Min(index, roads.Count), _requested);
        }
        if (_additional is not null) roads.Add(_additional);

        // The explicit surface order only names what exists. A brand-new road takes the default
        // place of a road - under the water of its layer - so nothing above it changes.
        var order = layer.SurfaceOrder;
        if (!order.IsDefaultOrEmpty)
        {
            var list = order.Where(id => roads.Any(r => r.Id == id) || layer.Rivers.Any(r => r.Id == id)).ToList();
            foreach (var added in new[] { existing is null ? _requested : null, _additional })
            {
                if (added is null || list.Contains(added.Id)) continue;
                var firstWater = list.FindIndex(id => layer.Rivers.Any(r => r.Id == id));
                list.Insert(firstWater < 0 ? list.Count : firstWater, added.Id);
            }
            order = [.. list];
        }
        _before = document; _after = WithRoads(document, layer, roads, order);
        return _after;
    }

    public MapDocument Revert(MapDocument document) => _before ?? throw new InvalidOperationException("Команда дороги ещё не выполнена.");

    internal static MapDocument WithRoads(MapDocument document, ObjectLayer layer, IReadOnlyList<Road> roads, ImmutableArray<Guid> order)
    {
        var layers = document.Layers.ToArray(); var li = Array.FindIndex(layers, l => l.Id == layer.Id);
        var targetLayer = layer with { Roads = roads.ToImmutableArray(), SurfaceOrder = order }; layers[li] = targetLayer;
        var target = document.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterReplacements(document, target, []); ObjectNameIndex.Share(document, target);
        ObjectLayerChanges.Register(layer, targetLayer, [], []);
        return target;
    }
}

/// <summary>
/// Changes only the order of one layer's roads and water, which decides what covers what among
/// surfaces of the same level. The same roads and rivers come out; ids the order does not name
/// keep their place after the named ones. Undo restores the exact previous snapshot.
/// </summary>
public sealed class ReorderSurfaceCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<Guid> _order;
    private MapDocument? _before, _after;

    public ReorderSurfaceCommand(LayerId layerId, IReadOnlyList<Guid> order, string name)
    {
        ArgumentNullException.ThrowIfNull(order);
        _layerId = layerId;
        _order = [.. order];
        Name = string.IsNullOrWhiteSpace(name) ? "Reorder surfaces" : name;
    }

    public string Name { get; }

    public long? MeasuredWorkingBytes => RiverCommand.RiverCommandBaseBytes;

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var layer = document.FindLayer(_layerId) as ObjectLayer ?? throw new InvalidOperationException("Слой не найден.");
        if (!layer.Visible || layer.Locked) throw new InvalidOperationException("Слой скрыт или заблокирован.");
        var ordered = LayerSurfaceOrder.Ordered(layer, _order);
        _before = document;
        _after = RoadCommand.WithRoads(document, layer with { Rivers = ordered.Rivers }, ordered.Roads, ordered.SurfaceOrder);
        return _after;
    }

    public MapDocument Revert(MapDocument document) => _before ?? document;
}
