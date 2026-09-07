using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Pure immutable edits of a river; every result still has to pass <see cref="River.Validate"/>.</summary>
public static class RiverEditor
{
    public static River ReplaceChannel(River river, RiverChannel channel) =>
        river with { Channels = [.. river.Channels.Select(c => c.Id == channel.Id ? channel : c)] };

    private static RiverChannel Require(River river, Guid channelId) =>
        river.FindChannel(channelId) ?? throw new InvalidOperationException("Русло не найдено.");

    public static River MoveNode(River river, Guid channelId, int index, double x, double y)
    {
        var channel = Require(river, channelId);
        return ReplaceChannel(river, channel with { Nodes = channel.Nodes.SetItem(index, channel.Nodes[index] with { X = x, Y = y }) });
    }

    public static River SetNodeWidth(River river, Guid channelId, int index, double width)
    {
        var channel = Require(river, channelId);
        return ReplaceChannel(river, channel with { Nodes = channel.Nodes.SetItem(index, channel.Nodes[index] with { Width = width }) });
    }

    public static River SetNodeSharp(River river, Guid channelId, int index, bool sharp)
    {
        var channel = Require(river, channelId);
        return ReplaceChannel(river, channel with { Nodes = channel.Nodes.SetItem(index, channel.Nodes[index] with { Sharp = sharp }) });
    }

    /// <summary>Curvature handle of one node: the tangent scale the spline uses there.</summary>
    public static River SetNodeTension(River river, Guid channelId, int index, double tension)
    {
        var channel = Require(river, channelId);
        var clamped = Math.Clamp(tension, RiverNode.MinimumTension, RiverNode.MaximumTension);
        return ReplaceChannel(river, channel with
        { Nodes = channel.Nodes.SetItem(index, channel.Nodes[index] with { Tension = clamped, Sharp = clamped <= 0 && channel.Nodes[index].Sharp }) });
    }

    /// <summary>Curvature handle of every node of one channel, for a whole-course smoothness dial.</summary>
    public static River SetChannelTension(River river, Guid channelId, double tension)
    {
        var channel = Require(river, channelId);
        var clamped = Math.Clamp(tension, RiverNode.MinimumTension, RiverNode.MaximumTension);
        return ReplaceChannel(river, channel with { Nodes = [.. channel.Nodes.Select(n => n with { Tension = clamped })] });
    }

    /// <summary>
    /// Moves every node of the river by the same world offset. Junctions address parent segments
    /// by node id and fraction, so a whole system keeps every connection while it travels.
    /// </summary>
    public static River Translate(River river, double dx, double dy) => river with
    {
        Channels = [.. river.Channels.Select(c => c with { Nodes = [.. c.Nodes.Select(n => n with { X = n.X + dx, Y = n.Y + dy })] })],
    };

    /// <summary>
    /// Drops nodes a straight-enough course does not need, within an explicit world tolerance:
    /// the visible course stays inside <paramref name="tolerance"/> of the previous one. Sharp
    /// nodes and both ends are kept, and junctions are re-addressed to the new geometry.
    /// </summary>
    public static River SimplifyChannel(River river, Guid channelId, double tolerance)
    {
        var channel = Require(river, channelId);
        var minimum = channel.Closed ? 3 : 2;
        if (channel.Nodes.Length <= minimum || tolerance <= 0) return river;
        var points = channel.Nodes.Select(n => new VectorPoint(n.X, n.Y)).ToList();
        var kept = new HashSet<int>();
        var simplified = RiverGeometry.SimplifyIndices(points, tolerance);
        foreach (var index in simplified) kept.Add(index);
        for (var i = 0; i < channel.Nodes.Length; i++) if (channel.Nodes[i].Sharp) kept.Add(i);
        if (kept.Count < minimum) return river;
        if (kept.Count == channel.Nodes.Length) return river;
        var nodes = channel.Nodes.Where((_, i) => kept.Contains(i)).ToImmutableArray();
        return RemapJunctions(river, ReplaceChannel(river, channel with { Nodes = nodes }), channelId);
    }

