using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Where an inherited river or road came from (R28): the parent map, its feature and the shape it had when copied.</summary>
public sealed record InheritedFrom(Guid ParentMap, Guid SourceId, string Fingerprint);

/// <summary>What a sync of one child map would do (R28).</summary>
public sealed record ScaleSyncPlan(
    IReadOnlyList<Road> AddRoads, IReadOnlyList<(Road Before, Road After)> UpdateRoads, IReadOnlyList<Road> RemoveRoads,
    IReadOnlyList<River> AddRivers, IReadOnlyList<(River Before, River After)> UpdateRivers, IReadOnlyList<River> RemoveRivers,
    IReadOnlyList<string> KeptByHand)
{
    public int Changes => AddRoads.Count + UpdateRoads.Count + RemoveRoads.Count + AddRivers.Count + UpdateRivers.Count + RemoveRivers.Count;

    public string Summary()
    {
        var parts = new List<string>();
        void Part(int count, string text) { if (count > 0) parts.Add($"{text}: {count}"); }
        Part(AddRoads.Count + AddRivers.Count, "новых");
        Part(UpdateRoads.Count + UpdateRivers.Count, "изменённых");
        Part(RemoveRoads.Count + RemoveRivers.Count, "убранных");
        Part(KeptByHand.Count, "правленных вручную (не трогаю)");
        return parts.Count == 0 ? "совпадает с родителем" : string.Join(", ", parts);
    }
}

/// <summary>
/// R28: the rivers and roads of a parent map carried into a child map of the atlas. What crosses the
/// child's area is clipped at its edge (so a river meets the edge exactly where the parent has it),
/// moved and scaled into the child's canvas and marked as inherited with the shape it had. Next time
/// a changed parent updates its copies - unless the copy was edited by hand in the child: then the
/// copy is a manual exception and stays. A copy whose source left the area goes away the same way.
/// </summary>
public static class ScaleSync
{
    public const string LayerName = "Из родительской карты";

    public static ScaleSyncPlan Plan(MapDocument parent, MapDocument child, AtlasTransform transform)
    {
        var scale = transform.ChildWidth / transform.Width;
        var area = new WorldBounds(transform.X, transform.Y, transform.X + transform.Width, transform.Y + transform.Height);
        var childRoads = child.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Where(r => r.Inherited?.ParentMap == parent.Id.Value).ToList();
        var childRivers = child.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Where(r => r.Inherited?.ParentMap == parent.Id.Value).ToList();
        var (addRoads, updateRoads, removeRoads, kept) = (new List<Road>(), new List<(Road, Road)>(), new List<Road>(), new List<string>());
        var (addRivers, updateRivers, removeRivers) = (new List<River>(), new List<(River, River)>(), new List<River>());

        var wantedRoads = new HashSet<Guid>();
        foreach (var road in parent.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads))
        {
            var pieces = Clip([.. road.Nodes.Select(n => new VectorPoint(n.X, n.Y))], area);
            if (pieces.Count == 0) continue;
            var points = pieces.OrderByDescending(p => p.Count).First().Select(p => ToChild(transform, p)).ToList();
            if (points.Count < 2) continue;
            wantedRoads.Add(road.Id);
            var fresh = RoadEditor.NewRoad(points, Math.Max(1, road.Branches.FirstOrDefault()?.Vertices.FirstOrDefault()?.Width ?? 12) * scale, road.Style, road.Name, road.Seed)
                with { Era = road.Era };
            fresh = fresh with { Inherited = new InheritedFrom(parent.Id.Value, road.Id, Fingerprint(fresh)) };
            if (childRoads.FirstOrDefault(r => r.Inherited!.SourceId == road.Id) is not { } existing) { addRoads.Add(fresh); continue; }
            if (Fingerprint(existing) != existing.Inherited!.Fingerprint) { kept.Add("дорога «" + existing.Name + "»"); continue; }
            if (fresh.Inherited!.Fingerprint != existing.Inherited.Fingerprint || existing.Name != road.Name)
                updateRoads.Add((existing, fresh with { Id = existing.Id }));
        }
        foreach (var gone in childRoads.Where(r => !wantedRoads.Contains(r.Inherited!.SourceId)))
        {
            if (Fingerprint(gone) != gone.Inherited!.Fingerprint) kept.Add("дорога «" + gone.Name + "»");
            else removeRoads.Add(gone);
        }

