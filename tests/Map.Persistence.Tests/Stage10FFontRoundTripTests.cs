using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage10FFontRoundTripTests
{
    [Fact]
    public void Requested_and_fallback_font_families_round_trip_as_distinct_existing_fields()
    {
        using var workspace = new TempWorkspace();
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "font label",
            20, 30, 320, 120, 0, "#FF111111", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Stage 10F",
                RequestedFontFamily = "Map Stage 10F Missing Request",
                FallbackFontFamily = "Arial",
            },
        };
        var document = new MapDocument(DocumentId.New(), "font roundtrip", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(800, 600, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [text]),
            ]);
        var path = workspace.File("stage10f-font.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;
        var reopenedText = reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single().Text!;

        Assert.Equal("Map Stage 10F Missing Request", reopenedText.RequestedFontFamily);
        Assert.Equal("Arial", reopenedText.FallbackFontFamily);
        Assert.NotEqual(reopenedText.RequestedFontFamily, reopenedText.FallbackFontFamily);
    }
}
