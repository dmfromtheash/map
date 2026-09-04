using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public partial class Stage10EGridRoundTripTests
{
    [System.Text.RegularExpressions.GeneratedRegex("\"sceneSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial System.Text.RegularExpressions.Regex SceneShaRegex();

    [Fact]
    public void Four_geometries_and_every_grid_property_survive_save_and_reopen()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("four-grids.mapproj");
        var document = Document([.. Enum.GetValues<GridGeometry>()
            .Select((geometry, index) => Grid(geometry) with
            {
                CellSize = 96 + index,
                Subdivisions = index + 1,
                LineWidth = 2.5 + index,
                Color = $"#CC{index + 1:X2}4050",
                Opacity = 0.65,
                OffsetX = 13 + index,
                OffsetY = -17 - index,
                RotationDegrees = -725.5 + index,
                SnapEnabled = index % 2 == 0,
                IncludeInExport = index % 2 != 0,
            })]);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        ProjectFixtures.AssertSemanticallyEqual(document, reopened);
        Assert.Equal(
            [GridGeometry.Square, GridGeometry.IsometricSquares, GridGeometry.HexFlat, GridGeometry.HexPointy],
            reopened.Layers.OfType<GridLayer>().Single().Grids.Select(grid => grid.Geometry));
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open());
        Assert.True(JsonTestMutations.ContainsFragment(
            reader.ReadToEnd(), $"\"schemaVersion\": {MapProjectFormat.SchemaVersion}"));
    }

    [Fact]
    public void A_pre_stage10e_project_opens_without_rewriting_its_only_copy()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("old-grid-project.mapproj");
        var oldDocument = Document(
            Grid(GridGeometry.Square), Grid(GridGeometry.HexFlat), Grid(GridGeometry.HexPointy));
        MapProjectStore.Save(oldDocument, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var before = File.ReadAllBytes(path);

        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equal(before, File.ReadAllBytes(path));
        ProjectFixtures.AssertSemanticallyEqual(oldDocument, reopened);
    }

    [Fact]
    public void Unknown_geometry_is_corruption_and_is_never_silently_replaced_with_square()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("future-grid.mapproj");
        MapProjectStore.Save(
            Document(Grid(GridGeometry.IsometricSquares)), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, scene => JsonTestMutations.ReplaceFirst(
            scene,
            "\"geometry\": \"IsometricSquares\"",
            "\"geometry\": \"FutureGrid\""));
        var poisoned = File.ReadAllBytes(path);

        var error = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));

        Assert.Contains("scene.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(poisoned, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("\"cellSize\": 128", "\"cellSize\": \"NaN\"")]
    [InlineData("\"cellSize\": 128", "\"cellSize\": 0")]
    [InlineData("\"subdivisions\": 4", "\"subdivisions\": 0")]
    [InlineData("\"subdivisions\": 4", "\"subdivisions\": 65")]
    [InlineData("\"lineWidth\": 2.5", "\"lineWidth\": 0")]
    [InlineData("\"lineWidth\": 2.5", "\"lineWidth\": \"Infinity\"")]
    [InlineData("\"color\": \"#CC334455\"", "\"color\": \"broken\"")]
    [InlineData("\"opacity\": 0.65", "\"opacity\": 1.1")]
    [InlineData("\"offsetX\": 13", "\"offsetX\": \"Infinity\"")]
    [InlineData("\"rotationDegrees\": -725.5", "\"rotationDegrees\": \"NaN\"")]
    public void Malformed_grid_values_are_refused_without_changing_the_file(string needle, string poison)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("invalid-grid.mapproj");
        MapProjectStore.Save(
            Document(Grid(GridGeometry.IsometricSquares) with
            {
                CellSize = 128,
                Subdivisions = 4,
                LineWidth = 2.5,
                Color = "#CC334455",
                Opacity = 0.65,
                OffsetX = 13,
                RotationDegrees = -725.5,
            }), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, scene =>
        {
            Assert.True(
                JsonTestMutations.ContainsFragment(scene, needle),
                $"scene.json does not contain «{needle}»");
            return JsonTestMutations.ReplaceFirst(scene, needle, poison);
        });
        var poisoned = File.ReadAllBytes(path);

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Equal(poisoned, File.ReadAllBytes(path));
    }

    private static GridDefinition Grid(GridGeometry geometry) =>
        new(GridId.New(), geometry, 128)
        {
            Subdivisions = 1,
            LineWidth = 1,
            Color = "#FF405060",
            Opacity = 0.7,
        };

    private static MapDocument Document(params GridDefinition[] grids) => new(
        DocumentId.New(), "stage10e", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new GridLayer(LayerId.New(), "grid", true, false, 1, grids),
        ]);

    private static void RewriteScene(string path, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var sceneEntry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
        string scene;
        using (var reader = new StreamReader(sceneEntry.Open(), Encoding.UTF8)) scene = reader.ReadToEnd();
        var bytes = Encoding.UTF8.GetBytes(rewrite(scene));
        using (var stream = sceneEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }
        using (var parsed = JsonDocument.Parse(bytes)) _ = parsed.RootElement;

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifest;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8)) manifest = reader.ReadToEnd();
        manifest = SceneShaRegex().Replace(
            manifest, $"\"sceneSha256\": \"{MapProjectRoundTripTests.Sha256Hex(bytes)}\"");
        using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
        writer.BaseStream.SetLength(0);
        writer.Write(manifest);
    }
}
