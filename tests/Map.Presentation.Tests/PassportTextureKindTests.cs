using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

/// <summary>Passport texture tags are exclusive catalog metadata, including on an attached drive.</summary>
public sealed class PassportTextureKindTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(RepositoryRoot(), "artifacts", "sol4-passport-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Theory]
    [InlineData(PassportTextureKind.None, null)]
    [InlineData(PassportTextureKind.Ground, "Текстура/Земля")]
    [InlineData(PassportTextureKind.Water, "Текстура/Вода")]
    [InlineData(PassportTextureKind.Floor, "Текстура/Пол")]
    [InlineData(PassportTextureKind.Paint, "Текстура/Краска")]
    public void Changing_the_kind_replaces_texture_tags_only_and_refreshes_fill_choices(
        PassportTextureKind kind, string? expectedTag)
    {
        var catalog = Catalog("own");
        var set = AddSet(catalog, "mountain", 1);
        catalog.SetTags(set.Id, ["Горы", "Текстура", "Текстура/Земля", "Текстура/Вода", "Текстура/Пол",
            "Текстура/Краска", "текстура/Другое", "Место/Хребет", "Текстура камня"]);
        var placement = new AssetPlacementDefaults { CanFlip = false, RandomSizePercent = 35 };
        catalog.SetPlacementDefaults(set.Id, placement);
        var editor = Editor(catalog);
        editor.SelectAssetSet(set.Id);
        var document = editor.Session.Document;
        Assert.Equal(MapFillTextureKind.Water, Assert.Single(editor.LibraryTextures).Kind);

        Assert.True(editor.SetSelectedSetTextureKind(kind));

        var expected = new List<string> { "Горы", "Место/Хребет", "Текстура камня" };
        if (expectedTag is not null) expected.Add(expectedTag);
        Assert.Equal(expected, catalog.FindSet(set.Id)!.Tags);
        Assert.Equal(kind, editor.SelectedSetTextureKind);
        Assert.Equal(placement, catalog.FindSet(set.Id)!.Placement);
        Assert.Same(document, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(0, editor.Session.RedoDepth);
        if (kind == PassportTextureKind.None) Assert.Empty(editor.LibraryTextures);
        else
        {
            var texture = Assert.Single(editor.LibraryTextures);
            Assert.Equal(kind == PassportTextureKind.Water ? MapFillTextureKind.Water : MapFillTextureKind.Ground, texture.Kind);
            Assert.Equal(kind == PassportTextureKind.Floor, texture.Floor);
            Assert.Equal(kind == PassportTextureKind.Paint, texture.Paint);
        }
    }

    [Theory]
    [InlineData("Текстура", PassportTextureKind.Ground)]
    [InlineData(" текстура/земля ", PassportTextureKind.Ground)]
    [InlineData("ТЕКСТУРА/Вода", PassportTextureKind.Water)]
    [InlineData("Текстура/Пол", PassportTextureKind.Floor)]
    [InlineData("Текстура/Краска", PassportTextureKind.Paint)]
    [InlineData("Горы", PassportTextureKind.None)]
    public void The_passport_reads_existing_tags_and_tracks_the_chosen_card(string tag, PassportTextureKind expected)
    {
        var catalog = Catalog("own");
        var tagged = AddSet(catalog, "tagged", 1);
        var plain = AddSet(catalog, "plain", 2);
        catalog.SetTags(tagged.Id, [tag]);
        var editor = Editor(catalog);

        editor.SelectAssetSet(tagged.Id);
        Assert.Equal(expected, editor.SelectedSetTextureKind);
        editor.SelectAssetSet(plain.Id);
        Assert.Equal(PassportTextureKind.None, editor.SelectedSetTextureKind);
        editor.SelectAssetSet(null);
        Assert.Equal(PassportTextureKind.None, editor.SelectedSetTextureKind);
    }

    [Fact]
    public void Unchanged_kind_and_missing_selection_do_not_write_metadata()
    {
        var catalog = Catalog("own");
        var set = AddSet(catalog, "mountain", 1);
        var editor = Editor(catalog);
        editor.SelectAssetSet(set.Id);
        var revision = catalog.MetadataRevision;
        Assert.False(editor.SetSelectedSetTextureKind(PassportTextureKind.None));
        Assert.Equal(revision, catalog.MetadataRevision);

        Assert.True(editor.SetSelectedSetTextureKind(PassportTextureKind.Ground));
        revision = catalog.MetadataRevision;
        Assert.False(editor.SetSelectedSetTextureKind(PassportTextureKind.Ground));
        Assert.Equal(revision, catalog.MetadataRevision);

        editor.SelectAssetSet(null);
        revision = catalog.MetadataRevision;
        Assert.False(editor.SetSelectedSetTextureKind(PassportTextureKind.Water));
        Assert.Equal(revision, catalog.MetadataRevision);
        Assert.Equal(["Текстура/Земля"], catalog.FindSet(set.Id)!.Tags);
    }

    [Fact]
    public void Invalid_kind_is_rejected_before_the_catalog_changes()
    {
        var catalog = Catalog("own");
        var set = AddSet(catalog, "mountain", 1);
        var editor = Editor(catalog);
        editor.SelectAssetSet(set.Id);
        var revision = catalog.MetadataRevision;

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetSelectedSetTextureKind((PassportTextureKind)99));
        Assert.Equal(revision, catalog.MetadataRevision);
        Assert.Empty(catalog.FindSet(set.Id)!.Tags);
    }

    [Fact]
    public async Task Drive_card_edits_survive_reattach_and_leave_the_owners_catalog_and_detail_intact()
    {
        var drive = Catalog("drive");
        var driveSet = AddSet(drive, "drive-card", 1, "Горная вершина · контур тушью · вид сбоку");
        drive.SetTags(driveSet.Id, ["Горы", "Текстура"]);
        await drive.FlushMetadataAsync();
        var own = Catalog("own");
        var ownSet = AddSet(own, "own-card", 2);
        own.SetMounts([drive.Root]);
        var editor = Editor(own);
        editor.SelectAssetSet(driveSet.Id);
        Assert.Equal(PassportTextureKind.Ground, editor.SelectedSetTextureKind);
        Assert.Equal(driveSet.Detail, editor.SelectedSet!.Detail);

        Assert.True(editor.SetSelectedSetTextureKind(PassportTextureKind.Water));
        await own.FlushMetadataAsync();
        Assert.DoesNotContain(driveSet.Id, File.ReadAllText(Path.Combine(own.Root, "catalog.json")));
        Assert.Empty(own.FindSet(ownSet.Id)!.Tags);
        own.SetMounts([]);
        own.SetMounts([drive.Root]);
        editor.SelectAssetSet(driveSet.Id);
        Assert.Equal(PassportTextureKind.Water, editor.SelectedSetTextureKind);
        Assert.Equal(["Горы", "Текстура/Вода"], own.FindSet(driveSet.Id)!.Tags);
        Assert.Equal(driveSet.Detail, editor.SelectedSet!.Detail);

        Assert.True(editor.SetSelectedSetTextureKind(PassportTextureKind.None));
        await own.FlushMetadataAsync();
        own.SetMounts([]);
        own.SetMounts([drive.Root]);
        editor.SelectAssetSet(driveSet.Id);
        Assert.Equal(PassportTextureKind.None, editor.SelectedSetTextureKind);
        Assert.Equal(["Горы"], own.FindSet(driveSet.Id)!.Tags);
        Assert.Empty(editor.LibraryTextures);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    private AssetCatalog Catalog(string name)
    {
        var catalog = new AssetCatalog(Path.Combine(_root, name));
        _catalogs.Add(catalog);
        return catalog;
    }

    private static AssetSet AddSet(AssetCatalog catalog, string key, int fixture, string? detail = null) =>
        catalog.ImportSets([new AssetSetImport
        {
            StableKey = "passport-test|" + key, Name = "Гора " + key, Detail = detail,
            Paths = [Path.Combine(RepositoryRoot(), "tests", "fixtures", "assets", $"owner-mountain-0{fixture}.png")],
            Labels = ["Гора"],
        }], DateTimeOffset.UnixEpoch).Single();

    private static EditorViewModel Editor(AssetCatalog catalog)
    {
        var document = new MapDocument(DocumentId.New(), "passport-textures", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B")]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Корень Map не найден.");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var catalog in _catalogs) await catalog.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
