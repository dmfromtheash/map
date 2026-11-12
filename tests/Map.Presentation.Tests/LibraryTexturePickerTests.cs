using System.Reflection;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Picker metadata and preview loading, without opening an Avalonia window.</summary>
public sealed class LibraryTexturePickerTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(RepositoryRoot, ".sol3-temp", "picker-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Exact_variant_thumbnail_is_available_without_the_original_and_does_not_read_full_textures()
    {
        var catalog = Catalog("own");
        var set = Import(catalog, "Камень", "Мои ассеты", LibraryFillTextures.GroundTag, 1, 2);
        var editor = Editor(catalog);
        var fullReads = 0;
        using var source = MapTextureLibrary.AddAssetSource(_ => { fullReads++; return null; });
        foreach (var variantId in set.VariantIds)
        {
            var record = catalog.RecordForVariant(variantId)!;
            File.Delete(Path.Combine(catalog.Root, record.StoredRelativePath!));
            var id = MapFillTextures.AssetId(record.Sha256);
            var thumbnail = editor.FillTextureThumbnailPath(id);
            Assert.Equal(catalog.ThumbnailPathForVariant(variantId), thumbnail);
            Assert.True(File.Exists(thumbnail));
            Assert.Contains(editor.FillTextureChoices(MapFillTextureKind.Ground), texture => texture.Id == id);
            using var image = Thumbnail(thumbnail);
            Assert.NotNull(image);
            Assert.InRange(image.Width, 1, AssetCatalog.ThumbnailSize);
            Assert.InRange(image.Height, 1, AssetCatalog.ThumbnailSize);
        }
        Assert.Equal(0, fullReads);
        Assert.NotEqual(editor.FillTextureThumbnailPath(MapFillTextures.AssetId(catalog.RecordForVariant(set.VariantIds[0])!.Sha256)),
            editor.FillTextureThumbnailPath(MapFillTextures.AssetId(catalog.RecordForVariant(set.VariantIds[1])!.Sha256)));
    }

    [Fact]
    public void Mount_attach_replace_with_same_count_and_detach_invalidate_choices_and_thumbnail_roots()
    {
        var own = Catalog("own");
        var first = Catalog("first");
        var second = Catalog("second");
        Import(first, "Вода", "Водоёмы", LibraryFillTextures.WaterTag, 3);
        Import(second, "Пол", "Каменные полы", LibraryFillTextures.FloorTag, 4);
        var editor = Editor(own);
        var notifications = 0;
        editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditorViewModel.LibrarySets)) notifications++; };
        var initial = editor.FillTextureRevision;

        own.AttachMounts([first]);
        var attached = editor.FillTextureRevision;
        Assert.NotEqual(initial, attached);
        var water = Assert.Single(editor.FillTextureChoices(MapFillTextureKind.Water), texture => MapFillTextures.IsAssetId(texture.Id));
        Assert.Equal("Водоёмы", water.Group);
        Assert.StartsWith(first.Root, editor.FillTextureThumbnailPath(water.Id));

        own.AttachMounts([second]);
        Assert.Equal(attached.Mounts, editor.FillTextureRevision.Mounts);
        Assert.NotEqual(attached, editor.FillTextureRevision);
        Assert.DoesNotContain(editor.FillTextureChoices(MapFillTextureKind.Water), texture => MapFillTextures.IsAssetId(texture.Id));
        Assert.Null(editor.FillTextureThumbnailPath(water.Id));
        var floor = Assert.Single(editor.FillTextureChoices(MapFillTextureKind.Ground), texture => MapFillTextures.IsAssetId(texture.Id));
        Assert.Equal("Каменные полы · пол", floor.Group);
        Assert.StartsWith(second.Root, editor.FillTextureThumbnailPath(floor.Id));

        own.AttachMounts([]);
        Assert.DoesNotContain(editor.FillTextureChoices(MapFillTextureKind.Ground), texture => MapFillTextures.IsAssetId(texture.Id));
        Assert.Null(editor.FillTextureThumbnailPath(floor.Id));
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void Metadata_changes_refresh_titles_and_kind_without_changing_mount_count()
    {
        var catalog = Catalog("own");
        var set = Import(catalog, "Камень", "Мои ассеты", LibraryFillTextures.GroundTag, 5);
        var editor = Editor(catalog);
        var original = Assert.Single(editor.LibraryTextures);
        var before = editor.FillTextureRevision;
        catalog.RenameSet(set.Id, "Камень у воды");
        catalog.SetTags(set.Id, [LibraryFillTextures.WaterTag]);
        editor.RefreshLibrary();

        Assert.Equal(before.Mounts, editor.FillTextureRevision.Mounts);
        Assert.True(editor.FillTextureRevision.Metadata > before.Metadata);
        Assert.Equal("Камень у воды", editor.FillTextureTitle(original.Id));
        Assert.DoesNotContain(editor.FillTextureChoices(MapFillTextureKind.Ground), texture => texture.Id == original.Id);
        var water = Assert.Single(editor.FillTextureChoices(MapFillTextureKind.Water), texture => texture.Id == original.Id);
        Assert.Equal(MapFillTextures.LibraryGroup, water.Group);
    }

    [Fact]
    public void Missing_corrupt_or_oversized_thumbnail_does_not_decode_an_original()
    {
        Assert.Null(Thumbnail(null));
        Assert.Null(Thumbnail(Path.Combine(_root, "missing.png")));
        Directory.CreateDirectory(_root);
        var corrupt = Path.Combine(_root, "corrupt.png");
        File.WriteAllText(corrupt, "invalid thumbnail");
        Assert.Null(Thumbnail(corrupt));
        // The owner's fixture is 512 px: it must be rejected from its header, before pixel allocation.
        Assert.Null(Thumbnail(Fixture(6)));
    }

    [Fact]
    public void Creating_offscreen_swatches_does_not_request_pixels()
    {
        var requests = 0;
        var type = typeof(EditorView).GetNestedType("MaterialSwatch", BindingFlags.NonPublic)!;
        var contextType = typeof(EditorView).GetNestedType("MaterialPreviewContext", BindingFlags.NonPublic)!;
        var preview = typeof(LibraryTexturePickerTests).GetMethod(nameof(Preview), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(contextType).Invoke(null, [new Action(() => requests++)]);
        for (var i = 0; i < 200; i++)
            Assert.NotNull(Activator.CreateInstance(type, [preview, Activator.CreateInstance(contextType)]));
        Assert.Equal(0, requests);
    }

    private static Func<T, Avalonia.Media.IBrush?> Preview<T>(Action requested) => _ => { requested(); return null; };

    [Theory]
    [InlineData("MaterialCatalog", null, "MaterialCatalog_none")]
    [InlineData("LandTexturePicker", "owner-snow-drifts", "LandTexturePicker_owner-snow-drifts")]
    [InlineData("MaterialCatalog", "asset:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "MaterialCatalog_asset_0123456789abcdef")]
    [InlineData("BaseTexturePicker", "asset:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "BaseTexturePicker_asset_0123456789abcdef")]
    public void Button_names_are_stable_and_safe(string prefix, string? id, string expected)
    {
        var method = typeof(EditorView).GetMethod("MaterialButtonName", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [prefix, id]));
    }

    private static SKImage? Thumbnail(string? path) => (SKImage?)typeof(EditorView)
        .GetMethod("MaterialThumbnail", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path]);

    private AssetCatalog Catalog(string name)
    {
        var catalog = new AssetCatalog(Path.Combine(_root, name));
        _catalogs.Add(catalog);
        return catalog;
    }

    private static AssetSet Import(AssetCatalog catalog, string name, string pack, string tag, params int[] fixtures)
    {
        var set = catalog.ImportSet(fixtures.Select(Fixture).ToArray(), name,
            new AssetImportOptions { Pack = pack, Tags = [tag] }, DateTimeOffset.UnixEpoch);
        catalog.RenameSet(set.Id, name);
        return catalog.FindSet(set.Id)!;
    }

    private static EditorViewModel Editor(AssetCatalog catalog) => new(
        new MapDocument(DocumentId.New(), "Покрытия", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), LandLayer.CreateEmpty(LayerId.New(), "Земля")]),
        null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
        clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);

    private static string Fixture(int index) => Path.Combine(RepositoryRoot, "tests", "fixtures", "assets", $"owner-mountain-{index:D2}.png");

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
