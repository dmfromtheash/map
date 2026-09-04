using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// A placed object must be shown as the asset it actually draws. The defect this covers is the
/// objects panel picking a single generic tool glyph for every stamp, so a tree and a mountain
/// looked identical and were both captioned with the tool's name.
/// </summary>
public sealed class ObjectRowThumbnailTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-object-thumb", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Each_placed_object_resolves_the_thumbnail_of_its_own_asset()
    {
        var editor = Editor(out var catalog);
        var tree = catalog.Import(Source("tree", SKColors.ForestGreen), new AssetImportOptions(), Stamp(0));
        var mountain = catalog.Import(Source("mountain", SKColors.SaddleBrown), new AssetImportOptions(), Stamp(1));
        editor.RefreshLibrary();

        editor.PlaceStamp(tree);
        var placedTree = editor.Session.SelectedObject!;
        editor.PlaceStamp(mountain);
        var placedMountain = editor.Session.SelectedObject!;

        var treeThumbnail = editor.ObjectThumbnailPath(placedTree);
        var mountainThumbnail = editor.ObjectThumbnailPath(placedMountain);

        // A real file for each, and a different one per asset - not one shared tool pictogram.
        Assert.NotNull(treeThumbnail);
        Assert.NotNull(mountainThumbnail);
        Assert.True(File.Exists(treeThumbnail));
        Assert.True(File.Exists(mountainThumbnail));
        Assert.NotEqual(treeThumbnail, mountainThumbnail);

        // The thumbnail belongs to the exact content the object stores, so reopening the map
        // cannot silently pair the object with some other picture.
        Assert.Equal(catalog.ThumbnailPath(tree), treeThumbnail);
        Assert.Equal(tree.Sha256, placedTree.Stamp!.AssetSha256);
        Assert.Equal(mountain.Sha256, placedMountain.Stamp!.AssetSha256);
    }

    [Fact]
    public void An_object_that_is_not_asset_backed_has_no_thumbnail_and_falls_back_to_its_kind()
    {
        var editor = Editor(out _);
        editor.AddObject(SceneObjectKind.Ellipse);

        var selected = editor.Session.SelectedObject!;
        Assert.Null(editor.ObjectThumbnailPath(selected));

        // With no picture of its own the row still has to say what the object is.
        Assert.Equal("Эллипс", EditorLabels.ObjectKind(selected.Kind));
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        return new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog);
    }

    private static DateTimeOffset Stamp(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private string Source(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        if (!File.Exists(path))
        {
            using var surface = SKSurface.Create(new SKImageInfo(64, 48))!;
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, encoded.ToArray());
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