        var wantedRivers = new HashSet<Guid>();
        foreach (var river in parent.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers))
        {
            var channels = new List<RiverChannel>();
            foreach (var channel in river.Channels)
                foreach (var piece in Clip([.. channel.Nodes.Select(n => new VectorPoint(n.X, n.Y))], area))
                {
                    var points = piece.Select(p => ToChild(transform, p)).ToList();
                    if (points.Count < 2) continue;
                    var width = Math.Max(River.MinimumWidth, (channel.Nodes.Length == 0 ? 6 : channel.Nodes.Average(n => n.Width)) * scale);
                    channels.Add(RiverEditor.NewChannel(points, width, channel.Name));
                }
            if (channels.Count == 0) continue;
            wantedRivers.Add(river.Id);
            var fresh = river with { Id = Guid.NewGuid(), Channels = [.. channels], Inherited = null };
            fresh = fresh with { Inherited = new InheritedFrom(parent.Id.Value, river.Id, Fingerprint(fresh)) };
            if (childRivers.FirstOrDefault(r => r.Inherited!.SourceId == river.Id) is not { } existing) { addRivers.Add(fresh); continue; }
            if (Fingerprint(existing) != existing.Inherited!.Fingerprint) { kept.Add("река «" + existing.Name + "»"); continue; }
            if (fresh.Inherited!.Fingerprint != existing.Inherited.Fingerprint || existing.Name != river.Name)
                updateRivers.Add((existing, fresh with { Id = existing.Id }));
        }
        foreach (var gone in childRivers.Where(r => !wantedRivers.Contains(r.Inherited!.SourceId)))
        {
            if (Fingerprint(gone) != gone.Inherited!.Fingerprint) kept.Add("река «" + gone.Name + "»");
            else removeRivers.Add(gone);
        }
        return new ScaleSyncPlan(addRoads, updateRoads, removeRoads, addRivers, updateRivers, removeRivers, kept);
    }

    /// <summary>The child with the plan applied: new copies on the «Из родительской карты» layer, updates in place, removals.</summary>
    public static MapDocument Apply(MapDocument child, ScaleSyncPlan plan)
    {
        if (plan.Changes == 0) return child;
        var updatesRoads = plan.UpdateRoads.ToDictionary(u => u.Before.Id, u => u.After);
        var updatesRivers = plan.UpdateRivers.ToDictionary(u => u.Before.Id, u => u.After);
        var removeRoads = plan.RemoveRoads.Select(r => r.Id).ToHashSet();
        var removeRivers = plan.RemoveRivers.Select(r => r.Id).ToHashSet();
        var layers = child.Layers.Select(layer => layer is ObjectLayer objects
            ? objects with
            {
                Roads = [.. objects.Roads.Where(r => !removeRoads.Contains(r.Id)).Select(r => updatesRoads.GetValueOrDefault(r.Id, r))],
                Rivers = [.. objects.Rivers.Where(r => !removeRivers.Contains(r.Id)).Select(r => updatesRivers.GetValueOrDefault(r.Id, r))],
            }
            : layer).ToList();
        if (plan.AddRoads.Count + plan.AddRivers.Count > 0)
        {
            var index = layers.FindIndex(l => l is ObjectLayer { Name: LayerName });
            var target = index >= 0 ? (ObjectLayer)layers[index] : new ObjectLayer(LayerId.New(), LayerName, true, false, 1, []);
            target = target with { Roads = [.. target.Roads, .. plan.AddRoads], Rivers = [.. target.Rivers, .. plan.AddRivers] };
            if (index >= 0) layers[index] = target;
            else layers.Insert(Math.Max(1, layers.FindLastIndex(l => l is LandLayer or BaseColorLayer) + 1), target);
        }
        return child.WithLayers(layers);
    }

    /// <summary>
    /// The coast of the parent inside the child's area as the child's land (R28/R35): the parent's
    /// visible land coverage sampled smoothly into the child's canvas, in the style of the parent's
    /// topmost land. Null when the area holds no land.
    /// </summary>
    public static LandLayer? Coast(MapDocument parent, AtlasTransform transform, int childWidth, int childHeight)
    {
        var lands = TerrainOrder.Lands(parent).Where(l => l.Visible && l.Opacity > 0 && l.GeometryTiles.TileCount > 0).ToList();
        if (lands.Count == 0) return null;
        double Cover(double x, double y)
        {
            // Bilinear over the strongest land at each corner, so a tenfold zoom gives a smooth edge.
            var (x0, y0) = (Math.Floor(x - .5), Math.Floor(y - .5));
            var (fx, fy) = (x - .5 - x0, y - .5 - y0);
            double At(double px, double py) => lands.Max(l => (int)TerrainOrder.CoverageAt(l, px, py) * (TerrainOrder.MaskCoverageAt(l, px, py) / 255.0));
            var top = (At(x0, y0) * (1 - fx)) + (At(x0 + 1, y0) * fx);
            var bottom = (At(x0, y0 + 1) * (1 - fx)) + (At(x0 + 1, y0 + 1) * fx);
            return (top * (1 - fy)) + (bottom * fy);
        }

        var size = TileMap.TileSize;
        var tiles = TileMap.Empty(1);
        var any = false;
        for (var ty = 0; ty * size < childHeight; ty++)
        for (var tx = 0; tx * size < childWidth; tx++)
        {
            // Open sea under the whole tile: nothing to sample.
            var (l0, t0) = transform.ToParent(tx * size, ty * size);
            var (r0, b0) = transform.ToParent((tx + 1) * size, (ty + 1) * size);
            var (k0x, k0y, k1x, k1y) = ((int)Math.Floor((l0 - 1) / size), (int)Math.Floor((t0 - 1) / size), (int)Math.Floor((r0 + 1) / size), (int)Math.Floor((b0 + 1) / size));
            if (!lands.Any(l => l.GeometryTiles.Keys.Any(k => k.X >= k0x && k.X <= k1x && k.Y >= k0y && k.Y <= k1y))) continue;
            var pixels = new byte[size * size];
            var filled = false;
            for (var py = 0; py < size; py++)
            for (var px = 0; px < size; px++)
            {
                var (cx, cy) = ((tx * size) + px + .5, (ty * size) + py + .5);
                if (cx > childWidth || cy > childHeight) continue;
                var (wx, wy) = transform.ToParent(cx, cy);
                var value = (byte)Math.Clamp(Math.Round(Cover(wx, wy)), 0, 255);
                if (value == 0) continue;
                pixels[(py * size) + px] = value;
                filled = true;
            }
            if (!filled) continue;
            tiles = tiles.With(new TileKey(tx, ty), pixels);
            any = true;
        }
        if (!any) return null;
        var style = lands[^1];
        return style with
        {
            Id = LayerId.New(), Name = "Суша · " + LayerName.ToLowerInvariant(), Mask = null, GeometryTiles = tiles,
            PaintTiles = TileMap.Empty(4), CliffWallTiles = TileMap.Empty(1), CliffBeachTiles = TileMap.Empty(1), ReliefTiles = TileMap.Empty(style.ReliefTiles.BytesPerPixel),
        };
    }

    private static VectorPoint ToChild(AtlasTransform transform, VectorPoint point)
    {
        var (x, y) = transform.ToChild(point.X, point.Y);
        return new VectorPoint(Math.Round(x, 2), Math.Round(y, 2));
    }

    /// <summary>The pieces of a polyline inside a rectangle; where it crosses the edge a point is put exactly on the edge.</summary>
    public static List<List<VectorPoint>> Clip(IReadOnlyList<VectorPoint> line, WorldBounds area)
    {
        bool In(VectorPoint p) => p.X >= area.Left && p.X <= area.Right && p.Y >= area.Top && p.Y <= area.Bottom;
        var pieces = new List<List<VectorPoint>>();
        List<VectorPoint>? current = null;
        for (var i = 0; i < line.Count; i++)
        {
            var p = line[i];
            if (In(p))
            {
                if (current is null)
                {
                    current = [];
                    if (i > 0 && Cross(line[i - 1], p, area) is { } entry) current.Add(entry);
                    pieces.Add(current);
                }
                current.Add(p);
            }
            else
            {
                if (current is not null && Cross(p, line[i - 1], area) is { } exit) current.Add(exit);
                current = null;
                // A segment that passes through the area with both ends outside.
                if (i > 0 && !In(line[i - 1]) && Through(line[i - 1], p, area) is { } through) pieces.Add([through.A, through.B]);
            }
        }
        return [.. pieces.Where(p => p.Count >= 2)];
    }

    /// <summary>Where the segment from an outside point to an inside one crosses the edge.</summary>
    private static VectorPoint? Cross(VectorPoint outside, VectorPoint inside, WorldBounds area)
    {
        var (t0, _) = SegmentRange(outside, inside, area);
        return t0 is { } t ? new VectorPoint(outside.X + ((inside.X - outside.X) * t), outside.Y + ((inside.Y - outside.Y) * t)) : null;
    }

    private static (VectorPoint A, VectorPoint B)? Through(VectorPoint a, VectorPoint b, WorldBounds area)
    {
        var (t0, t1) = SegmentRange(a, b, area);
        if (t0 is not { } s || t1 is not { } e || e - s < 1e-9) return null;
        return (new VectorPoint(a.X + ((b.X - a.X) * s), a.Y + ((b.Y - a.Y) * s)), new VectorPoint(a.X + ((b.X - a.X) * e), a.Y + ((b.Y - a.Y) * e)));
    }

    /// <summary>The part of the segment inside the rectangle as parameters 0…1 (Liang–Barsky); nulls when it misses.</summary>
    private static (double?, double?) SegmentRange(VectorPoint a, VectorPoint b, WorldBounds area)
    {
        double t0 = 0, t1 = 1;
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        foreach (var (p, q) in new[] { (-dx, a.X - area.Left), (dx, area.Right - a.X), (-dy, a.Y - area.Top), (dy, area.Bottom - a.Y) })
        {
            if (Math.Abs(p) < 1e-12) { if (q < 0) return (null, null); continue; }
            var r = q / p;
            if (p < 0) { if (r > t1) return (null, null); t0 = Math.Max(t0, r); }
            else { if (r < t0) return (null, null); t1 = Math.Min(t1, r); }
        }
        return (t0, t1);
    }

    public static string Fingerprint(Road road) => Hash(road.Nodes.Select(n => (n.X, n.Y)));

    public static string Fingerprint(River river) => Hash(river.Channels.SelectMany(c => c.Nodes).Select(n => (n.X, n.Y)));

    private static string Hash(IEnumerable<(double X, double Y)> points)
    {
        var text = new StringBuilder();
        foreach (var (x, y) in points) text.Append(Math.Round(x, 1).ToString(CultureInfo.InvariantCulture)).Append(',').Append(Math.Round(y, 1).ToString(CultureInfo.InvariantCulture)).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>
    /// The terrain of each hex of a hex child taken from the painted parent (R28): water where the
    /// parent has no land, mountains or hills where its land rises, forest under a forest area,
    /// grass elsewhere. <paramref name="isForest"/> says which placement areas are forests.
    /// </summary>
    public static HexLayer HexesFromArt(HexLayer hexes, MapDocument parent, AtlasTransform transform, Func<PlacementArea, bool> isForest)
    {
        var lands = parent.Layers.OfType<LandLayer>().Where(l => l.Visible).ToList();
        var forests = parent.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Where(isForest)
            .Select(a => a.Nodes.Select(n => new VectorPoint(n.X, n.Y)).ToList()).ToList();
        var changes = new List<KeyValuePair<HexCoord, HexCell?>>();
        foreach (var (hex, cell) in AllHexes(hexes.Board))
        {
            var (cx, cy) = hexes.Board.Layout.Center(hex);
            var (px, py) = transform.ToParent(cx, cy);
            var land = lands.FirstOrDefault(l => Coverage(l.GeometryTiles, px, py) > 127);
            string terrain;
            if (land is null) terrain = "starter/sea";
            else
            {
                var height = Coverage(land.ReliefTiles, px, py) / 255.0;
                terrain = height > .7 ? "starter/mountains" : height > .45 ? "starter/hills"
                    : forests.Any(f => Inside(f, px, py)) ? "starter/forest" : "starter/grass";
            }
            if (cell?.Terrain != terrain) changes.Add(new(hex, HexCell.Of(terrain, (int)(HexEditing.Noise(hex, 23) % 3))));
        }
        return changes.Count == 0 ? hexes : hexes with { Board = hexes.Board.With(changes) };
    }

    private static IEnumerable<(HexCoord, HexCell?)> AllHexes(HexBoard board)
    {
        for (var row = 0; row < board.Rows; row++)
            for (var column = 0; column < board.Columns; column++)
            {
                var hex = board.Layout.FromOffset(new HexOffset(column, row));
                yield return (hex, board[hex]);
            }
    }

    private static byte Coverage(TileMap tiles, double x, double y)
    {
        if (x < 0 || y < 0) return 0;
        var key = TileMap.KeyAt(x, y);
        if (tiles.TryGet(key) is not { } pixels) return 0;
        var (lx, ly) = ((int)x - (key.X * TileMap.TileSize), (int)y - (key.Y * TileMap.TileSize));
        return pixels[(Math.Clamp(ly, 0, TileMap.TileSize - 1) * TileMap.TileSize) + Math.Clamp(lx, 0, TileMap.TileSize - 1)];
    }

    private static bool Inside(List<VectorPoint> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y) && x < ((polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X)
                inside = !inside;
        return inside;
    }
}