    /// <summary>Inserts a node on a segment with the interpolated width; junctions on that segment keep their place.</summary>
    public static River InsertNode(River river, Guid channelId, int segment, double fraction, VectorPoint point)
    {
        var channel = Require(river, channelId);
        var geometry = RiverGeometry.Evaluate(river).Channels[channelId];
        var width = RiverGeometry.WidthAt(geometry, Math.Max(0, segment), fraction) / Math.Max(0.01, river.WidthScale);
        var node = new RiverNode(Guid.NewGuid(), point.X, point.Y, Math.Clamp(width, River.MinimumWidth, River.MaximumWidth));
        var at = Math.Clamp(segment + 1, 0, channel.Nodes.Length);
        if (channel.Closed && segment >= channel.Nodes.Length - 1) at = channel.Nodes.Length;
        var next = ReplaceChannel(river, channel with { Nodes = channel.Nodes.Insert(at, node) });
        return RemapJunctions(river, next, channelId);
    }

    /// <summary>Removes a node; junctions on the merged segment keep their world position.</summary>
    public static River DeleteNode(River river, Guid channelId, int index)
    {
        var channel = Require(river, channelId);
        if (channel.Nodes.Length <= (channel.Closed ? 3 : 2))
            throw new InvalidOperationException(channel.Closed ? "У озера должно остаться три узла." : "Оставьте два узла или удалите всё русло.");
        var next = ReplaceChannel(river, channel with { Nodes = channel.Nodes.RemoveAt(index) });
        return RemapJunctions(river, next, channelId);
    }

    /// <summary>Keeps the nodes from <paramref name="from"/> to <paramref name="to"/> inclusive; the rest is trimmed away.</summary>
    public static River Trim(River river, Guid channelId, int from, int to)
    {
        var channel = Require(river, channelId);
        if (channel.Closed) throw new InvalidOperationException("Озеро не обрезается: удаляйте его узлы.");
        from = Math.Clamp(from, 0, channel.Nodes.Length - 1); to = Math.Clamp(to, from, channel.Nodes.Length - 1);
        if (to - from < 1) throw new InvalidOperationException("Оставьте хотя бы два узла.");
        var kept = channel with
        {
            Nodes = [.. channel.Nodes.Skip(from).Take(to - from + 1)],
            Source = from == 0 ? channel.Source : null,
            Mouth = to == channel.Nodes.Length - 1 ? channel.Mouth : null,
        };
        return RemapJunctions(river, ReplaceChannel(river, kept), channelId);
    }

    /// <summary>
    /// After nodes were inserted into or removed from a channel, every junction on that channel is
    /// re-addressed to the segment now closest to its previous world point. Junctions on other
    /// channels are untouched.
    /// </summary>
    internal static River RemapJunctions(River before, River after, Guid channelId)
    {
        var previous = RiverGeometry.Evaluate(before);
        var current = RiverGeometry.Evaluate(after);
        if (!previous.Channels.TryGetValue(channelId, out var oldParent) || !current.Channels.TryGetValue(channelId, out var parent)) return after;
        RiverJunction? Remap(RiverJunction? junction)
        {
            if (junction is null || junction.ParentChannelId != channelId) return junction;
            if (!RiverGeometry.TryJunction(oldParent, junction, out var point)) return junction;
            return ClosestJunction(current, parent, point.X, point.Y) ?? junction;
        }
        return after with { Channels = [.. after.Channels.Select(c => c with { Mouth = Remap(c.Mouth), Source = Remap(c.Source) })] };
    }

    /// <summary>The junction on <paramref name="parent"/> nearest to a world point, clamped to real node segments.</summary>
    public static RiverJunction? ClosestJunction(RiverGeometrySet set, RiverChannelGeometry parent, double x, double y)
    {
        if (!RiverGeometry.TryClosest(set, x, y, double.MaxValue, out _, out var segment, out var fraction, out _, out _,
                c => c.Id == parent.Channel.Id))
            return null;
        var nodes = parent.Channel.Nodes;
        var last = parent.Closed ? nodes.Length - 1 : nodes.Length - 2;
        if (segment < 0) { segment = 0; fraction = 0; }
        if (segment > last) { segment = last; fraction = 1; }
        return new RiverJunction(parent.Channel.Id, nodes[segment].Id, Math.Clamp(fraction, 0, 1));
    }

