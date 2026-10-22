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
                using var imported = HexTileArt.Image(set.TileId(tile), 0, orientation)!.Encode(SKEncodedImageFormat.Png, 100);
                Assert.Equal(original.ToArray(), imported.ToArray());
            }
        }
        Assert.Throws<InvalidDataException>(() => HexUserTileSets.Import(source));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "user")));
        HexUserTileSets.Initialize(Path.Combine(_root, "other")); Assert.Null(HexTileArt.Image("user/procedural/grass", 0, HexOrientation.Flat));
        HexUserTileSets.Initialize(Path.Combine(_root, "user")); Assert.NotNull(HexTileArt.Image("user/procedural/grass", 0, HexOrientation.Flat));
    }
    [Theory]
    [InlineData("../escape.png", 0, 1)] [InlineData("grass.png", double.NaN, 1)]
    [InlineData("grass.png", 0, -1)] [InlineData("grass.png", 0, 3)]
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
