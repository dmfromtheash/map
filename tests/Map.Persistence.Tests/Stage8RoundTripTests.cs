using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage8RoundTripTests
{
    [Fact]
    public void Masked_filter_editable_composition_and_flattened_stamp_survive_reopen()
    {
        using var workspace = new TempWorkspace();
        var source = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label",
            20, 30, 180, 80, 0, "#FF000000", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "editable" } };
        var baseDocument = Document(source);
        var package = CompositionTransfer.Copy(baseDocument, [source.Id], new CompositionCaptureOptions
        { IncludeBackground = true, IncludeGrids = true, IncludeFilters = true, ReferenceGridScale = 64 });
        var editable = CompositionTransfer.CreateInstance(package, 300, 200, 128);
        var png = ProjectFixtures.PreviewPng(); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var asset = new MapAssetReference(hash, "flat", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "flattened composition", "user content", embeddedContent: png);
        var flat = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "flat", 500, 100, 90, 60, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(hash) };
        var filterPixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var filter = new FilterLayer(LayerId.New(), "masked filter", true, false, 0.75,
            new FilterSettings { Brightness = 0.1, HueDegrees = 22, Temperature = -0.2 },
            new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), filterPixels), true));
        var document = new MapDocument(baseDocument.Id, baseDocument.Title, baseDocument.CreatedUtc,
            baseDocument.Canvas,
            [baseDocument.BaseColor, new ObjectLayer(LayerId.New(), "objects", true, false, 1, [editable, flat]), filter],
            [asset]);
        var path = workspace.File("stage8.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equivalent(editable.Composition,
            reopened.Layers.OfType<ObjectLayer>().Single().Objects[0].Composition, strict: true);
        Assert.Equal(hash, reopened.Layers.OfType<ObjectLayer>().Single().Objects[1].Stamp!.AssetSha256);
        Assert.Equivalent(filter.Settings, reopened.Layers.OfType<FilterLayer>().Single().Settings, strict: true);
        Assert.Equal(filterPixels, reopened.Layers.OfType<FilterLayer>().Single().Mask!.Tiles.TryGet(new TileKey(0, 0)));
    }

    private static MapDocument Document(SceneObject source) => new(DocumentId.New(), "stage8", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "source", true, false, 1, [source]),
            new GridLayer(LayerId.New(), "grid", true, false, 1,
                [new GridDefinition(GridId.New(), GridGeometry.Square, 64)]),
            new FilterLayer(LayerId.New(), "filter", true, false, 1, new FilterSettings { Contrast = 0.1 }),
        ]);
}