    public static River SetWidthProfile(River river, Guid channelId, double sourceWidth, double mouthWidth)
    {
        var channel = Require(river, channelId);
        return ReplaceChannel(river, channel with { Nodes = RiverGeometry.WidthProfile(channel.Nodes, sourceWidth, mouthWidth) });
    }

    /// <summary>Attaches or moves a mouth; refuses a junction that would send water back upstream.</summary>
    public static River SetMouth(River river, Guid channelId, RiverJunction? mouth) => SetJunction(river, channelId, mouth, isMouth: true);

    /// <summary>Attaches or moves a source (distributary or lake outflow) with the same cycle rule.</summary>
    public static River SetSource(River river, Guid channelId, RiverJunction? source) => SetJunction(river, channelId, source, isMouth: false);

    private static River SetJunction(River river, Guid channelId, RiverJunction? junction, bool isMouth)
    {
        var channel = Require(river, channelId);
        if (channel.Closed && junction is not null) throw new InvalidOperationException("Озеро присоединяется руслом, а не само.");
        if (junction is not null && (junction.ParentChannelId == channelId || river.DependsOn(junction.ParentChannelId, channelId)))
            throw new InvalidOperationException("Русло не может впадать в собственное течение или вытекать из него.");
        return ReplaceChannel(river, isMouth ? channel with { Mouth = junction } : channel with { Source = junction });
    }

    /// <summary>Removes one channel only; channels attached to it become free at that end.</summary>
    public static River DeleteChannel(River river, Guid channelId)
    {
        if (river.Channels.Length <= 1) throw new InvalidOperationException("Удалите всю реку целиком.");
        return river with
        {
            Channels = [.. river.Channels.Where(c => c.Id != channelId).Select(c => c with
            {
                Mouth = c.Mouth?.ParentChannelId == channelId ? null : c.Mouth,
                Source = c.Source?.ParentChannelId == channelId ? null : c.Source,
            })],
        };
    }

    /// <summary>
    /// Splits a channel at a node into two channels that stay connected: the upstream part flows
    /// into the start of the downstream part. Junctions keep their places on either part.
    /// </summary>
    public static River SplitChannel(River river, Guid channelId, int index, out Guid downstreamId)
    {
        var channel = Require(river, channelId);
        if (channel.Closed) throw new InvalidOperationException("Озеро не разделяется.");
        if (index <= 0 || index >= channel.Nodes.Length - 1) throw new InvalidOperationException("Разделить можно только во внутреннем узле.");
        var splitNode = channel.Nodes[index];
        var head = new RiverNode(Guid.NewGuid(), splitNode.X, splitNode.Y, splitNode.Width, splitNode.Sharp);
        var downstream = new RiverChannel
        {
            Name = channel.Name + " · ниже", Nodes = [head, .. channel.Nodes.Skip(index + 1)], Mouth = channel.Mouth,
        };
        downstreamId = downstream.Id;
        var upstream = channel with
        {
            Nodes = [.. channel.Nodes.Take(index + 1)],
            Mouth = new RiverJunction(downstream.Id, head.Id, 0),
        };
        var channels = river.Channels.Select(c => c.Id == channelId ? upstream : c).ToList();
        channels.Insert(channels.IndexOf(upstream) + 1, downstream);
        var oldNodeIndex = channel.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(p => p.Id, p => p.i);
        RiverJunction? Remap(RiverJunction? junction)
        {
            if (junction is null || junction.ParentChannelId != channelId) return junction;
            var at = oldNodeIndex.GetValueOrDefault(junction.ParentNodeId, -1);
            if (at < index) return junction;
            return junction with { ParentChannelId = downstream.Id, ParentNodeId = at == index ? head.Id : junction.ParentNodeId };
        }
        return river with { Channels = [.. channels.Select(c => c with { Mouth = Remap(c.Mouth), Source = Remap(c.Source) })] };
    }

