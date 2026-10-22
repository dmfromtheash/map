using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection("HexTileSets")]
public sealed class HexTileSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-r15-" + Guid.NewGuid().ToString("N"));
    public HexTileSetTests() => Directory.CreateDirectory(_root);
    public void Dispose() { HexUserTileSets.Initialize(Path.Combine(_root, "empty")); Directory.Delete(_root, true); }
    private string Source()
    {
        var path = Path.Combine(_root, "source"); HexUserTileSets.CreateProcedural(path); return path;
    }
    [Fact]
    public void TileSet_import_retains_public_API_pixels_both_orientations_and_metadata()
    {
        var source = Source(); HexUserTileSets.Initialize(Path.Combine(_root, "user"));
        var set = HexUserTileSets.Import(source);
        Assert.Equal(3, set.Manifest.Tiles.Count);
        foreach (var tile in set.Manifest.Tiles)
        {
            var definition = HexStarterTiles.Find(set.TileId(tile))!;
            Assert.Equal(tile.TravelCost, definition.TravelCost); Assert.Equal(tile.Rotatable, definition.Rotatable);
            foreach (var orientation in Enum.GetValues<HexOrientation>())
            {
                using var original = HexTileArt.Image("starter/" + tile.Id, 0, orientation)!.Encode(SKEncodedImageFormat.Png, 100);
                var file = orientation == HexOrientation.Pointy ? tile.File : tile.FlatFile;
                Assert.Equal(original.ToArray(), File.ReadAllBytes(Path.Combine(set.Directory, file)));
                using var expected = SKBitmap.Decode(original.ToArray());
                using var imported = SKBitmap.FromImage(HexTileArt.Image(set.TileId(tile), 0, orientation)!);
                Assert.Equal(expected.Pixels, imported.Pixels);
            }
        }
        Assert.Throws<InvalidDataException>(() => HexUserTileSets.Import(source));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "user")));
        HexUserTileSets.Initialize(Path.Combine(_root, "other")); Assert.Null(HexTileArt.Image("user/procedural/grass", 0, HexOrientation.Flat));
        HexUserTileSets.Initialize(Path.Combine(_root, "user")); Assert.NotNull(HexTileArt.Image("user/procedural/grass", 0, HexOrientation.Flat));
    }
    [Theory]
    [InlineData("../escape.png", 1, 0)] [InlineData("grass.png", double.NaN, 0)]
    [InlineData("grass.png", 1, -1)] [InlineData("grass.png", 1, 3)]
    public void TileSet_rejects_paths_invalid_cost_or_non_tile_target(string file, double cost, int target)
    {
        var source = Source(); var set = HexUserTileSets.Read(source);
        var tile = set.Manifest.Tiles[0] with { File = file, TravelCost = cost, Target = (HexPaintTarget)target };
        var manifest = set.Manifest with { Tiles = [tile] };
        // NaN is represented as a JSON string, so deserialization must reject it too.
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        File.WriteAllText(Path.Combine(source, "set.json"), json);
        Assert.ThrowsAny<Exception>(() => HexUserTileSets.Read(source));
    }
    [Fact]
    public void TileSet_startup_reports_a_broken_installed_set_without_losing_other_sets()
    {
        var root = Path.Combine(_root, "user"); var source = Source();
        HexUserTileSets.Initialize(root); HexUserTileSets.Import(source);
        var bad = Path.Combine(root, "broken"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "set.json"), "{\"Version\":99}");
        HexUserTileSets.Initialize(Path.Combine(_root, "other")); HexUserTileSets.Initialize(root);
        Assert.Single(HexUserTileSets.All); Assert.Contains("не загружен", HexUserTileSets.LoadWarning);
        Assert.NotNull(HexTileArt.Image("user/procedural/grass", 0, HexOrientation.Pointy));
    }

    [Fact]
    public void TileSet_import_replaces_cached_missing_markers_without_editing_document()
    {
        var source = Source(); HexUserTileSets.Initialize(Path.Combine(_root, "user"));
        var d = HexMapFactory.Create("Комплект", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var l = d.Layers.OfType<HexLayer>().Single(); var at = l.Board.Layout.FromOffset(new(3, 3));
        l = l with { Board = l.Board.With([new(at, HexCell.Of("user/procedural/grass"))]) }; d = d.WithLayer(l.Id, _ => l);
        using var cache = new HexChunkCache();
        SKBitmap Render(HexChunkCache? chunks)
        {
            var result = new SKBitmap((int)d.Canvas.Width, (int)d.Canvas.Height);
            using var canvas = new SKCanvas(result);
            SceneRenderer.Render(canvas, d, new RenderOptions { ViewScale = 1, HexChunks = chunks });
            return result;
        }
        using var before = Render(cache);
        HexUserTileSets.Import(source);
        using var freshCache = new HexChunkCache();
        using var after = Render(cache); using var fresh = Render(freshCache);
        Assert.False(before.Pixels.SequenceEqual(after.Pixels));
        Assert.Equal(fresh.Pixels, after.Pixels);
    }

    [Fact]
    public void TileSet_template_is_importable_and_corrupt_or_wrong_sized_PNG_is_rejected()
    {
        var source = Path.Combine(_root, "template"); HexUserTileSets.ExportTemplate(source);
        Assert.Single(HexUserTileSets.Read(source).Manifest.Tiles);
        using var bitmap = new SKBitmap(12, 12); using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(source, "tile.png"), png.ToArray());
        Assert.Throws<InvalidDataException>(() => HexUserTileSets.Read(source));
        File.WriteAllText(Path.Combine(source, "tile.png"), "не картинка");
        Assert.Throws<InvalidDataException>(() => HexUserTileSets.Read(source));
    }
    [Fact]
    public void TileSet_control_sheet_covers_each_pair_direction_turn_and_flags_real_problems()
    {
        var set = HexUserTileSets.Read(Source());
        Assert.Equal(1944, HexTileSetInspection.Count(set, HexTileSetCheckKind.Neighbors));
        var cases = Enumerable.Range(0, 1944).Select(i => HexTileSetInspection.At(set, HexTileSetCheckKind.Neighbors, i, HexOrientation.Pointy)).ToArray();
        Assert.Equal(1944, cases.Select(c => (c.First.Id, c.Second.Id, c.Direction, c.FirstRotation, c.SecondRotation)).Distinct().Count());
        Assert.Contains(cases, c => c.Problems.Any(p => p.Contains("Край не совпадает")));
        Assert.Contains(cases, c => c.Problems.Any(p => p.Contains("Поворот запрещён")));
        var tall = set.Manifest.Tiles[2] with { Overhang = 1.2 };
        var bad = set with { Manifest = set.Manifest with { Tiles = [tall] } };
        Assert.Contains(HexTileSetInspection.At(bad, HexTileSetCheckKind.Overlaps, 0, HexOrientation.Pointy).Problems, p => p.Contains("Нависание выходит за ряд"));
        Assert.Throws<ArgumentOutOfRangeException>(() => HexTileSetInspection.At(set, 0, 1944, 0));
    }
}

[CollectionDefinition("HexTileSets", DisableParallelization = true)]
public sealed class HexTileSetsCollection;
