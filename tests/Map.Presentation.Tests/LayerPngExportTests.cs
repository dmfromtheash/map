using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class LayerPngExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r13-layers-").FullName;
    private static ImageExportEngine Engine(Action<ExportFaultPoint>? fault = null) => new(new ExportEngineSettings
    {
        TileEdge = 64, TileCacheCapacity = 4, AvailableMemoryBytes = () => long.MaxValue,
        AvailableDiskBytes = _ => long.MaxValue, FaultInjector = fault,
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Every_visible_layer_is_a_separate_PNG_with_the_same_region_dimensions_and_alpha(bool hex)
    {
        var doc = Document(hex);
        var snapshot = doc;
        var path = Path.Combine(_root, "layers");
        var result = await LayerPngExporter.WriteAsync(Engine(), doc, Request(hex), path);
        var files = Directory.GetFiles(path, "*.png").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(hex ? 4 : 3, result.Files);
        Assert.Equal(result.Files, files.Length);
        Assert.EndsWith("001-Фон.png", files[0]);
        Assert.EndsWith("002-A_B.png", files[1]);
        Assert.EndsWith("003-A_B.png", files[2]);
        Assert.DoesNotContain(files, f => f.Contains("Hidden", StringComparison.Ordinal));
        foreach (var file in files)
        {
            using var png = SKBitmap.Decode(file);
            Assert.Equal(160, png.Width);
            Assert.Equal(120, png.Height);
        }
        using var background = SKBitmap.Decode(files[0]);
        using var first = SKBitmap.Decode(files[1]);
        using var second = SKBitmap.Decode(files[2]);
        // The source region starts at (10,10) and scales by 2. Markers do not shift to the origin.
        Assert.Equal(SKColors.White, background.GetPixel(1, 1));
        Assert.Equal(SKColors.Red, first.GetPixel(30, 30));
        Assert.Equal((byte)0, first.GetPixel(110, 70).Alpha);
        Assert.Equal(SKColors.Blue, second.GetPixel(110, 70));
        Assert.Equal((byte)0, second.GetPixel(30, 30).Alpha);
        Assert.Same(snapshot, doc);
        Assert.All(doc.Layers.Take(3), l => Assert.True(l.Visible));
    }

    [Fact]
    public async Task An_export_only_hidden_grid_cannot_appear_in_other_layers_PNGs()
    {
        var doc = Document(false);
        var grid = new GridDefinition(GridId.New(), GridGeometry.Square, 8)
        { Color = "#FF00FF00", Opacity = 1, IncludeInExport = true, LineWidth = 2 };
        doc = doc.WithLayers([.. doc.Layers, new GridLayer(LayerId.New(), "Hidden grid", false, false, 1, [grid])]);
        var result = await LayerPngExporter.WriteAsync(Engine(), doc, Request(false) with { IncludeGrid = true }, Path.Combine(_root, "grids"));
        Assert.Equal(3, result.Files);
        using var background = SKBitmap.Decode(Directory.GetFiles(result.Path).Order().First());
        Assert.All(background.Pixels, p => Assert.Equal(SKColors.White, p));
    }

    [Fact]
    public async Task Layer_subset_and_hex_level_are_honoured_without_changing_document()
    {
        var doc = Document(true);
        var hex = doc.Layers.OfType<HexLayer>().Single();
        var other = hex with { Id = LayerId.New(), Name = "Underground", Level = -1 };
        doc = doc.WithLayers([.. doc.Layers, other]);
        var request = Request(true) with { VisibleLayerIds = ImmutableHashSet.Create(hex.Id, other.Id) };
        var result = await LayerPngExporter.WriteAsync(Engine(), doc, request, Path.Combine(_root, "subset"));
        Assert.Equal(1, result.Files);
        Assert.DoesNotContain("Underground", Path.GetFileName(Directory.GetFiles(result.Path).Single()));
        Assert.Equal(2, doc.Layers.OfType<HexLayer>().Count());
    }

    [Fact]
    public async Task Failure_cancellation_and_existing_destination_never_publish_partial_files()
    {
        var doc = Document(false);
        var path = Path.Combine(_root, "layers");
        var count = 0;
        await Assert.ThrowsAsync<ExportOperationException>(() => LayerPngExporter.WriteAsync(Engine(point =>
        {
            if (point == ExportFaultPoint.BeforeFinalize && ++count == 2) throw new IOException("injected second layer failure");
        }), doc, Request(false), path));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LayerPngExporter.WriteAsync(Engine(point =>
        {
            if (point == ExportFaultPoint.BeforeFinalize) cancellation.Cancel();
        }), doc, Request(false), path, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.txt"), "previous");
        await Assert.ThrowsAsync<IOException>(() => LayerPngExporter.WriteAsync(Engine(), doc, Request(false), path));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(path, "keep.txt")));
        Assert.Single(Directory.GetFiles(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ViewModel_exports_layers_and_poster_without_touching_document_or_undo(bool hex)
    {
        var doc = Document(hex);
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), imageExporter: Engine(), exportDestinationPicker: new Picker(_root));
        editor.HexPrintOptions = new HexPrintOptions(MapWidthMillimetres: 200, IncludeKey: false);
        editor.BeginExport();
        await editor.WaitForExportPreviewAsync();
        var layers = await editor.ExportLayersAsync();
        var poster = await editor.ExportPosterAsync();
        Assert.NotNull(layers);
        Assert.NotNull(poster);
        Assert.Same(doc, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(1, poster.OverviewPages);
        Assert.Equal((160, 120), (layers.Width, layers.Height));
        editor.CloseExport();
    }

    [Fact]
    public async Task Standard_embedded_asset_reaches_both_the_PDF_and_its_isolated_PNG()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Asset", 20, 20, 16, 16,
            0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(hash) };
        var original = Document(false);
        var doc = new MapDocument(original.Id, original.Title, original.CreatedUtc, original.Canvas,
            [original.Layers[0], new ObjectLayer(LayerId.New(), "Asset", true, false, 1, [stamp])],
            [new MapAssetReference(hash, "Test red square", "image/png", ".png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "Test fixture", "Map tests", bytes)]);
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), imageExporter: Engine(), exportDestinationPicker: new Picker(_root));
        editor.BeginExport();
        await editor.WaitForExportPreviewAsync();
        var pdf = await editor.ExportPosterAsync();
        Assert.NotNull(pdf);
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(File.ReadAllBytes(pdf.Path)));
        var layers = await editor.ExportLayersAsync();
        Assert.NotNull(layers);
        using var png = SKBitmap.Decode(Directory.GetFiles(layers.Path).Order().Last());
        Assert.Equal((160, 120), (png.Width, png.Height));
        Assert.Equal(SKColors.Red, png.GetPixel(25, 25));
        Assert.Same(doc, editor.Session.Document);
        editor.CloseExport();
    }

    private sealed class Picker(string root) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string name, ExportImageFormat format) => Task.FromResult<string?>(null);
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Path.Combine(root, "layers"));
        public Task<string?> PickFileAsync(string name, string extension, string description) => Task.FromResult<string?>(Path.Combine(root, "poster.pdf"));
    }

    private static ExportRequest Request(bool hex) => new(ExportImageFormat.Jpeg, ExportAreaMode.Region,
        new CanvasRegion(10, 10, 80, 60), 160, 120, ExportOutputMode.TilePackage, false, false)
        { Hex = hex ? new HexExportView(true, 0) : null };

    private static MapDocument Document(bool hex)
    {
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "A/B", true, false, 1, [Shape(20, 20, "#FFFF0000")]),
            new ObjectLayer(LayerId.New(), "A:B", true, false, 1, [Shape(60, 40, "#FF0000FF")]),
            new ObjectLayer(LayerId.New(), "Hidden", false, false, 1, [Shape(20, 20, "#FF00FF00")]),
        };
        if (hex) layers.Add(HexMapFactory.Create("Hex", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 16, 4, 4, "#FFFFFFFF").Layers.OfType<HexLayer>().Single());
        return new(DocumentId.New(), "Layers", DateTimeOffset.UnixEpoch, new CanvasDefinition(160, 120, "px", "#FFFFFFFF"), layers,
            category: hex ? new("hex") : MapCategoryId.Standard);
    }

    private static SceneObject Shape(double x, double y, string color) => new(ObjectId.New(), SceneObjectKind.Rectangle,
        "Marker", x, y, 10, 10, 0, color, "#00000000", 0, true, false);
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