    /// <summary>
    /// Cuts a channel at a node and moves the downstream part, with everything attached to it,
    /// into a new independent river. The upstream part keeps a free end.
    /// </summary>
    public static (River Remaining, River Detached) SplitRiver(River river, Guid channelId, int index, string detachedName)
    {
        var split = SplitChannel(river, channelId, index, out var downstreamId);
        var upstream = split.FindChannel(channelId)!;
        split = ReplaceChannel(split, upstream with { Mouth = null });
        // Everything that (transitively) attaches to the downstream part travels with it.
        var moving = new HashSet<Guid> { downstreamId };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var c in split.Channels)
                if (!moving.Contains(c.Id) && c.Id != channelId
                    && (c.Mouth is { } m && moving.Contains(m.ParentChannelId) || c.Source is { } s && moving.Contains(s.ParentChannelId)))
                { moving.Add(c.Id); grew = true; }
        }
        RiverChannel Detach(RiverChannel c) => c with
        {
            Mouth = c.Mouth is { } m && moving.Contains(m.ParentChannelId) != moving.Contains(c.Id) ? null : c.Mouth,
            Source = c.Source is { } s && moving.Contains(s.ParentChannelId) != moving.Contains(c.Id) ? null : c.Source,
        };
        var remaining = split with { Channels = [.. split.Channels.Where(c => !moving.Contains(c.Id)).Select(Detach)] };
        var detached = new River
        {
            Name = detachedName, Style = river.Style, WidthScale = river.WidthScale,
            Channels = [.. split.Channels.Where(c => moving.Contains(c.Id)).Select(Detach)],
        };
        return (remaining, detached);
    }

    /// <summary>Reverses the flow of a channel: its mouth becomes its source; junctions on it keep their places.</summary>
    public static River Reverse(River river, Guid channelId)
    {
        var channel = Require(river, channelId);
        if (channel.Closed) return river;
        var reversed = channel with { Nodes = [.. channel.Nodes.Reverse()], Mouth = channel.Source, Source = channel.Mouth };
        return RemapJunctions(river, ReplaceChannel(river, reversed), channelId);
    }

    /// <summary>
    /// Relaxes the course towards its own average without adding a single node: each interior node
    /// moves a quarter of the way towards the midpoint of its neighbours. Applying it again keeps
    /// the same node count, so repeated use can never inflate the model - shape is controlled by
    /// <see cref="SetNodeTension"/> and by these positions, not by subdivision.
    /// </summary>
    public static River Smooth(River river, Guid channelId)
    {
        var channel = Require(river, channelId);
        var nodes = channel.Nodes;
        var count = nodes.Length;
        if (count < 3) return river;
        var result = new RiverNode[count];
        for (var i = 0; i < count; i++)
        {
            var current = nodes[i];
            if (channel.Closed || (i > 0 && i < count - 1))
            {
                var previous = nodes[(i - 1 + count) % count];
                var next = nodes[(i + 1) % count];
                current = current with { X = (previous.X + 2 * current.X + next.X) / 4, Y = (previous.Y + 2 * current.Y + next.Y) / 4 };
            }
            result[i] = current;
        }
        return ReplaceChannel(river, channel with { Nodes = [.. result] });
    }

    /// <summary>
    /// Adds meanders: each segment is subdivided and the new nodes swing alternately left and
    /// right by <paramref name="amplitude"/> of the segment length, deterministically per seed.
    /// </summary>
    public const int MeanderNodeCeiling = 512;

    public static River Meander(River river, Guid channelId, double amplitude, int seed)
    {
        var channel = Require(river, channelId);
        if (channel.Closed) return river;
        var nodes = channel.Nodes;
        if (nodes.Length >= MeanderNodeCeiling)
            throw new InvalidOperationException($"В русле уже {nodes.Length} узлов: сначала упростите его, иначе извилины только множат точки.");
        var result = new List<RiverNode> { nodes[0] };
        var flip = seed % 2 == 0 ? 1 : -1;
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            var a = nodes[i]; var b = nodes[i + 1];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
            var pieces = Math.Clamp((int)Math.Round(length / 60), 1, 8);
            for (var k = 1; k < pieces; k++)
            {
                var t = k / (double)pieces;
                var wobble = 0.6 + 0.4 * (0.5 + 0.5 * RiverGeometry.Noise(seed ^ (i * 977), k * 1.37));
                var offset = flip * amplitude * length / pieces * wobble;
                flip = -flip;
                result.Add(new RiverNode(Guid.NewGuid(), a.X + dx * t - dy / length * offset, a.Y + dy * t + dx / length * offset,
                    a.Width + (b.Width - a.Width) * t));
            }
            result.Add(b);
        }
        return RemapJunctions(river, ReplaceChannel(river, channel with { Nodes = [.. result] }), channelId);
    }

    /// <summary>
    /// Sets every channel width from the flow: a free source starts at <paramref name="sourceWidth"/>,
    /// width grows by <paramref name="growthPerThousand"/> per 1000 units of length, and a channel
    /// receiving a tributary widens by the combined discharge (√(w²+t²)) from that junction on.
    /// A distributary starts at half its parent's width there. Lakes are left alone.
    /// </summary>
    public static River WidthByFlow(River river, double sourceWidth, double growthPerThousand)
    {
        var set = RiverGeometry.Evaluate(river);
        var widths = new Dictionary<Guid, double[]>();
        var pending = river.Channels.Where(c => !c.Closed).ToList();
        var guard = 0;
        while (pending.Count > 0 && guard++ <= river.Channels.Length)
        {
            foreach (var channel in pending.ToArray())
            {
                var tributaries = river.Channels.Where(c => c.Mouth?.ParentChannelId == channel.Id && !c.Closed).ToList();
                if (tributaries.Any(t => !widths.ContainsKey(t.Id))) continue;
                if (channel.Source is { } source && river.FindChannel(source.ParentChannelId) is { Closed: false } sp && !widths.ContainsKey(sp.Id)) continue;
                var geometry = set.Channels[channel.Id];
                var nodes = channel.Nodes;
                var lengths = new double[nodes.Length];
                for (var i = 1; i < nodes.Length; i++)
                    lengths[i] = lengths[i - 1] + Math.Sqrt(Math.Pow(nodes[i].X - nodes[i - 1].X, 2) + Math.Pow(nodes[i].Y - nodes[i - 1].Y, 2));
                var start = sourceWidth;
                if (channel.Source is { } s && river.FindChannel(s.ParentChannelId) is { } parent)
                    start = parent.Closed ? sourceWidth * 2 : Math.Max(sourceWidth, WidthOn(parent, widths[parent.Id], s) * 0.5);
                var result = new double[nodes.Length];
                for (var i = 0; i < nodes.Length; i++)
                {
                    var w = start + growthPerThousand * lengths[i] / 1000;
                    foreach (var tributary in tributaries)
                    {
                        var seg = RiverGeometry.SegmentOf(channel, tributary.Mouth!.ParentNodeId);
                        if (seg < 0 || i <= seg) continue;
                        var tw = widths[tributary.Id][^1];
                        w = Math.Sqrt(w * w + tw * tw);
                    }
                    result[i] = Math.Round(Math.Clamp(w, River.MinimumWidth, River.MaximumWidth), 2);
                }
                widths[channel.Id] = result;
                pending.Remove(channel);
            }
        }
        return river with
        {
            Channels = [.. river.Channels.Select(c => widths.TryGetValue(c.Id, out var w)
                ? c with { Nodes = [.. c.Nodes.Select((n, i) => n with { Width = w[i] })] } : c)],
        };
    }

    private static double WidthOn(RiverChannel parent, double[] widths, RiverJunction junction)
    {
        var seg = RiverGeometry.SegmentOf(parent, junction.ParentNodeId);
        if (seg < 0) return widths[^1];
        var next = Math.Min(seg + 1, widths.Length - 1);
        return widths[seg] + (widths[next] - widths[seg]) * junction.Fraction;
    }

    /// <summary>Moves every channel of <paramref name="source"/> into <paramref name="target"/>, keeping ids and junctions.</summary>
    public static River Absorb(River target, River source) =>
        target with { Channels = [.. target.Channels, .. source.Channels] };

    /// <summary>Creates a channel from world points with one width, ready to be added to a river.</summary>
    public static RiverChannel NewChannel(IReadOnlyList<VectorPoint> points, double width, string name = "Русло") => new()
    {
        Name = name,
        Nodes = [.. points.Select(p => new RiverNode(Guid.NewGuid(), p.X, p.Y, width))],
    };

    /// <summary>Creates a lake outline from world points.</summary>
    public static RiverChannel NewLake(IReadOnlyList<VectorPoint> points, string name = "Озеро") => new()
    {
        Name = name, Closed = true,
        Nodes = [.. points.Select(p => new RiverNode(Guid.NewGuid(), p.X, p.Y, 1))],
    };
}

