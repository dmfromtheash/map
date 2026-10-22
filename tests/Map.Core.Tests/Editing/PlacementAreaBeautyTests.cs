using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R10 «Красота» and the density brush: an area without them fills as before; soft edge, clumps
/// and the brush only remove or return places (the slots that stay keep their centres); a moved
/// border, a brush stroke or a switch fill exactly what a fresh fill would; the layer's «ближние
/// поверх дальних» orders drawing and picking by the lower edge.
/// </summary>
public sealed class PlacementAreaBeautyTests
{
    private static readonly string ShaA = new('a', 64), ShaB = new('b', 64);

    [Fact]
    public void All_switches_off_fill_exactly_like_an_area_without_beauty()
    {
        var plain = Fill(Area(), out _);
        var off = Fill(Area() with { Beauty = new() }, out _);
        Assert.Equal(plain.Count, off.Count);
        foreach (var (id, item) in plain) Assert.Equal(item, off[id]);
    }

    [Fact]
    public void Soft_edge_only_removes_and_shrinks_and_keeps_every_centre()
    {
        var plain = Fill(Area(), out _);
        var soft = Fill(Area() with { Beauty = new() { SoftEdge = true, EdgeWidth = 300 } }, out var area);
        Assert.True(soft.Count < plain.Count);
        foreach (var (id, item) in soft)
        {
            Assert.True(plain.TryGetValue(id, out var was));
            Assert.Equal(was!.X + was.Width / 2, item.X + item.Width / 2, 6);
            Assert.Equal(was.Y + was.Height / 2, item.Y + item.Height / 2, 6);
            Assert.True(item.Width <= was.Width + 1e-9);
        }
        // Near the border what stays is smaller than deep inside.
        var edge = soft.Values.Where(o => Inset(o) < 200).ToArray();
        var deep = soft.Values.Where(o => Inset(o) > 400).ToArray();
        Assert.NotEmpty(edge); Assert.NotEmpty(deep);
        Assert.True(edge.Average(o => o.Width / plain[o.Id].Width) < deep.Average(o => o.Width / plain[o.Id].Width));
        area.ValidateMembers();
    }

    [Fact]
    public void Clumps_and_colour_keep_centres_and_tint_varies_by_at_most_the_percent()
    {
        var plain = Fill(Area(), out _);
        var beauty = Fill(Area() with { Beauty = new() { Clumps = true, ClumpStrength = 60, ColorVariation = true, ColorVariationPercent = 5 } }, out var area);
        Assert.False(plain.Keys.ToHashSet().SetEquals(beauty.Keys));
        foreach (var (id, item) in beauty)
            if (plain.TryGetValue(id, out var was))
            {
                Assert.Equal(was.X, item.X, 9); Assert.Equal(was.Y, item.Y, 9); Assert.Equal(was.Width, item.Width, 9);
            }
        var tints = beauty.Values.Select(o => Convert.ToUInt32(o.Stamp!.Tint![1..], 16)).ToArray();
        Assert.All(tints, t => Assert.InRange((t >> 8) & 0xFF, (uint)Math.Floor(255 * .9) - 1, 255u));
        Assert.True(tints.Distinct().Count() > 5);
        area.ValidateMembers();
    }

    [Fact]
    public void Ground_shadow_and_underlay_are_stored_on_the_object_inside_its_rectangle()
    {
        var items = Fill(Area() with { Beauty = new() { GroundShadow = true, Underlay = true } }, out _);
        Assert.All(items.Values, o =>
        {
            var g = o.Stamp!.GroundShadow!;
            Assert.True(g.IsValid);
            Assert.Equal(.34, g.Opacity, 9); Assert.Equal(.12, g.Mass, 9);
        });
    }

    [Fact]
    public void Species_patches_put_one_kind_in_patches()
    {
        var mixed = Fill(Area(two: true), out _);
        var patches = Fill(Area(two: true) with { Beauty = new() { SpeciesPatches = true, ClumpSize = 400 } }, out _);
        // Neighbours share their kind far more often in patches than when mixed per slot.
        Assert.True(Agreement(patches) > Agreement(mixed) + .15);
    }

