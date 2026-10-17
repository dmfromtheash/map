using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Alt+wheel through variants and on into the next set; the asset tool's key table.</summary>
public sealed class AssetSetStepAndKeysTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-set-step-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Alt_wheel_walks_variants_then_the_next_set_and_back_exactly()
    {
        var editor = Editor(out var catalog);
        var sets = editor.LibrarySets.Select(set => set.SetId).ToArray();
        Assert.Equal(3, sets.Length);
        var trees = catalog.FindSet(sets[0])!;
        Assert.Equal(3, trees.VariantIds.Count);
        editor.ChooseGalleryVariant(trees.Id, trees.VariantIds[0]);

        var visited = new List<(string Set, string Variant)>();
        for (var i = 0; i < 6; i++)
        {
            Assert.True(editor.StepAssetSet(1));
            visited.Add((editor.SelectedSet!.SetId, editor.CurrentVariant!.Id));
        }
        Assert.Equal(
        [
            (sets[0], trees.VariantIds[1]), (sets[0], trees.VariantIds[2]),
            (sets[1], catalog.FindSet(sets[1])!.VariantIds[0]), (sets[2], catalog.FindSet(sets[2])!.VariantIds[0]),
            (sets[0], trees.VariantIds[0]), (sets[0], trees.VariantIds[1]),
        ], visited);

        // Back from the first variant of the trees goes to the previous set; back again returns to
        // the last variant of the trees, so the wheel reverses step by step.
        editor.ChooseGalleryVariant(trees.Id, trees.VariantIds[0]);
        Assert.True(editor.StepAssetSet(-1));
        Assert.Equal(sets[2], editor.SelectedSet!.SetId);
        Assert.True(editor.StepAssetSet(-1));
        Assert.True(editor.StepAssetSet(-1));
        Assert.Equal((sets[0], trees.VariantIds[2]), (editor.SelectedSet!.SetId, editor.CurrentVariant!.Id));
        Assert.Contains("Alt+колесо", editor.StatusDetail);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Alt_wheel_follows_the_filtered_library_and_needs_a_chosen_set()
    {
        var editor = Editor(out var catalog);
        Assert.False(editor.StepAssetSet(1));
        var house = catalog.Sets.Single(set => set.Name == "Дом");
        editor.ChooseGalleryVariant(house.Id, house.VariantIds[0]);
        editor.LibrarySearchText = "дом";
        // Only one set is listed: nothing to move to, and a single-variant set has nothing to cycle.
        Assert.False(editor.StepAssetSet(1));
        Assert.Equal(house.Id, editor.SelectedSet!.SetId);
    }

    [Theory]
    [InlineData("W", EditorShortcutSettings.Commands.PrimaryUp)]
    [InlineData("S", EditorShortcutSettings.Commands.PrimaryDown)]
    [InlineData("E", EditorShortcutSettings.Commands.SecondaryUp)]
    [InlineData("Q", EditorShortcutSettings.Commands.SecondaryDown)]
    [InlineData("A", EditorShortcutSettings.Commands.RotateLeft)]
    [InlineData("D", EditorShortcutSettings.Commands.RotateRight)]
    [InlineData("C", EditorShortcutSettings.Commands.TertiaryUp)]
    [InlineData("Z", EditorShortcutSettings.Commands.TertiaryDown)]
    [InlineData("Alt+Q", EditorShortcutSettings.Commands.StampDensityUp)]
    [InlineData("Alt+A", EditorShortcutSettings.Commands.StampDensityDown)]
    [InlineData("Alt+Right", EditorShortcutSettings.Commands.StampNextVariant)]
    [InlineData("Alt+Left", EditorShortcutSettings.Commands.StampPreviousVariant)]
    [InlineData("Alt+Wheel", EditorShortcutSettings.Commands.StampCycleVariant)]
    public void Asset_tool_keys_are_bound_once_by_default(string gesture, string command)
    {
        var shortcuts = new EditorShortcutSettings();
        Assert.Equal(gesture, shortcuts.Get(command));
        Assert.Single(EditorShortcutSettings.Definitions, d => d.DefaultGesture == gesture);
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([Image("t1", SKColors.ForestGreen), Image("t2", SKColors.DarkGreen), Image("t3", SKColors.Olive)],
            "Деревья", new AssetImportOptions { Pack = "Лес" }, DateTimeOffset.UnixEpoch.AddSeconds(3));
        catalog.ImportSet([Image("Камень", SKColors.Gray)], "Камень", new AssetImportOptions { Pack = "Горы" }, DateTimeOffset.UnixEpoch.AddSeconds(2));
        catalog.ImportSet([Image("Дом", SKColors.Brown)], "Дом", new AssetImportOptions { Pack = "Город" }, DateTimeOffset.UnixEpoch.AddSeconds(1));
        var document = new MapDocument(DocumentId.New(), "keys", DateTimeOffset.UnixEpoch, new CanvasDefinition(800, 600, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
    }

    private string Image(string name, SKColor color)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(20, 20))!;
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
