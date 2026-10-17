using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StampFamilyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-stamp-family-" + Guid.NewGuid().ToString("N"));
    private AssetCatalog _catalog = null!;
    public Task InitializeAsync() { _catalog = new AssetCatalog(Path.Combine(_root, "catalog")); return Task.CompletedTask; }
    public async Task DisposeAsync() { await _catalog.DisposeAsync(); Directory.Delete(_root, true); }

    private string Source(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray()); return path;
    }

    private AssetSet Import(string name, string? pack, SKColor color)
    {
        var record = _catalog.Import(Source(name, color), new AssetImportOptions { Pack = pack ?? "" }, DateTimeOffset.UtcNow);
        var set = _catalog.FindSetForContent(record.Sha256)!;
        _catalog.RenameSet(set.Id, name); return _catalog.FindSet(set.Id)!;
    }

    private EditorViewModel Editor() => new(TestData.Document(), null, false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: _catalog);

    [Fact]
    public void Singleton_mountains_switch_within_their_style_despite_catalog_search_and_keep_placement_settings()
    {
        var a = Import("Ледяной рог", "Живописные горы", SKColors.Blue);
        var b = Import("Снежный цирк", "Живописные горы", SKColors.Red);
        Import("Каменный массив", "Живописные горы", SKColors.Green);
        Import("Иглы", "Живописные горы", SKColors.Yellow);
        var other = Import("Контурная гора", "Контурные горы", SKColors.Brown);
        var editor = Editor(); editor.SelectAssetSet(a.Id);
        editor.SetStampScalePercent(175, commit: true); editor.SetStampRotationDegrees(25, commit: true);
        editor.SetStampOpacityPercent(70, commit: true);
        var document = editor.Session.Document; var depth = editor.Session.UndoDepth;
        var family = editor.StampFamilyVariants;
        editor.LibrarySearchText = "Ледяной рог";
        Assert.Single(editor.LibrarySets);
        Assert.Equal(4, family.Count);
        Assert.DoesNotContain(family, choice => choice.SetId == other.Id);
        Assert.Equal("Живописные горы", editor.StampFamilyName);
        Assert.True(editor.SelectStampFamilyVariantAt(family.ToList().FindIndex(c => c.SetId == b.Id)));
        Assert.Equal(b.Id, editor.SelectedSetId);
        Assert.Same(family, editor.StampFamilyVariants);
        Assert.Equal(175, editor.StampToolSettings.ScalePercent);
        Assert.Equal(25, editor.StampToolSettings.RotationDegrees);
        Assert.Equal(.7, editor.StampToolSettings.ObjectOpacity, 8);
        Assert.Same(document, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        editor.UpdatePlacementPreview(220, 180);
        Assert.True(editor.CommitPendingPlacement());
        var placed = Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects));
        Assert.Equal(_catalog.FindVariant(b.CoverVariantId)!.Sha256, placed.Stamp!.AssetSha256);
        editor.Undo();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects));
        Assert.Equal(depth, editor.Session.UndoDepth);
        editor.ArmStampPlacement(); editor.UpdatePlacementPreview(275, 200);
        editor.NextStampFamilyVariant(); editor.PreviousStampFamilyVariant();
        Assert.Equal(b.Id, editor.SelectedSetId);
        Assert.True(editor.PendingPlacement!.PointerVisible);
        Assert.Equal(275, editor.PendingPlacement.WorldX); Assert.Equal(200, editor.PendingPlacement.WorldY);
    }

    [Fact]
    public void Real_variant_sets_keep_their_order_and_uncollected_singletons_are_not_a_family()
    {
        var set = _catalog.ImportSet([Source("a", SKColors.Teal), Source("b", SKColors.Orange)],
            "Горный набор", new AssetImportOptions { Pack = "Разные наборы" }, DateTimeOffset.UtcNow);
        Import("Соседняя карточка", "Разные наборы", SKColors.Black);
        var single = Import("Без коллекции", null, SKColors.Violet);
        Import("Другая без коллекции", null, SKColors.White);
        var editor = Editor(); editor.SelectAssetSet(set.Id);
        Assert.Equal(set.VariantIds, editor.StampFamilyVariants.Select(c => c.Variant.Id));
        editor.NextStampFamilyVariant(); Assert.Equal(set.VariantIds[1], editor.CurrentVariant!.Id);
        editor.SelectAssetSet(single.Id); Assert.Single(editor.StampFamilyVariants);
        editor.SelectAssetSet(null); Assert.Empty(editor.StampFamilyVariants);
        Assert.Equal("", editor.CurrentStampFamilyLabel);
    }
}