    [Fact]
    public void A_moved_vertex_with_a_soft_edge_fills_what_a_fresh_fill_would()
    {
        var session = Session(out var layer);
        var area = Area() with { Beauty = new() { SoftEdge = true, EdgeWidth = 250 } };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var current = Get(session.Document);
        var nodes = current.Nodes.SetItem(1, current.Nodes[1] with { X = 1500, Y = 300 });
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, current with { Nodes = nodes }, "move"));
        Assert.Null(session.LastResourceError);
        var moved = Get(session.Document);
        moved.ValidateMembers();
        var fresh = Fill(current with { Nodes = nodes, Cells = moved.Cells.Clear(), MemberCount = 0 }, out _);
        var stored = Objects(session.Document);
        Assert.Equal(fresh.Count, stored.Count);
        foreach (var (id, item) in fresh) Assert.Equal(item, stored[id]);
    }

    [Fact]
    public void Density_brush_thins_only_under_the_stroke_and_one_command_restores_it()
    {
        var session = Session(out var layer);
        var area = Area();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var plain = Objects(session.Document);
        var work = PlacementAreaDensityBrush.Begin(Get(session.Document));
        for (var x = 300; x <= 900; x += 40)
            PlacementAreaDensityBrush.Dab(work.Left, work.Top, work.CellSize, work.Width, work.Height, work.Values, x, 600, 150, 1,
                PlacementAreaDensityBrushMode.Thinner);
        var mask = PlacementAreaDensityBrush.Finish(work.Left, work.Top, work.CellSize, work.Width, work.Height, work.Values);
        Assert.NotNull(mask);
        var command = new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { DensityMask = mask }, "brush");
        session.Execute(command);
        Assert.Null(session.LastResourceError);
        var brushed = Objects(session.Document);
        Assert.True(brushed.Count < plain.Count);
        // Only the cells under the stroke are visited, not the whole area.
        Assert.True(command.VisitedCells < PlacementAreaGeometry.Coverage(area.Nodes).Sum(s => s.MaxX - s.MinX + 1) / 2);
        foreach (var (id, item) in brushed) Assert.Equal(plain[id], item);
        Assert.DoesNotContain(brushed.Values, o => Math.Abs(o.Y + o.Height / 2 - 600) < 60 && o.X + o.Width / 2 is > 400 and < 800);
        Get(session.Document).ValidateMembers();
        Assert.True(Get(session.Document).NeedsBeautySchema);

        session.Undo();
        Assert.Equal(plain.Count, Objects(session.Document).Count);
    }

    [Fact]
    public void Density_mask_round_trips_through_its_packed_form_and_reads_neutral_outside()
    {
        var values = new byte[16 * 8]; Array.Fill(values, PlacementAreaDensityMask.Neutral); values[3 * 16 + 5] = 0;
        var mask = PlacementAreaDensityMask.Create(100, 200, 10, 16, 8, values);
        var copy = new PlacementAreaDensityMask(mask.Left, mask.Top, mask.CellSize, mask.Width, mask.Height, mask.Data);
        Assert.Equal(mask, copy);
        Assert.Equal(values, copy.CopyValues());
        Assert.Equal(1, copy.Factor(0, 0));
        Assert.Equal(0, copy.Factor(100 + 5.5 * 10, 200 + 3.5 * 10), 9);
        Assert.Null(PlacementAreaDensityBrush.Finish(0, 0, 1, 2, 2, [128, 128, 128, 128]));
        Assert.Throws<ArgumentException>(() => new PlacementAreaDensityMask(0, 0, 10, 4, 4, "not base64!").Validate());
    }

    [Fact]
    public void Layer_depth_sort_orders_drawing_and_picking_by_the_lower_edge()
    {
        var high = Rect(100, 100, 200, 200); // lower edge at 300, listed last
        var low = Rect(150, 150, 200, 200);  // lower edge at 350, listed first
        var layer = new ObjectLayer(LayerId.New(), "L", true, false, 1, [low, high]);
        Assert.Equal([low.Id, high.Id], SceneGeometry.InDrawOrder(layer).Select(o => o.Id));
        var sorted = layer with { Effects = new() { DepthSort = true } };
        Assert.Equal([high.Id, low.Id], SceneGeometry.InDrawOrder(sorted).Select(o => o.Id));
        MapDocument Doc(ObjectLayer l) => new(DocumentId.New(), "D", DateTimeOffset.UnixEpoch, new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "B", true, false, 1, "#FFFFFFFF"), l]);
        Assert.Equal(high.Id, SceneGeometry.HitTest(Doc(layer), 220, 220)!.Id);
        Assert.Equal(low.Id, SceneGeometry.HitTest(Doc(sorted), 220, 220)!.Id);
    }

    private static SceneObject Rect(double x, double y, double w, double h) =>
        new(ObjectId.New(), SceneObjectKind.Rectangle, "r", x, y, w, h, 0, "#FFFF0000", "#00000000", 0, true, false);

    private static double Inset(SceneObject o)
    {
        var cx = o.X + o.Width / 2; var cy = o.Y + o.Height / 2;
        return new[] { cx - 10, 1900 - cx, cy - 10, 1900 - cy }.Min();
    }

    private static double Agreement(Dictionary<ObjectId, SceneObject> items)
    {
        var list = items.Values.ToArray(); var same = 0; var pairs = 0;
        foreach (var a in list)
        {
            var nearest = list.Where(b => b.Id != a.Id).MinBy(b => Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2))!;
            pairs++; if (nearest.Stamp!.AssetSha256 == a.Stamp!.AssetSha256) same++;
        }
        return (double)same / pairs;
    }

    private static Dictionary<ObjectId, SceneObject> Fill(PlacementArea area, out PlacementArea stored)
    {
        var session = Session(out var layer);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        Assert.Null(session.LastResourceError);
        stored = Get(session.Document);
        return Objects(session.Document);
    }

    private static Dictionary<ObjectId, SceneObject> Objects(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Stamp).ToDictionary(o => o.Id);

    private static EditingSession Session(out ObjectLayer layer)
    {
        layer = new(LayerId.New(), "Objects", true, false, 1, []);
        var assets = new[] { ShaA, ShaB }.Select(s => new MapAssetReference(s, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: s)).ToArray();
        return new(new MapDocument(DocumentId.New(), "Areas", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer], assets));
    }

    private static PlacementArea Area(bool two = false) => PlacementAreaPlanner.InitializeComposition(new PlacementArea
    {
        Id = AreaId, Name = "Trees", LayoutVersion = 4, Density = 80, Seed = 18, Pattern = PlacementAreaPattern.Groups,
        Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1900, 10), new(Guid.NewGuid(), 1900, 1900), new(Guid.NewGuid(), 10, 1900)],
        Variants = two ? [new(ShaA, 32, 32), new(ShaB, 32, 48)] : [new(ShaA, 32, 32)], Style = new(ShaA),
        Request = new() { BaseWidth = 40, RandomizeVariant = true },
    });

    private static readonly Guid AreaId = Guid.NewGuid();

    private static PlacementArea Get(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
}
