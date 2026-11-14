using System.Reflection;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// STYLE_SCALE: the style's per-layer data (the map without its names, its spatial index, the largest side, the pictures)
/// follow the edits instead of walking every object again. Whatever path an edit takes, they must equal what a cold
/// build of the same objects gives, and the styled frame must be byte for byte the frame of a freshly built map.
/// </summary>
public sealed class StyleScaleEquivalenceTests
{
    private static readonly Func<ObjectLayer, double> LargestSide = (Func<ObjectLayer, double>)typeof(StyleRenderer)
        .GetMethod("LargestSide", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(Func<ObjectLayer, double>));
    private static readonly Func<ObjectLayer, IReadOnlyCollection<string>> PicturesOf = (Func<ObjectLayer, IReadOnlyCollection<string>>)typeof(StyleRenderer)
        .GetMethod("VisibleStampAssetsOf", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(Func<ObjectLayer, IReadOnlyCollection<string>>));

    [Fact]
    public void Split_index_largest_side_and_pictures_after_every_kind_of_edit_equal_a_cold_build()
    {
        var random = new Random(20261114);
        var shas = Enumerable.Range(0, 5).Select(i => new string((char)('a' + i), 64)).ToArray();
        var document = Document(Enumerable.Range(0, 3000).Select(i => Item(random, shas, i % 41 == 7)).ToArray());
        DocumentRevisionIndex.For(document);
        Check(document);
        var layerId = document.Layers[1].Id;
        for (var step = 0; step < 80; step++)
        {
            var layer = (ObjectLayer)document.Layers[1];
            var objects = layer.Objects;
            var pick = objects[random.Next(objects.Count)];
            document = (step % 8) switch
            {
                // A move, a resize to the largest size and back, a stamp that becomes a text and a text that becomes a stamp.
                0 => DocumentEditor.ReplaceObjects(document, [SceneObjectClone.At(pick, pick.X + 9, pick.Y - 4)]),
                1 => DocumentEditor.ReplaceObjects(document, [SceneObjectClone.At(pick, pick.X, pick.Y, random.Next(2) == 0 ? 400 : 20, 30)]),
                2 => DocumentEditor.ReplaceObjects(document, [Item(random, shas, pick.Kind != SceneObjectKind.Text) with { Id = pick.Id }]),
                3 => DocumentEditor.AppendObjects(document, layerId, [.. Enumerable.Range(0, random.Next(1, 6)).Select(_ => Item(random, shas, random.Next(3) == 0))]),
                4 => DocumentEditor.RemoveAppendedObjects(document, layerId, [.. objects.Skip(objects.Count - random.Next(1, 4))]),
                5 => DocumentEditor.InsertObjects(document, layerId, new Dictionary<int, IReadOnlyList<SceneObject>>
                {
                    [random.Next(objects.Count)] = [Item(random, shas, random.Next(2) == 0)],
                    [random.Next(objects.Count)] = [Item(random, shas, false), Item(random, shas, true)],
                }),
                6 => DocumentEditor.RemovePathObjects(document, layerId,
                    Enumerable.Range(0, 4).Select(_ => objects[random.Next(objects.Count)].Id).ToHashSet()),
                // Several stamps at once, one of them hidden: the pictures count only visible stamps.
                _ => DocumentEditor.ReplaceObjects(document, [.. Enumerable.Range(0, 6).Select(_ => objects[random.Next(objects.Count)]).DistinctBy(o => o.Id)
                    .Select(o => o.Kind == SceneObjectKind.Stamp ? new SceneObject(o.Id, o.Kind, o.Name, o.X, o.Y, o.Width, o.Height, 0, o.FillColor,
                        o.StrokeColor, 0, random.Next(3) != 0, false) { Stamp = o.Stamp } : o)]),
            };
            // Frames skip revisions now and then (a gesture's coalesced edits): the next one derives through the chain.
            if (step % 5 != 3) Check(document);
            // The first move reached the bare layer's index through the edit's delta, not by indexing every object again.
            if (step == 0) Assert.True(ObjectSpatialIndex.For((ObjectLayer)StyleRenderer.Split(document).Bare.Layers[1]).IncrementalOverrideCount > 0);
        }
        Check(document);
    }

