using Map.Core.Documents;
using Map.Persistence;

namespace Map.Persistence.Tests.Support;

/// <summary>Deterministic fixtures: fixed IDs and timestamps so every comparison is exact.</summary>
public static class ProjectFixtures
{
    public static readonly DocumentId FixedDocumentId =
        DocumentId.Parse("11111111-2222-3333-4444-555555555555");

    public static readonly LayerId FixedLayerId =
        LayerId.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static readonly LayerId FixedBaseLayerId =
        LayerId.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

    public static readonly ObjectId FixedRectangleId =
        ObjectId.Parse("01234567-89ab-cdef-0123-456789abcdef");

    public static readonly ObjectId FixedEllipseId =
        ObjectId.Parse("fedcba98-7654-3210-fedc-ba9876543210");

    public static readonly DateTimeOffset FixedCreatedUtc =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset FixedModifiedUtc =
        new(2026, 8, 25, 12, 34, 56, TimeSpan.Zero);

    /// <summary>
    /// Tiny but real PNG header: signature plus a well-formed 1x1 RGBA IHDR, then filler bytes.
    /// The reader validates the signature and IHDR dimensions only - it never decodes pixels -
    /// and persistence stores the caller's bytes verbatim.
    /// </summary>
    public static byte[] PreviewPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk length + type
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // width = 1, height = 1
        0x08, 0x06, 0x00, 0x00, 0x00,                   // bit depth 8, colour type 6 (RGBA)
        0x1F, 0x15, 0xC4, 0x89,                         // IHDR CRC
        1, 2, 3, 4, 5, 6, 7, 8,                         // filler; stored and compared verbatim
    ];

    public static SaveMetadata Metadata() => new("0.1.0-test", FixedModifiedUtc);

    public static MapDocument Document(string title = "Fixture map")
    {
        var rectangle = new SceneObject(
            FixedRectangleId, SceneObjectKind.Rectangle, "keep",
            x: 96.5, y: -12.25, width: 200, height: 120, rotationDegrees: 30,
            fillColor: "#FF3E5C76", strokeColor: "#FF1B263B", strokeWidth: 2.5,
            visible: true, locked: false);

        var ellipse = new SceneObject(
            FixedEllipseId, SceneObjectKind.Ellipse, "moon",
            x: 512, y: 384, width: 64, height: 48, rotationDegrees: 0,
            fillColor: "#80FFD60A", strokeColor: "#FF000000", strokeWidth: 0,
            visible: false, locked: true);

        var layer = new ObjectLayer(
            FixedLayerId, "ground", visible: true, locked: false, opacity: 0.85,
            [rectangle, ellipse]);

        return new MapDocument(
            FixedDocumentId, title, FixedCreatedUtc,
            new CanvasDefinition(1920, 1080, "px", "#FF10131A"),
            [
                new BaseColorLayer(
                    FixedBaseLayerId, "background", true, false, 1.0, "#FF10131A"),
                layer,
            ]);
    }

    /// <summary>Field-by-field semantic equality with exact values, independent of record Equals.</summary>
    public static void AssertSemanticallyEqual(MapDocument expected, MapDocument actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);

        Assert.Equal(expected.Canvas.Width, actual.Canvas.Width);
        Assert.Equal(expected.Canvas.Height, actual.Canvas.Height);
        Assert.Equal(expected.Canvas.Unit, actual.Canvas.Unit);
        Assert.Equal(expected.Canvas.Background, actual.Canvas.Background);

        Assert.Equal(expected.Layers.Count, actual.Layers.Count);
        for (var l = 0; l < expected.Layers.Count; l++)
        {
            var expectedLayer = expected.Layers[l];
            var actualLayer = actual.Layers[l];

            Assert.Equal(expectedLayer.Id, actualLayer.Id);
            Assert.Equal(expectedLayer.Name, actualLayer.Name);
            Assert.Equal(expectedLayer.Visible, actualLayer.Visible);
            Assert.Equal(expectedLayer.Locked, actualLayer.Locked);
            Assert.Equal(expectedLayer.Opacity, actualLayer.Opacity);
            Assert.Equal(expectedLayer.Blend, actualLayer.Blend);
            Assert.Equal(expectedLayer.GetType(), actualLayer.GetType());

            if (expectedLayer is BaseColorLayer expectedBase)
            {
                Assert.Equal(expectedBase.Color, ((BaseColorLayer)actualLayer).Color);
            }

            if (expectedLayer is ObjectLayer expectedObjects)
            {
                var actualObjects = (ObjectLayer)actualLayer;
                Assert.Equal(expectedObjects.Objects.Count, actualObjects.Objects.Count);
                for (var o = 0; o < expectedObjects.Objects.Count; o++)
                {
                    var expectedObject = expectedObjects.Objects[o];
                    var actualObject = actualObjects.Objects[o];

                    Assert.Equal(expectedObject.Id, actualObject.Id);
                    Assert.Equal(expectedObject.Kind, actualObject.Kind);
                    Assert.Equal(expectedObject.Name, actualObject.Name);
                    Assert.Equal(expectedObject.X, actualObject.X);
                    Assert.Equal(expectedObject.Y, actualObject.Y);
                    Assert.Equal(expectedObject.Width, actualObject.Width);
                    Assert.Equal(expectedObject.Height, actualObject.Height);
                    Assert.Equal(expectedObject.RotationDegrees, actualObject.RotationDegrees);
                    Assert.Equal(expectedObject.FillColor, actualObject.FillColor);
                    Assert.Equal(expectedObject.StrokeColor, actualObject.StrokeColor);
                    Assert.Equal(expectedObject.StrokeWidth, actualObject.StrokeWidth);
                    Assert.Equal(expectedObject.Visible, actualObject.Visible);
                    Assert.Equal(expectedObject.Locked, actualObject.Locked);
                    Assert.Equal(expectedObject.Sublayer, actualObject.Sublayer);
                    Assert.Equal(expectedObject.GroupId, actualObject.GroupId);
                    Assert.Equal(expectedObject.ObjectOpacity, actualObject.ObjectOpacity);
                    Assert.Equal(expectedObject.Stamp, actualObject.Stamp);
                }
            }

            if (expectedLayer is IRasterContentLayer expectedRaster)
            {
                AssertTilesEqual(expectedRaster.ContentTiles, ((IRasterContentLayer)actualLayer).ContentTiles);
            }

            Assert.Equal(expectedLayer.Mask?.Enabled, actualLayer.Mask?.Enabled);
            if (expectedLayer.Mask is { } expectedMask)
            {
                AssertTilesEqual(expectedMask.Tiles, actualLayer.Mask!.Tiles);
            }
        }
    }

    private static void AssertTilesEqual(TileMap expected, TileMap actual)
    {
        Assert.Equal(expected.BytesPerPixel, actual.BytesPerPixel);
        Assert.Equal(expected.Keys.OrderBy(key => key.X).ThenBy(key => key.Y),
            actual.Keys.OrderBy(key => key.X).ThenBy(key => key.Y));
        foreach (var key in expected.Keys)
        {
            Assert.Equal(expected.TryGet(key), actual.TryGet(key));
        }
    }
}

/// <summary>Isolated directory per test.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "mapproj-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not turn a passing test red; the OS reclaims it later.
        }
    }
}
