using System.IO.Compression;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage13DLargeCanvasRoundTripTests
{
    [Fact]
    public void Large_sparse_canvas_round_trip_preserves_groups_compositions_tiles_and_expanded_envelope()
    {
        using var temp = new TempWorkspace();
        var path = temp.File("large-131k.mapproj");
        var document = Document();

        MapProjectStore.Save(
            document,
            path,
            new SaveMetadata("stage-13d-test", DateTimeOffset.UnixEpoch),
            ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        ProjectFixtures.AssertSemanticallyEqual(document, reopened);
        Assert.Equal(131_072, reopened.Canvas.Width);
        Assert.Equal(3, reopened.Layers.Sum(layer =>
            (layer is IRasterContentLayer raster ? raster.ContentTiles.TileCount : 0)
            + (layer.Mask?.Tiles.TileCount ?? 0)));
        Assert.Equal(3, reopened.Layers.OfType<ObjectLayer>().Single().Objects.Count);
        Assert.Single(
            reopened.Layers.OfType<ObjectLayer>().Single().Objects,
            item => item.Composition is not null);

        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.SceneEntry)!.Open();
        using var reader = new StreamReader(stream);
        var sceneJson = reader.ReadToEnd();
        Assert.DoesNotContain('\n', sceneJson);
        Assert.DoesNotContain('\r', sceneJson);
        // Structural JSON is capped at 32 MiB per entry since object chunks carry the objects
        // (DEC on the container budget, docs/05 §limits); the total stays at 2 GiB.
        Assert.Equal(32L * 1024 * 1024, MapProjectFormat.MaxJsonEntryBytes);
        Assert.Equal(2_048L * 1024 * 1024, MapProjectFormat.MaxTotalUncompressedBytes);
    }

    private static MapDocument Document()
    {
        var groupId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var first = Rectangle("first", 20, 30) with
        {
            GroupId = groupId,
            GroupName = "kept group",
        };
        var second = Rectangle("second", 80, 90) with
        {
            GroupId = groupId,
            GroupName = "kept group",
        };
        var child = Rectangle("composition child", 2, 3);
        var composition = Rectangle("composition", 130_000, 130_000) with
        {
            Kind = SceneObjectKind.Composition,
            Composition = new EditableComposition(
                CompositionId.New(),
                DocumentId.New(),
                "kept composition",
                64,
                64,
                [child]),
        };
        var far = new TileKey(511, 511);
        var maskPixels = Enumerable.Repeat(
            (byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        maskPixels[0] = 0;
        var objectLayer = new ObjectLayer(
            LayerId.New(),
            "objects",
            true,
            false,
            1,
            [first, second, composition],
            new LayerMask(TileMap.Empty(1).With(far, maskPixels), enabled: true));

        var brushPixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        brushPixels[3] = 255;
        var brush = new BrushLayer(
            LayerId.New(),
            "brush",
            true,
            false,
            1,
            TileMap.Empty(4).With(new TileKey(510, 1), brushPixels));
        var landPixels = new byte[TileMap.TileSize * TileMap.TileSize];
        landPixels[^1] = 255;
        var land = new LandLayer(
            LayerId.New(),
            "land",
            true,
            false,
            1,
            TileMap.Empty(1).With(new TileKey(1, 510), landPixels),
            new LandEdgeStyle());

        return new MapDocument(
            DocumentId.New(),
            "large sparse",
            DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131_072, 131_072, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                objectLayer,
                brush,
                land,
            ]);
    }

    private static SceneObject Rectangle(string name, double x, double y) => new(
        ObjectId.New(),
        SceneObjectKind.Rectangle,
        name,
        x,
        y,
        32,
        32,
        0,
        "#FF557799",
        "#FF112233",
        1,
        visible: true,
        locked: false);
}