    private static void Check(MapDocument document)
    {
        var layer = (ObjectLayer)document.Layers[1];
        var (bare, names) = StyleRenderer.Split(document);
        var bareLayer = (ObjectLayer)bare.Layers[1];
        var expectedBare = layer.Objects.Where(o => o.Kind != SceneObjectKind.Text).ToArray();
        var expectedNames = layer.Objects.Where(o => o.Kind == SceneObjectKind.Text).ToArray();
        Assert.Equal(expectedBare, bareLayer.Objects);
        if (expectedNames.Length == 0) Assert.Same(layer, bareLayer);
        else Assert.Equal(expectedNames, ((ObjectLayer)names!.Layers.Single(l => l is ObjectLayer)).Objects);
        // A cold layer of the same objects: no edit links, nothing cached.
        var cold = new ObjectLayer(LayerId.New(), "cold", true, false, 1, expectedBare);
        foreach (var view in new[] { new WorldBounds(0, 0, 2000, 2000), new WorldBounds(900, 300, 1600, 1900), new WorldBounds(-50, -50, 5000, 5000) })
            Assert.Equal(ObjectSpatialIndex.For(cold).Query(view), ObjectSpatialIndex.For(bareLayer).Query(view));
        if (expectedBare.Length > 0) Assert.Equal(expectedBare.Max(o => Math.Max(o.Width, o.Height)), LargestSide(bareLayer));
        Assert.Equal(expectedBare.Where(o => o.Visible && o.Stamp is not null).Select(o => o.Stamp!.AssetSha256).Distinct().Order(StringComparer.Ordinal),
            PicturesOf(bareLayer).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(MapStylePreset.InkWetBrush)]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.OilAllaPrima)]
    [InlineData(MapStylePreset.PixelFantasy16)]
    [InlineData(MapStylePreset.WinterEngraving)]
    public void A_styled_frame_after_edits_is_byte_equal_to_the_frame_of_a_freshly_built_map(MapStylePreset preset)
    {
        var images = Owner.Value;
        {
            var stamps = new Images(images);
            var shas = images.Keys.Order(StringComparer.Ordinal).ToArray();
            var random = new Random(7);
            var items = new List<SceneObject>();
            for (var i = 0; i < 400; i++)
            {
                var size = 60 + random.Next(120);
                items.Add(i % 23 == 5
                    ? new SceneObject(ObjectId.New(), SceneObjectKind.Text, "РРјСЏ", random.Next(1800), random.Next(1800), 160, 36, 0,
                        "#FF202020", "#00000000", 0, true, false) { Text = new TextStyle { Content = "РРјСЏ " + i, FontSize = 22 } }
                    : new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Р“РѕСЂР°", random.Next(1800), random.Next(1800), size, size * .8,
                        i % 9 == 0 ? 11 : 0, "#FFFFFFFF", "#00000000", 0, true, false)
                        { Stamp = new StampStyle(shas[i % shas.Length]) { FlipHorizontal = i % 4 == 0 } });
            }
            var document = Document([.. items]) with { Style = MapStyle.For(preset) };
            DocumentRevisionIndex.For(document);
            var view = new WorldBounds(500, 500, 1012, 884);
            using (Draw(document, view, stamps)) { }
            var layerId = document.Layers[1].Id;
            var inView = ObjectSpatialIndex.For((ObjectLayer)document.Layers[1]).Query(view).Where(o => o.Kind == SceneObjectKind.Stamp).ToArray();
            // A move in the view, the largest mountain grows, a name becomes part of the map, new mountains, an undo.
            document = DocumentEditor.ReplaceObjects(document, [SceneObjectClone.At(inView[0], inView[0].X + 13, inView[0].Y + 5)]);
            using (Draw(document, view, stamps)) { }
            document = DocumentEditor.ReplaceObjects(document, [SceneObjectClone.At(inView[1], inView[1].X, inView[1].Y, 260, 210)]);
            var text = ((ObjectLayer)document.Layers[1]).Objects.First(o => o.Kind == SceneObjectKind.Text);
            document = DocumentEditor.ReplaceObjects(document, [new SceneObject(text.Id, SceneObjectKind.Stamp, "Р“РѕСЂР°", 700, 600, 140, 110, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(shas[2]) }]);
            using (Draw(document, view, stamps)) { }
            var added = new[] { new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Р“РѕСЂР°", 760, 640, 150, 120, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(shas[4]) } };
            document = DocumentEditor.AppendObjects(document, layerId, added);
            using (Draw(document, view, stamps)) { }
            document = DocumentEditor.InsertObjects(document, layerId, new Dictionary<int, IReadOnlyList<SceneObject>>
            {
                [3] = [new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Р“РѕСЂР°", 820, 560, 120, 100, 0,
                    "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(shas[1]) }],
            });
            using var edited = Draw(document, view, stamps);
            var fresh = Document([.. ((ObjectLayer)document.Layers[1]).Objects]) with { Style = MapStyle.For(preset) };
            using var expected = Draw(fresh, view, stamps);
            Assert.Equal(Hash(expected), Hash(edited));
            // The same holds for the export path, which never keeps buffers.
            using var exportEdited = Draw(document, view, stamps, export: true);
            using var exportFresh = Draw(fresh, view, stamps, export: true);
            Assert.Equal(Hash(exportFresh), Hash(exportEdited));
        }
    }

    private static string Hash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));

    private static SKBitmap Draw(MapDocument document, WorldBounds view, IStampImageSource stamps, bool export = false)
    {
        var info = new SKImageInfo((int)(view.Right - view.Left), (int)(view.Bottom - view.Top), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = stamps, VisibleWorld = view, IsExport = export });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static SceneObject Item(Random random, string[] shas, bool text)
    {
        double x = random.Next(4000), y = random.Next(4000), w = 20 + random.Next(200), h = 20 + random.Next(160);
        return text
            ? new SceneObject(ObjectId.New(), SceneObjectKind.Text, "РРјСЏ", x, y, w, h, 0, "#FF000000", "#00000000", 0, true, false)
                { Text = new TextStyle { Content = "РРјСЏ", FontSize = 18 } }
            : new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Р“РѕСЂР°", x, y, w, h, 0, "#FFFFFFFF", "#00000000", 0,
                random.Next(10) != 0, false) { Stamp = new StampStyle(shas[random.Next(shas.Length)]) };
    }

    private static MapDocument Document(SceneObject[] objects) => new(DocumentId.New(), "style scale", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(4096, 4096, "px", "#FFB6AE86"),
        [new BaseColorLayer(LayerId.New(), "Р¤РѕРЅ", true, false, 1, "#FFB6AE86"),
         new ObjectLayer(LayerId.New(), "Р“РѕСЂС‹", true, false, 1, objects)]);

    // Loaded once: the form maps of a picture are kept per image, so every preset reuses them.
    private static readonly Lazy<Dictionary<string, SKImage>> Owner = new(OwnerMountains);

    private static Dictionary<string, SKImage> OwnerMountains()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "assets"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var result = new Dictionary<string, SKImage>(StringComparer.Ordinal);
        for (var i = 1; i <= 6; i++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{i:D2}.png"));
            result[Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()] = SKImage.FromEncodedData(bytes).ToRasterImage();
        }
        return result;
    }

    private sealed class Images(IReadOnlyDictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
