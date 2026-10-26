using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class RegionalRoleWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-r11-workflow-" + Guid.NewGuid().ToString("N"));
    private AssetCatalog? _catalog;
    private EditorViewModel? _editor;

    private EditorViewModel Create()
    {
        Directory.CreateDirectory(_root);
        var paths = new List<string>();
        foreach (var (name, color) in new[] { ("a", SKColors.Teal), ("b", SKColors.Gray) })
        {
            var path = Path.Combine(_root, name + ".png");
            using var surface = SKSurface.Create(new SKImageInfo(24, 32))!;
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot(); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, bytes.ToArray()); paths.Add(path);
        }
        _catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var set = _catalog.ImportSet(paths, "Мои формы", new(), DateTimeOffset.UnixEpoch);
        foreach (var id in set.VariantIds) _catalog.SetVariantPresentation(id, id, .5, .85, .125);
        _catalog.SetPlacementDefaults(set.Id, new() { AllowedRotations = [15, 45], CanFlip = false, DepthSort = true });
        var document = new MapDocument(DocumentId.New(), "R11", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        _editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: _catalog);
        _editor.SelectAssetSet(set.Id);
        return _editor;
    }

    [Theory]
    [InlineData(AssetRole.Tree, RegionalPlacementMode.Forest)]
    [InlineData(AssetRole.Spruce, RegionalPlacementMode.Forest)]
    [InlineData(AssetRole.Bush, RegionalPlacementMode.Forest)]
    [InlineData(AssetRole.Peak, RegionalPlacementMode.Ridge)]
    [InlineData(AssetRole.House, RegionalPlacementMode.Settlement)]
    [InlineData(AssetRole.Tower, RegionalPlacementMode.Settlement)]
    public async Task Own_procedural_set_places_without_a_pack_and_reopens_without_a_catalog(AssetRole role, RegionalPlacementMode mode)
    {
        var editor = Create(); var before = editor.Session.Document;
        Assert.True(editor.SetSelectedSetRole(role));
        Assert.Same(before, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        Assert.True(editor.StartRegionalPlacement(mode));
        Assert.Equal(12.5, editor.StampToolSettings.ScalePercent, 5);
        if (mode == RegionalPlacementMode.Ridge)
        {
            Assert.True(editor.AddEditablePathNode(200, 200)); Assert.True(editor.AddEditablePathNode(900, 500));
            Assert.True(editor.FinishEditablePath()); Assert.Single(editor.EditablePaths).ValidateMembers();
        }
        else
        {
            foreach (var (x, y) in new[] { (100d, 100d), (1000d, 100d), (1000d, 650d), (100d, 650d) })
                Assert.True(editor.AddEditableAreaNode(x, y));
            Assert.True(editor.FinishEditableArea());
            var area = Assert.Single(editor.EditableAreas); area.ValidateMembers(); Assert.True(area.DepthSort);
            Assert.Equal(PlacementAreaBeauty.ForNewArea(area.Request.BaseWidth, area.Pattern), area.Beauty);
            if (mode == RegionalPlacementMode.Forest) Assert.NotEmpty(area.RuleBlocked);
        }
        Assert.Equal(1, editor.Session.UndoDepth);
        var placed = Objects(editor.Session.Document);
        Assert.NotEmpty(placed);
        Assert.All(placed, item => { Assert.Equal(15, item.RotationDegrees); Assert.False(item.Stamp!.FlipHorizontal || item.Stamp.FlipVertical); });
        Assert.All(placed, item => Assert.Contains(item.Stamp!.AssetSha256, _catalog!.Records.Select(r => r.Sha256)));
        editor.Undo(); Assert.Empty(Objects(editor.Session.Document)); editor.Redo();
        Assert.Equal(placed, Objects(editor.Session.Document));
        var path = Path.Combine(_root, "result.mapproj");
        MapProjectStore.Save(editor.Session.Document, path, new("r11-test", DateTimeOffset.UnixEpoch), File.ReadAllBytes(Path.Combine(_root, "a.png")));
        var reopened = MapProjectStore.Open(path).Document;
        Assert.Equal(placed, Objects(reopened));
        foreach (var area in reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas))
        {
            area.ValidateMembers();
            var saved = editor.EditableAreas.Single(a => a.Id == area.Id);
            Assert.Equal(saved.Beauty, area.Beauty); Assert.Equal(saved.DepthSort, area.DepthSort);
            Assert.Equal(saved.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal),
                area.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal));
        }
        foreach (var ridge in reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementPaths)) ridge.ValidateMembers();
        var withoutCatalog = new EditorViewModel(reopened, path, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        Assert.Equal(placed, Objects(withoutCatalog.Session.Document));
        await _catalog!.FlushMetadataAsync();
        await using var reopenedCatalog = new AssetCatalog(_catalog.Root);
        Assert.Equal(role, reopenedCatalog.FindSet(_catalog.Sets[0].Id)!.Placement!.Role);
    }

    [Fact]
    public void Ecology_refresh_follows_a_new_river_without_requiring_a_manual_R10_rule()
    {
        var editor = Create(); editor.SetSelectedSetRole(AssetRole.Tree);
        Assert.True(editor.StartRegionalPlacement(RegionalPlacementMode.Forest));
        foreach (var (x, y) in new[] { (100d, 100d), (1000d, 100d), (1000d, 650d), (100d, 650d) })
            editor.AddEditableAreaNode(x, y);
        Assert.True(editor.FinishEditableArea()); Assert.True(editor.EditableAreaUsesEcology);
        Assert.Null(Assert.Single(editor.EditableAreas).Rules);
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var river = new River { Name = "New river", Channels = [RiverEditor.NewChannel([new(0, 360), new(1200, 360)], 80)] };
        editor.Session.Execute(UpdateLayerCommand.Replace(layer, layer with { Rivers = [river] }, "river"));
        var before = editor.Session.Document;
        editor.RefreshEditableAreaRules();
        var area = Assert.Single(editor.EditableAreas); area.ValidateMembers();
        Assert.Equal(1, area.Rules!.Revision); Assert.False(area.Rules.Any);
        var water = new PlacementAreaRuleCheck(editor.Session.Document, new() { AvoidWater = true });
        Assert.NotEmpty(Objects(editor.Session.Document));
        Assert.All(Objects(editor.Session.Document), item => Assert.False(water.Blocks(item)));
        editor.Undo(); Assert.Same(before, editor.Session.Document);
    }

    [Fact]
    public void A_slope_set_uses_a_ridge_area_but_places_no_rocks_without_existing_cliffs()
    {
        var editor = Create(); editor.SetSelectedSetRole(AssetRole.Slope);
        Assert.True(editor.StartRegionalPlacement(RegionalPlacementMode.Ridge));
        foreach (var (x, y) in new[] { (100d, 100d), (1000d, 100d), (1000d, 650d), (100d, 650d) })
            editor.AddEditableAreaNode(x, y);
        Assert.True(editor.FinishEditableArea());
        var area = Assert.Single(editor.EditableAreas); area.ValidateMembers();
        Assert.Equal(PlacementAreaPattern.Ridge, area.Pattern);
        Assert.Equal(0, area.MemberCount); Assert.NotEmpty(area.RuleBlocked);
        Assert.Empty(Objects(editor.Session.Document));
    }

    [Fact]
    public void Missing_incompatible_and_archived_roles_refuse_without_document_edits()
    {
        var editor = Create(); var before = editor.Session.Document;
        Assert.False(editor.StartRegionalPlacement(RegionalPlacementMode.Forest));
        editor.SetSelectedSetRole(AssetRole.Sign);
        Assert.False(editor.StartRegionalPlacement(RegionalPlacementMode.Settlement));
        editor.SetSelectedSetRole(AssetRole.Tree);
        _catalog!.SetSetArchived(_catalog.Sets[0].Id, true);
        Assert.False(editor.StartRegionalPlacement(RegionalPlacementMode.Forest));
        Assert.Same(before, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
    }

    private static SceneObject[] Objects(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (_editor is not null)
        {
            _editor.CancelEditableAreaGesture();
            await _editor.PendingEditableAreaCount; await _editor.PendingEditableAreaPreview; await _editor.PendingEditableAreaWork;
        }
        if (_catalog is not null) await _catalog.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
