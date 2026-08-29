using Map.Core.Documents;
using Map.Persistence;
using Map.Rendering;
using SkiaSharp;

if (args is ["--export", var projectPath, var pngPath])
{
    var opened = MapProjectStore.Open(Path.GetFullPath(projectPath));
    var canvas = opened.Document.Canvas;
    var width = checked((int)canvas.Width);
    var height = checked((int)canvas.Height);
    if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384)
    {
        Console.Error.WriteLine($"Export dimensions are outside the bounded evidence range: {width}x{height}.");
        return 4;
    }

    var destination = Path.GetFullPath(pngPath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, RenderDocument(opened.Document, width, height));
    Console.WriteLine($"EXPORTED {width}x{height}: {destination}");
    return 0;
}

if (args is ["--propose"])
{
    var repository = FindRepositoryRoot(AppContext.BaseDirectory);
    var output = Path.Combine(repository, "tests", "fixtures", "grid-goldens");
    var fixtures = Enum.GetValues<GridGeometry>()
        .Select(geometry => (Geometry: geometry, Path: Path.Combine(output, FileName(geometry))))
        .ToArray();

    var existing = fixtures.Where(item => File.Exists(item.Path)).Select(item => item.Path).ToArray();
    if (existing.Length > 0)
    {
        Console.Error.WriteLine("Refusing to overwrite proposed grid golden files:");
        foreach (var path in existing) Console.Error.WriteLine(path);
        return 3;
    }

    Directory.CreateDirectory(output);
    foreach (var fixture in fixtures)
    {
        File.WriteAllBytes(fixture.Path, Render(fixture.Geometry));
        Console.WriteLine($"PROPOSED {fixture.Geometry}: {fixture.Path}");
    }

    return 0;
}

Console.Error.WriteLine("Usage: Map.GridGoldenProposer --propose | --export <project.mapproj> <output.png>");
return 2;

static byte[] Render(GridGeometry geometry)
{
    const int width = 192;
    const int height = 128;
    var grid = new GridDefinition(GridId.New(), geometry, 32)
    {
        Subdivisions = 2,
        LineWidth = 1.5,
        Color = "#FF365A78",
        Opacity = 0.85,
        OffsetX = 11,
        OffsetY = -7,
        RotationDegrees = 13,
        IncludeInExport = true,
    };
    var document = new MapDocument(
        DocumentId.New(), "grid golden", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(width, height, "px", "#FFF4F1E8"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFF4F1E8"),
            new GridLayer(LayerId.New(), "grid", true, false, 1, [grid]),
        ]);
    return RenderDocument(document, width, height);
}

static byte[] RenderDocument(MapDocument document, int width, int height)
{
    var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info)!;
    SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
    surface.Canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static string FileName(GridGeometry geometry) => geometry switch
{
    GridGeometry.Square => "square.png",
    GridGeometry.IsometricSquares => "isometric-squares.png",
    GridGeometry.HexFlat => "hex-flat.png",
    GridGeometry.HexPointy => "hex-pointy.png",
    _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
};

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx")))
        directory = directory.Parent;
    return directory?.FullName
        ?? throw new DirectoryNotFoundException("Could not locate Map.slnx from the tool directory.");
}
