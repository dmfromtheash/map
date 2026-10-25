using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class PlacementAreaEcologyTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly River River = new() { Name = "Water", Channels = [RiverEditor.NewChannel([new(600, 0), new(600, 1200)], 60)] };

    private static MapDocument Document(params MapLayer[] layers) => new(DocumentId.New(), "Ecology", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1200, 1200, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), .. layers],
        [new(Sha, "Tree", "image/png", "png", ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha)]);

    private static PlacementArea Area(int seed = 7) => PlacementAreaPlanner.InitializeComposition(new()
    {
        LayoutVersion = 4, Density = 100, SizeVariationPercent = 0, Seed = seed, JoinEdges = true,
        Nodes = [new(Guid.NewGuid(), 30, 30), new(Guid.NewGuid(), 1170, 30), new(Guid.NewGuid(), 1170, 1170), new(Guid.NewGuid(), 30, 1170)],
        Variants = [new(Sha, 24, 32)], Style = new(Sha), Request = new() { BaseWidth = 24 },
    });

    [Fact]
    public void Seeded_distribution_is_denser_at_the_centre_and_near_existing_water()
    {
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []) { Rivers = [River] };
        var document = Document(layer);
        var edgeTotal = 0; var edgeKept = 0; var centreTotal = 0; var centreKept = 0;
        var wetTotal = 0; var wetKept = 0; var dryTotal = 0; var dryKept = 0;
        foreach (var seed in new[] { 7, 18, 41, 93 })
        {
            var area = Area(seed); var ecology = new PlacementAreaEcology(document, area, true);
            var sampler = new PlacementAreaSampler(area);
            foreach (var span in PlacementAreaGeometry.Coverage(area.Nodes))
            for (var x = span.MinX; x <= span.MaxX; x++)
            foreach (var slot in sampler.Slots(x, span.Y))
            {
                var item = sampler.Item(slot); var foot = PlacementAreaRuleCheck.Foot(item);
                var kept = !ecology.Blocks(slot, item);
                var edge = Math.Min(Math.Min(foot.X - 30, 1170 - foot.X), Math.Min(foot.Y - 30, 1170 - foot.Y));
                var riverDistance = Math.Abs(foot.X - 600);
                if (riverDistance > 200 && edge is >= 0 and <= 24) { edgeTotal++; if (kept) edgeKept++; }
                if (riverDistance > 200 && edge > 150) { centreTotal++; if (kept) centreKept++; }
                if (edge > 150 && riverDistance is > 65 and < 95) { wetTotal++; if (kept) wetKept++; }
                if (edge > 150 && riverDistance > 200) { dryTotal++; if (kept) dryKept++; }
                if (RiverGeometry.Contains(River, foot.X, foot.Y)) Assert.False(kept);
            }
        }
        Assert.True(edgeTotal > 100 && centreTotal > 100 && wetTotal > 100 && dryTotal > 100);
        var edgeRate = (double)edgeKept / edgeTotal; var centreRate = (double)centreKept / centreTotal;
        var wetRate = (double)wetKept / wetTotal; var dryRate = (double)dryKept / dryTotal;
        Assert.True(centreRate > edgeRate + .15, $"centre={centreRate:F3}, edge={edgeRate:F3}");
        Assert.True(wetRate > dryRate + .15, $"near water={wetRate:F3}, dry={dryRate:F3}");
    }

    [Fact]
    public void Preview_count_and_command_agree_and_undo_keeps_the_exact_slots()
    {
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []) { Rivers = [River] };
        var document = Document(layer); var area = Area() with { Density = 72 };
        var ecology = new PlacementAreaEcology(document, area, true);
        var count = PlacementAreaPlanner.Count(area, ecology: ecology);
        var preview = PlacementAreaPlanner.Preview(area, ecology: ecology);
        var session = new EditingSession(document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "forest", forestEcology: true));
        Assert.Null(session.LastResourceError);
        var actualLayer = (ObjectLayer)session.Document.FindLayer(layer.Id)!; var saved = Assert.Single(actualLayer.PlacementAreas);
        saved.ValidateMembers(); Assert.NotEmpty(saved.RuleBlocked); Assert.Equal(count, saved.MemberCount);
        Assert.NotEmpty(preview);
        Assert.All(preview, item => Assert.Equal(item, actualLayer.Objects.Single(o => o.Id == item.Id)));
        var after = session.Document; session.Undo(); Assert.Same(document, session.Document);
        session.Redo(); Assert.Same(after, session.Document);
    }

    [Fact]
    public void Rocks_require_an_existing_enabled_level_cliff_and_do_not_invent_slope_on_flat_land()
    {
        var tiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        var land = new LandLayer(LayerId.New(), "Plateau", true, false, 1, tiles, new()) { Level = 3 };
        var area = Area(); var slot = new PlacementAreaSlot(0, 0, 0);
        static SceneObject At(double x, double y) => new(ObjectId.New(), SceneObjectKind.Stamp, "Rock", x - 10, y - 16, 20, 20, 0, "#FFFFFFFF", "#00000000", 0, true, false);
        Assert.False(new PlacementAreaEcology(Document(land), area, false).Blocks(slot, At(245, 128)));
        Assert.True(new PlacementAreaEcology(Document(land), area, false).Blocks(slot, At(128, 128)));
        Assert.True(new PlacementAreaEcology(Document(land with { Level = 0 }), area, false).Blocks(slot, At(245, 128)));
        Assert.True(new PlacementAreaEcology(Document(land with { EdgeStyle = land.EdgeStyle with { Cliff = land.EdgeStyle.Cliff with { Height = 0 } } }), area, false).Blocks(slot, At(245, 128)));
        Assert.True(new PlacementAreaEcology(Document(), area, false).Blocks(slot, At(245, 128)));
    }
}
