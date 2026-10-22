using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class AtlasWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-atlas-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectStore _store;
    public AtlasWorkflowTests() { Directory.CreateDirectory(_root); _store = new(new RecoveryJournal(Path.Combine(_root, "recovery"))); }
    public void Dispose() => Directory.Delete(_root, true);
    private string PathFor(string name) => Path.Combine(_root, name);
    private static MapDocument Document() => new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new(640, 480, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
    private EditorViewModel Editor(MapDocument doc, string path, FakeFilePicker? picker = null) => new(doc, path, false, _store, new FakeRecentMaps(),
        picker ?? new(), new FakePrompts(), new FakeLog(), localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
    private void Write(MapDocument doc, string path) => _store.Save(doc, path, new("r25", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(doc));

    [Fact]
    public async Task Atlas_region_registration_save_as_and_navigation_use_real_store()
    {
        var source = Document(); var parent = PathFor("parent.mapproj"); Write(source, parent);
        var picker = new FakeFilePicker { NextSavePath = PathFor("region.mapproj") }; var editor = Editor(source, parent, picker);
        editor.BeginCanvasWorkflow(); editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
        editor.RegionXText = "100"; editor.RegionYText = "90"; editor.RegionWidthText = "300"; editor.RegionHeightText = "200"; editor.RegionTitleText = "Регион";
        Assert.True(await editor.CreateMapFromRegionAsync()); Assert.Same(source, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        Assert.NotNull(_store.Open(parent).Document.Atlas); Assert.Equal(2, editor.Atlas!.Maps.Length);
        var child = Assert.Single(editor.AtlasChildren); Assert.Equal(new AtlasTransform(100, 90, 300, 200, 300, 200), child.Link.Transform);
        Assert.NotEmpty(child.Preview!); Assert.Empty(await editor.CheckAtlasAsync());
        // Save As immediately after opening, before the asynchronous navigation load finishes.
        var saveAsEditor = Editor(_store.Open(parent).Document, parent, picker);
        picker.NextSavePath = PathFor("nested/renamed.mapproj"); Assert.True(await saveAsEditor.SaveAsync(saveAs: true));
        Assert.NotNull(saveAsEditor.Atlas); Assert.Equal(editor.AtlasPath, saveAsEditor.AtlasPath);
        var moved = _store.Open(picker.NextSavePath).Document;
        Assert.Equal(source.Id, moved.Id); Assert.StartsWith("..", moved.Atlas!.File);
        Assert.Single(AtlasStore.Read(editor.AtlasPath!).Maps, m => m.Id == source.Id.Value);
        var childEditor = Editor(_store.Open(PathFor("region.mapproj")).Document, PathFor("region.mapproj"));
        await childEditor.RefreshAtlasAsync();
        string? requested = null; childEditor.AtlasMapRequested += (_, path) => requested = path;
        childEditor.OpenAtlasMap(source.Id.Value); Assert.Equal(Path.GetFullPath(picker.NextSavePath), requested);
    }

    [Fact]
    public async Task Atlas_hex_creation_saves_note_and_transform_and_reuses_existing_child()
    {
        var source = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var parent = PathFor("hex.mapproj"); Write(source, parent); var editor = Editor(source, parent);
        var hex = source.Layers.OfType<HexLayer>().Single().Board.Layout.FromOffset(new(2, 2));
        var path = await editor.PrepareHexChildMapAsync(hex); Assert.NotNull(path);
        var link = Assert.Single(editor.Atlas!.Links); Assert.Equal("hex", link.Kind);
        var bounds = source.Layers.OfType<HexLayer>().Single().Board.Layout.Bounds(hex);
        Assert.Equal(bounds.Left, link.Transform.X); Assert.Equal(bounds.Right - bounds.Left, link.Transform.Width);
        Assert.Equal(Path.GetFileName(path), _store.Open(parent).Document.Layers.OfType<HexLayer>().Single().Notes[hex].ChildMap);
        Assert.Equal(path, await editor.PrepareHexChildMapAsync(hex)); Assert.Equal(2, editor.Atlas.Maps.Length);
        Assert.Equal(1, editor.Session.UndoDepth); editor.Undo(); Assert.Null(editor.ActiveHexLayer!.Notes.GetValueOrDefault(hex)?.ChildMap); editor.Redo();
        Assert.Equal(Path.GetFileName(path), editor.ActiveHexLayer!.Notes[hex].ChildMap);
    }

    [Theory]
    [InlineData("lost.mapproj")]
    [InlineData("../outside.mapproj")]
    public async Task Atlas_missing_or_external_legacy_hex_link_returns_message_without_replacement(string linked)
    {
        var source = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var layer = source.Layers.OfType<HexLayer>().Single(); var hex = new HexCoord(0, 0);
        source = source.WithLayer(layer.Id, _ => layer with { Notes = layer.Notes.SetItem(hex, new HexNote("Место") { ChildMap = linked }) });
        var parent = PathFor("hex.mapproj"); Write(source, parent); var editor = Editor(source, parent);
        Assert.Null(await editor.PrepareHexChildMapAsync(hex)); Assert.NotEmpty(editor.StatusDetail);
        Assert.Single(Directory.GetFiles(_root, "*.mapproj")); Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public async Task Atlas_broken_manifest_is_a_readable_issue_not_an_exception()
    {
        var source = Document(); var parent = PathFor("parent.mapproj"); Write(source, parent);
        File.WriteAllText(AtlasStore.ManifestPath(source, parent), "{broken");
        var editor = Editor(source, parent); var issues = await editor.CheckAtlasAsync();
        Assert.Single(issues); Assert.Contains("Не удалось открыть атлас", issues[0].Message);
    }

    [Fact]
    public async Task Atlas_corrupt_recovery_journal_is_reported_by_dashboard_without_opening_half_atlas()
    {
        var source = Document(); var parent = PathFor("parent.mapproj"); Write(source, parent);
        var pending = AtlasStore.ManifestPath(source, parent) + ".pending";
        File.WriteAllText(pending, "{broken");
        var dashboard = new DashboardViewModel(_store, new FakeRecentMaps(), new FakeLog(), new FakeFilePicker());
        Assert.Null(await dashboard.TryOpenAsync(parent)); Assert.Contains("Операция атласа не завершена", dashboard.LastError);
        Assert.True(File.Exists(pending));
    }
}
