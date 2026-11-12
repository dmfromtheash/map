using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Tiling textures tagged in the asset library are offered as land, water and floor fills and travel with the map.</summary>
public sealed class LibraryFillTextureWorkflowTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-library-texture-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Tags_decide_which_pictures_are_textures_and_for_what()
    {
        Assert.Equal(MapFillTextureKind.Water, LibraryFillTextures.KindOf(["Текстура/Вода"], out _, out _));
        Assert.Equal(MapFillTextureKind.Ground, LibraryFillTextures.KindOf(["текстура/земля"], out _, out _));
        Assert.Equal(MapFillTextureKind.Ground, LibraryFillTextures.KindOf(["Текстура/Пол"], out var floor, out _));
        Assert.True(floor);
        Assert.Equal(MapFillTextureKind.Ground, LibraryFillTextures.KindOf(["Текстура/Краска"], out _, out var paint));
        Assert.True(paint);
        Assert.Equal(MapFillTextureKind.Ground, LibraryFillTextures.KindOf(["Текстура"], out _, out _));
        Assert.Null(LibraryFillTextures.KindOf(["Гора", "Лес"], out _, out _));
    }

    [Fact]
    public void Pickers_list_library_textures_after_the_built_in_ones_grouped_by_owner_and_pack()
    {
        var editor = Editor(out var catalog);
        var ground = editor.FillTextureChoices(MapFillTextureKind.Ground);
        var water = editor.FillTextureChoices(MapFillTextureKind.Water);

        var builtIn = MapFillTextures.Of(MapFillTextureKind.Ground).Count(t => MapTextureLibrary.Image(t.Id) is not null);
        Assert.All(ground.Take(builtIn), t => Assert.False(MapFillTextures.IsAssetId(t.Id)));
        var library = ground.Skip(builtIn).ToArray();
        Assert.Equal(["Камень", "Плитка", "Песок пустыни"], library.Select(t => t.Title));
        Assert.Equal([MapFillTextures.LibraryGroup, MapFillTextures.LibraryGroup + " · пол", "Пустыни"], library.Select(t => t.Group));
        Assert.All(library, t => Assert.True(MapFillTextures.IsAssetId(t.Id)));
        Assert.Equal("Озеро", Assert.Single(water, t => MapFillTextures.IsAssetId(t.Id)).Title);
        Assert.DoesNotContain(ground.Concat(water), t => t.Title == "Гора");
        Assert.Equal("Камень", editor.FillTextureTitle(library[0].Id));
    }

    [Fact]
    public void Choosing_a_library_ground_embeds_it_in_one_undo_step_and_the_map_draws_it_without_the_library()
    {
        var editor = Editor(out var catalog);
        var stone = editor.LibraryTextures.Single(t => t.Title == "Камень");
        Assert.True(editor.ChooseLandTexture(stone.Id));
        var land = editor.Session.Document.Layers.OfType<LandLayer>().Single();
        Assert.Equal(stone.Id, land.EdgeStyle.FillTextureId);
        var asset = Assert.Single(editor.Session.Document.Assets);
        Assert.Equal((stone.Sha256, ProjectAssetStorage.Embedded), (asset.Sha256, asset.Storage));
        Assert.Equal(1, editor.Session.UndoDepth);

        // Water goes to the sea the same way.
        var lake = editor.LibraryTextures.Single(t => t.Kind == MapFillTextureKind.Water);
        Assert.True(editor.SetBaseMaterial(lake.Id));
        Assert.Equal(2, editor.Session.Document.Assets.Count);

        editor.Session.Undo();
        editor.Session.Undo();
        Assert.Equal(MapFillTextures.OwnerSnow, editor.Session.Document.Layers.OfType<LandLayer>().Single().EdgeStyle.FillTextureId);
        Assert.Empty(editor.Session.Document.Assets);
        editor.Session.Redo();

        // The library (a drive) is gone: the copy the map carries still draws.
        var map = editor.Session.Document;
        foreach (var file in Directory.EnumerateFiles(catalog.Root, "*", SearchOption.AllDirectories)) File.Delete(file);
        var offline = new EditorViewModel(map, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch);
        Assert.Equal(stone.Sha256, Assert.Single(offline.Session.Document.Assets).Sha256);
        Assert.NotNull(offline.FillTextureBytes(stone.Sha256));
        Assert.NotNull(MapTextureLibrary.Shader(stone.Id, 256));
        Assert.Equal("Камень", offline.FillTextureTitle(stone.Id));
    }

    [Fact]
    public void The_land_brush_paints_with_a_library_texture()
    {
        var editor = Editor(out _);
        var floor = editor.LibraryTextures.Single(t => t.Floor);
        Assert.True(editor.ChooseLandMaterial(floor.Id));
        Assert.Equal(floor.Id, editor.LandMaterialId);
        Assert.NotNull(MapTextureLibrary.BrushTexture(floor.Id));
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        var at = DateTimeOffset.UnixEpoch;
        foreach (var (file, color, name, pack, tag) in new[]
        {
            ("stone", SKColors.SlateGray, "Камень", "Мои ассеты", "Текстура/Земля"),
            ("tiles", SKColors.Peru, "Плитка", "Мои ассеты", "Текстура/Пол"),
            ("sand", SKColors.SandyBrown, "Песок пустыни", "Пустыни", "Текстура"),
            ("lake", SKColors.SteelBlue, "Озеро", "Мои ассеты", "Текстура/Вода"),
            ("peak", SKColors.White, "Гора", "Мои ассеты", "Гора"),
        })
        {
            var set = catalog.ImportSet([Image(file, color)], name, new AssetImportOptions { Pack = pack, Tags = [tag] }, at);
            if (set.Name != name) catalog.RenameSet(set.Id, name);
            at = at.AddSeconds(1);
        }
        var document = new MapDocument(DocumentId.New(), "textures", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
             LandLayer.CreateEmpty(LayerId.New(), "Земля")]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
    }

    private string Image(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N") + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
