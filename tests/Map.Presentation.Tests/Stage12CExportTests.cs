using System.Collections.Concurrent;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage12CExportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resource_admission_refuses_before_render_and_preserves_the_existing_result(bool memory)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "previous.png");
        File.WriteAllBytes(path, [7, 4, 2]);
        var allocations = new List<ExportSurfaceAllocation>();
        var exporter = new ImageExportEngine(new ExportEngineSettings
        {
            AvailableMemoryBytes = () => memory ? 0 : long.MaxValue,
            AvailableDiskBytes = _ => memory ? long.MaxValue : 0,
            SurfaceAllocated = allocations.Add,
        });
        var document = Document(32, 24);
        await Assert.ThrowsAsync<ExportValidationException>(() => exporter.ExportAsync(document,
            exporter.Plan(document, Request(32, 24)), path, null));
        Assert.Empty(allocations);
        Assert.Equal(new byte[] { 7, 4, 2 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Stage12C_PlannerRejectsInvalidGeometryAndRequiresTilesBeyond16K()
    {
        var document = Document(64, 48);
        var exporter = new ImageExportEngine();

        Assert.Throws<ExportValidationException>(() => exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.Region, new CanvasRegion(60, 0, 8, 8),
            100, 100, ExportOutputMode.SingleImage, false, true)));
        Assert.Throws<ExportValidationException>(() => exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Jpeg, ExportAreaMode.WholeCanvas, null,
            100, 100, ExportOutputMode.SingleImage, true, true)));
        Assert.Throws<ExportValidationException>(() => exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            16_385, 100, ExportOutputMode.SingleImage, false, true)));

        var tiled = exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            20_000, 100, ExportOutputMode.TilePackage, false, true));
        Assert.Equal(10, tiled.Columns);
        Assert.Equal(1, tiled.Rows);
        Assert.True(tiled.EstimatedRawWorkingBytes > 0);
        Assert.True(tiled.EffectBleedPixels > 0);
    }

    [Fact]
    public async Task Stage12C_PngJpegWebpHeadersDimensionsAndAlphaPoliciesAreReal()
    {
        using var temp = new TempDirectory();
        var document = Document(40, 24, background: "#FF204060");
        var exporter = new ImageExportEngine();

        var png = Path.Combine(temp.Path, "transparent.png");
        await ExportSingle(exporter, document, png, ExportImageFormat.Png, transparent: true);
        using (var codec = Codec(png))
        {
            Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
            Assert.Equal((40, 24), (codec.Info.Width, codec.Info.Height));
        }
        using (var bitmap = SKBitmap.Decode(png))
            Assert.Equal(0, bitmap.GetPixel(1, 1).Alpha);

        var jpeg = Path.Combine(temp.Path, "opaque.jpg");
        await ExportSingle(exporter, document, jpeg, ExportImageFormat.Jpeg, transparent: false);
        using (var codec = Codec(jpeg))
        {
            Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.Equal((40, 24), (codec.Info.Width, codec.Info.Height));
            Assert.Equal(SKAlphaType.Opaque, codec.Info.AlphaType);
        }
        using (var bitmap = SKBitmap.Decode(jpeg))
            Assert.Equal(255, bitmap.GetPixel(1, 1).Alpha);

        var webp = Path.Combine(temp.Path, "transparent.webp");
        await ExportSingle(exporter, document, webp, ExportImageFormat.Webp, transparent: true);
        using (var codec = Codec(webp))
        {
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            Assert.Equal((40, 24), (codec.Info.Width, codec.Info.Height));
        }
        using (var bitmap = SKBitmap.Decode(webp))
            Assert.Equal(0, bitmap.GetPixel(1, 1).Alpha);
    }

    [Fact]
    public async Task Stage12C_RegionGridMasterAndPreviewUseTheFinalScenePolicyWithoutChrome()
    {
        using var temp = new TempDirectory();
        var objectLayerId = LayerId.New();
        var rectangle = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "region marker",
            20, 10, 10, 10, 0, "#FFFF0000", "#00000000", 0, true, false);
        var exportGrid = new GridDefinition(GridId.New(), GridGeometry.Square, 8)
        {
            Color = "#FF00FF00",
            Opacity = 1,
            LineWidth = 1,
            IncludeInExport = true,
        };
        var document = new MapDocument(
            DocumentId.New(), "region", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(64, 48, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(objectLayerId, "objects", true, false, 1, [rectangle]),
                // Hidden in the editor, explicitly enabled for export.
                new GridLayer(LayerId.New(), "grid", false, false, 1, [exportGrid]),
            ]);
        var exporter = new ImageExportEngine();
        var request = new ExportRequest(
            ExportImageFormat.Png,
            ExportAreaMode.Region,
            new CanvasRegion(16, 8, 24, 24),
            48,
            48,
            ExportOutputMode.SingleImage,
            TransparentBackground: true,
            IncludeGrid: true);
        var plan = exporter.Plan(document, request);
        var finalPath = Path.Combine(temp.Path, "region.png");
        await exporter.ExportAsync(document, plan, finalPath, stamps: null);
        var preview = await exporter.RenderPreviewAsync(document, plan, stamps: null);

        using var finalBitmap = SKBitmap.Decode(finalPath);
        using var previewBitmap = Decode(preview.PngBytes);
        AssertBitmapsEqual(finalBitmap, previewBitmap, tolerance: 0);
        Assert.True(finalBitmap.GetPixel(8, 8).Red > 200); // offset region translated to output
        Assert.True(finalBitmap.GetPixel(0, 0).Green > 150); // hidden editor grid exported

        var noGridPlan = exporter.Plan(document, request with { IncludeGrid = false });
        var noGridPath = Path.Combine(temp.Path, "region-no-grid.png");
        await exporter.ExportAsync(document, noGridPlan, noGridPath, stamps: null);
        using var noGrid = SKBitmap.Decode(noGridPath);
        Assert.Equal(0, noGrid.GetPixel(0, 0).Alpha);

        var excludedDocument = document.WithLayer(
            document.Layers.OfType<GridLayer>().Single().Id,
            layer => ((GridLayer)layer) with
            {
                Grids = [exportGrid with { IncludeInExport = false }],
            });
        var excludedPath = Path.Combine(temp.Path, "region-excluded-grid.png");
        await exporter.ExportAsync(
            excludedDocument, exporter.Plan(excludedDocument, request), excludedPath, stamps: null);
        using var excluded = SKBitmap.Decode(excludedPath);
        Assert.Equal(0, excluded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task Stage12C_SingleFileFailureAndCancellationPreserveOldDestinationByteForByte()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "protected.png");
        var oldBytes = new byte[] { 7, 4, 2, 9, 1 };
        await File.WriteAllBytesAsync(destination, oldBytes);
        var document = Document(32, 24);

        var injected = new ImageExportEngine(new ExportEngineSettings
        {
            FaultInjector = point =>
            {
                if (point == ExportFaultPoint.BeforeFinalize) throw new IOException("injected");
            },
        });
        var plan = injected.Plan(document, Request(32, 24));
        await Assert.ThrowsAsync<ExportOperationException>(() =>
            injected.ExportAsync(document, plan, destination, stamps: null));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(destination));
        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Path), path => path != destination);

        using var cancellation = new CancellationTokenSource();
        var cancelled = new ImageExportEngine(new ExportEngineSettings
        {
            FaultInjector = point =>
            {
                if (point == ExportFaultPoint.AfterRender) cancellation.Cancel();
            },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ExportAsync(
            document,
            cancelled.Plan(document, Request(32, 24)),
            destination,
            stamps: null,
            cancellationToken: cancellation.Token));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Stage12C_TilePackageIsBoundedCompleteAndReassemblesAcrossBlurBoundary()
    {
        using var temp = new TempDirectory();
        var document = BlurBoundaryDocument();
        var allocations = new ConcurrentBag<ExportSurfaceAllocation>();
        var settings = new ExportEngineSettings
        {
            TileEdge = 32,
            TileCacheCapacity = 8,
            SurfaceAllocated = allocations.Add,
        };
        var exporter = new ImageExportEngine(settings);
        var progress = new InlineProgress<ExportProgress>();
        var singlePath = Path.Combine(temp.Path, "single.png");
        var singlePlan = exporter.Plan(document, Request(96, 48));
        await exporter.ExportAsync(document, singlePlan, singlePath, stamps: null);

        var packagePath = Path.Combine(temp.Path, "tiles");
        var tilePlan = exporter.Plan(document, Request(
            96, 48, outputMode: ExportOutputMode.TilePackage));
        await exporter.ExportAsync(document, tilePlan, packagePath, stamps: null, progress);

        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(
            await File.ReadAllBytesAsync(Path.Combine(packagePath, "manifest.json")))!;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal((3, 2, 6), (manifest.Columns, manifest.Rows, manifest.Tiles.Count));
        Assert.Equal("tile-y0000-x0000.png", manifest.Tiles[0].File);
        Assert.All(manifest.Tiles, tile => Assert.True(File.Exists(Path.Combine(packagePath, tile.File))));
        Assert.DoesNotContain(
            allocations,
            item => item.Purpose == ExportSurfacePurpose.TileWithBleed
                && (item.Width > tilePlan.TileEdge + 2 * tilePlan.EffectBleedPixels
                    || item.Height > tilePlan.TileEdge + 2 * tilePlan.EffectBleedPixels));
        Assert.DoesNotContain(
            allocations,
            item => item.Purpose == ExportSurfacePurpose.TileWithBleed
                && (item.Width == tilePlan.OutputWidth || item.Height == tilePlan.OutputHeight));
        Assert.Equal(1, progress.Values[^1].Fraction);
        Assert.All(progress.Values.Zip(progress.Values.Skip(1)), pair =>
            Assert.True(pair.First.Fraction <= pair.Second.Fraction));
        Assert.Contains(progress.Values, item => item.Phase == ExportPhase.Rendering);
        Assert.Contains(progress.Values, item => item.Phase == ExportPhase.EncodingAndWriting);
        Assert.Contains(progress.Values, item => item.Phase == ExportPhase.Completed);

        using var single = SKBitmap.Decode(singlePath);
        using var reassembled = new SKBitmap(new SKImageInfo(
            manifest.OutputWidth, manifest.OutputHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(reassembled))
        {
            canvas.Clear(SKColors.Transparent);
            foreach (var tile in manifest.Tiles)
            {
                using var bitmap = SKBitmap.Decode(Path.Combine(packagePath, tile.File));
                canvas.DrawBitmap(bitmap, tile.X, tile.Y);
            }
            canvas.Flush();
        }
        AssertBitmapsEqual(single, reassembled, tolerance: 0);

        // Independent whole-surface scene evaluation catches a shared tiled-path mistake,
        // including a missing effect halo that single/package equality alone cannot detect.
        using var referenceSurface = SKSurface.Create(new SKImageInfo(96, 48))!;
        referenceSurface.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(referenceSurface.Canvas, document, new RenderOptions
        {
            IsExport = true, ViewScale = 1, VisibleWorld = new WorldBounds(0, 0, 96, 48),
        });
        using var referenceImage = referenceSurface.Snapshot();
        using var reference = SKBitmap.FromImage(referenceImage);
        AssertBitmapsEqual(reference, single, tolerance: 1);
    }

    [Fact]
    public async Task Stage12C_CancelBetweenTilesCleansTempAndReportsNoFalseSuccess()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var completedTiles = 0;
        var exporter = new ImageExportEngine(new ExportEngineSettings
        {
            TileEdge = 24,
            TileCacheCapacity = 4,
            FaultInjector = point =>
            {
                if (point == ExportFaultPoint.AfterTileWritten && ++completedTiles == 1)
                    cancellation.Cancel();
            },
        });
        var document = BlurBoundaryDocument();
        var destination = Path.Combine(temp.Path, "cancelled-tiles");
        var plan = exporter.Plan(document, Request(
            96, 48, outputMode: ExportOutputMode.TilePackage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            document, plan, destination, stamps: null, cancellationToken: cancellation.Token));
        Assert.Equal(1, completedTiles);
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Stage12C_EditorExportKeepsSourceStateAndRunningOperationUsesCapturedSnapshot()
    {
        using var temp = new TempDirectory();
        var document = Document(64, 48);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var exporter = new BlockingExporter();
        var picker = new FakeExportPicker
        {
            ImagePath = Path.Combine(temp.Path, "captured.png"),
        };
        var editor = Editor(document, exporter, picker);
        editor.BeginExport();
        await editor.WaitForExportPreviewAsync();

        var originalRevision = editor.Session.Revision;
        var originalPath = editor.DocumentPath;
        var operation = editor.StartExportAsync();
        await exporter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(document, exporter.CapturedSnapshot);

        editor.Session.Execute(new AddObjectCommand(layer.Id, new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "later edit",
            2, 2, 4, 4, 0, "#FFFFFFFF", "#00000000", 0, true, false)));
        var edited = editor.Session.Document;
        exporter.Release.TrySetResult();
        Assert.True(await operation);

        Assert.Same(edited, editor.Session.Document);
        Assert.Equal(originalRevision + 1, editor.Session.Revision);
        Assert.Equal(originalPath, editor.DocumentPath);
        Assert.True(editor.Session.IsDirty);
        Assert.Empty(editor.Session.Document.Checkpoints);
        Assert.Same(document, exporter.CapturedSnapshot);
    }

    [Fact]
    public async Task Stage12C_LateCancelledPreviewCannotOverwriteNewerSettings()
    {
        var exporter = new ReorderedPreviewExporter();
        var editor = Editor(Document(64, 48), exporter, new FakeExportPicker());
        editor.BeginExport();
        await exporter.WaitForCallsAsync(1);
        editor.SetExportFormat(ExportImageFormat.Webp);
        // A cancelled native render may finish late, but a new one must wait for its buffers.
        Assert.Equal(1, editor.ActiveResourceOperations);
        exporter.Complete(0, [1]);
        await exporter.WaitForCallsAsync(2);
        Assert.Null(editor.ExportPreviewPng);
        exporter.Complete(1, [2]);
        await editor.WaitForExportPreviewAsync();
        Assert.Equal(0, editor.ActiveResourceOperations);
        Assert.Equal(new byte[] { 2 }, editor.ExportPreviewPng);
        Assert.Equal(ExportImageFormat.Webp, editor.CurrentExportPlan!.Format);
    }

    private static EditorViewModel Editor(
        MapDocument document, IImageExporter exporter, IExportDestinationPicker picker) => new(
            document,
            "C:\\maps\\source.mapproj",
            startDirty: false,
            new FakeProjectStore(),
            new FakeRecentMaps(),
            new FakeFilePicker(),
            new FakePrompts(),
            new FakeLog(),
            imageExporter: exporter,
            exportDestinationPicker: picker,
            folderLauncher: new FakeFolderLauncher());

    private static async Task ExportSingle(
        IImageExporter exporter,
        MapDocument document,
        string path,
        ExportImageFormat format,
        bool transparent)
    {
        var request = new ExportRequest(
            format,
            ExportAreaMode.WholeCanvas,
            null,
            document.Canvas.Width,
            document.Canvas.Height,
            ExportOutputMode.SingleImage,
            transparent,
            IncludeGrid: true,
            Quality: 90);
        await exporter.ExportAsync(document, exporter.Plan(document, request), path, stamps: null);
    }

    private static ExportRequest Request(
        int width,
        int height,
        ExportOutputMode outputMode = ExportOutputMode.SingleImage) => new(
            ExportImageFormat.Png,
            ExportAreaMode.WholeCanvas,
            null,
            width,
            height,
            outputMode,
            TransparentBackground: false,
            IncludeGrid: true);

    private static MapDocument Document(int width, int height, string background = "#FF182430") => new(
        DocumentId.New(),
        "export map",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(width, height, "px", background),
        [
            new BaseColorLayer(LayerId.New(), "base", true, false, 1, background),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, []),
        ]);

    private static MapDocument BlurBoundaryDocument()
    {
        var shape = VectorGeometry.CreateShape(
            ObjectId.New(),
            "blur across tile",
            ShapePrimitiveKind.Rectangle,
            new WorldBounds(27, 12, 43, 36),
            new ShapeFillStyle { Color = "#FFFF6A35" },
            new VectorStrokeStyle
            {
                Color = "#FFFFFFFF",
                Width = 3,
                Blur = 4,
                ShadowOffsetX = 5,
                ShadowOffsetY = 2,
                ShadowBlur = 4,
                ShadowOpacity = 0.65,
            });
        return new MapDocument(
            DocumentId.New(), "seam", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(96, 48, "px", "#FF13202A"),
            [
                new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF13202A"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [shape]),
            ]);
    }

    private static SKCodec Codec(string path)
    {
        var data = SKData.Create(path)!;
        var codec = SKCodec.Create(data)!;
        data.Dispose();
        return codec;
    }

    private static SKBitmap Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        return SKBitmap.Decode(data);
    }

    private static void AssertBitmapsEqual(SKBitmap expected, SKBitmap actual, int tolerance)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var a = expected.GetPixel(x, y);
            var b = actual.GetPixel(x, y);
            Assert.InRange(Math.Abs(a.Red - b.Red), 0, tolerance);
            Assert.InRange(Math.Abs(a.Green - b.Green), 0, tolerance);
            Assert.InRange(Math.Abs(a.Blue - b.Blue), 0, tolerance);
            Assert.InRange(Math.Abs(a.Alpha - b.Alpha), 0, tolerance);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "map-stage12c-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FakeExportPicker : IExportDestinationPicker
    {
        public string? ImagePath { get; init; }
        public string? PackagePath { get; init; }
        public Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format) =>
            Task.FromResult(ImagePath);
        public Task<string?> PickTilePackageAsync(string suggestedFolderName) =>
            Task.FromResult(PackagePath);
    }

    private sealed class FakeFolderLauncher : IFolderLauncher
    {
        public void OpenContainingFolder(string resultPath, ExportOutputMode outputMode)
        {
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class BlockingExporter : IImageExporter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MapDocument? CapturedSnapshot { get; private set; }

        public ExportPlan Plan(MapDocument document, ExportRequest request) =>
            ExportPlanner.Create(document, request, new ExportEngineSettings());

        public Task<ExportPreview> RenderPreviewAsync(
            MapDocument snapshot, ExportPlan plan, IStampImageSource? stamps,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExportPreview([9], 1, 1));

        public async Task<ExportResult> ExportAsync(
            MapDocument snapshot, ExportPlan plan, string destinationPath, IStampImageSource? stamps,
            IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CapturedSnapshot = snapshot;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ExportResult(destinationPath, plan.OutputMode, 1);
        }
    }

    private sealed class ReorderedPreviewExporter : IImageExporter
    {
        private readonly List<TaskCompletionSource<ExportPreview>> _calls = [];

        public ExportPlan Plan(MapDocument document, ExportRequest request) =>
            ExportPlanner.Create(document, request, new ExportEngineSettings());

        public Task<ExportPreview> RenderPreviewAsync(
            MapDocument snapshot, ExportPlan plan, IStampImageSource? stamps,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ExportPreview>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_calls) _calls.Add(completion);
            return completion.Task; // deliberately ignores cancellation to prove the generation guard
        }

        public Task<ExportResult> ExportAsync(
            MapDocument snapshot, ExportPlan plan, string destinationPath, IStampImageSource? stamps,
            IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_calls)
                {
                    if (_calls.Count >= count) return;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException($"Expected {count} preview calls.");
        }

        public void Complete(int index, byte[] bytes)
        {
            TaskCompletionSource<ExportPreview> completion;
            lock (_calls) completion = _calls[index];
            completion.TrySetResult(new ExportPreview(bytes, 1, 1));
        }
    }
}