/// <summary>Atomic replace/insert/delete of one river on one object layer; redo reuses the exact snapshot.</summary>
public sealed class RiverCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Guid _riverId;
    private readonly River? _requested;
    private readonly Guid? _absorbed;
    private readonly River? _additional;
    private MapDocument? _before, _after;

    public RiverCommand(LayerId layerId, Guid riverId, River? requested, string name, Guid? absorbedRiverId = null, River? additionalRiver = null)
    { _layerId = layerId; _riverId = riverId; _requested = requested; Name = name; _absorbed = absorbedRiverId; _additional = additionalRiver; }

    public string Name { get; }

    /// <summary>
    /// A river command rewrites one river of one layer: the immutable snapshot shares every other
    /// layer, so the real cost is the rewritten nodes plus the centreline this edit will make the
    /// renderer and the hit test evaluate. One node costs its record (~80 B) and up to 64 samples
    /// of ~48 B on the segment that follows it, which <see cref="NodeWorkingBytes"/> rounds up to
    /// 4 KiB. A deletion still rebuilds the layer, hence the fixed base.
    /// </summary>
    public const long NodeWorkingBytes = 4 * 1024;
    public const long RiverCommandBaseBytes = 256 * 1024;

    public long EstimatedWorkingBytes
    {
        get
        {
            var nodes = 0L;
            foreach (var river in new[] { _requested, _additional })
                if (river is not null)
                    foreach (var channel in river.Channels)
                        nodes += channel.Nodes.Length;
            return RiverCommandBaseBytes + nodes * NodeWorkingBytes;
        }
    }

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var layer = document.FindLayer(_layerId) as ObjectLayer ?? throw new InvalidOperationException("Слой реки не найден.");
        if (!layer.Visible || layer.Locked) throw new InvalidOperationException("Слой реки скрыт или заблокирован.");
        var existing = layer.Rivers.FirstOrDefault(r => r.Id == _riverId);
        if (existing is null && _requested is null) throw new InvalidOperationException("Река не найдена.");
        if (_requested is { } requested)
        {
            if (requested.Id != _riverId) throw new InvalidOperationException("Идентификатор реки изменён.");
            requested.Validate();
        }
        _additional?.Validate();
        var rivers = layer.Rivers.Where(r => r.Id != _riverId && r.Id != _absorbed && r.Id != _additional?.Id).ToList();
        if (_requested is not null)
        {
            var index = layer.Rivers.ToList().FindIndex(r => r.Id == _riverId);
            rivers.Insert(index < 0 ? rivers.Count : Math.Min(index, rivers.Count), _requested);
        }
        if (_additional is not null) rivers.Add(_additional);
        _before = document; _after = WithRivers(document, layer, rivers);
        return _after;
    }

    public MapDocument Revert(MapDocument document) => _before ?? throw new InvalidOperationException("Команда реки ещё не выполнена.");

    internal static MapDocument WithRivers(MapDocument document, ObjectLayer layer, IReadOnlyList<River> rivers)
    {
        var layers = document.Layers.ToArray(); var li = Array.FindIndex(layers, l => l.Id == layer.Id);
        var targetLayer = layer with { Rivers = rivers.ToImmutableArray() }; layers[li] = targetLayer;
        var target = document.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterReplacements(document, target, []); ObjectNameIndex.Share(document, target);
        ObjectLayerChanges.Register(layer, targetLayer, [], []);
        return target;
    }
}
