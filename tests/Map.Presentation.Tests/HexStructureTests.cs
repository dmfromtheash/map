using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Resources;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class HexStructureTests
{
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("map-r16-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    private static EditorViewModel Editor()
    {
        var document = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 48, 5, 5, "#FFFFFFFF");
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
                16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB)));
    }

    [Fact]
    public void Poi_editor_reorders_renames_hides_removes_and_undoes()
    {
        var editor = Editor();
        var hex = editor.ActiveHexLayer!.Board.Layout.FromOffset(new HexOffset(1, 1));
        editor.OpenHexKey(hex);
        editor.AddHexPoi(); editor.AddHexPoi("starter/ruins");
        editor.SetHexPoi(1, editor.HexPoints[1] with { Name = "Тайна", PlayerVisible = false });
        editor.MoveHexPoi(1, -1);
        Assert.Equal("Тайна", editor.HexPoints[0].Name);
        editor.RemoveHexPoi(1);
        Assert.Single(editor.HexPoints);
        editor.Undo(); Assert.Equal(2, editor.HexPoints.Count);
        editor.Undo(); Assert.Equal("Тайна", editor.HexPoints[1].Name);
        editor.Redo(); Assert.False(editor.HexPoints[0].PlayerVisible);
    }

    [Fact]
    public void Region_parent_is_undoable_and_presentation_does_not_edit_document()
    {
        var editor = Editor();
        editor.AddHexRegion("Королевство"); editor.AddHexRegion("Провинция");
        Assert.True(editor.SetHexRegionParent(2,1));
        Assert.False(editor.SetHexRegionParent(1,2));
        editor.RenameHexRegion(2,"Край");
        Assert.Equal(1, editor.HexRegions[1].ParentRegion);
        var doc = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        foreach (var view in Enum.GetValues<HexPresentation>()) editor.HexPresentation = view;
        Assert.Same(doc, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        editor.RemoveHexRegion(1); Assert.Equal(0, editor.HexRegions.Single().ParentRegion);
        editor.Undo(); Assert.Equal(1, editor.HexRegions[1].ParentRegion);
    }

    [Fact]
    public void Export_json_markdown_and_obsidian_preserve_structure_and_filter_secrets()
    {
        var editor = Editor();
        var hex = editor.ActiveHexLayer!.Board.Layout.FromOffset(new HexOffset(1,1));
        editor.OpenHexKey(hex); editor.AddHexPoi(); editor.AddHexPoi("starter/ruins");
        editor.SetHexPoi(0, editor.HexPoints[0] with { Name = "Площадь" });
        editor.SetHexPoi(1, editor.HexPoints[1] with { Name = "Скрытый храм", PlayerVisible = false });
        editor.AddHexRegion("Королевство"); editor.AddHexRegion("Край"); editor.SetHexRegionParent(2,1);
        var doc = editor.Session.Document;
        var gm = new ExportScene(doc);
        var player = new ExportScene(doc, recipient: ExportRecipient.Players);
        var json = HexJsonFormat.ToJson(gm).ToJsonString();
        var restored = HexJsonFormat.Read(json, editor.ActiveHexLayer!);
        Assert.Equal(2, restored.Board[hex]!.PointsOfInterest.Count);
        Assert.Equal(1, restored.Regions[1].ParentRegion);
        var publicJson = HexJsonFormat.ToJson(player).ToJsonString();
        Assert.DoesNotContain("Скрытый храм", publicJson); Assert.DoesNotContain("starter/ruins", publicJson);
        Assert.Single(player.Layers.OfType<HexLayer>().Single().Board[hex]!.PointsOfInterest);
        Assert.Contains("Скрытый храм", editor.HexKeyMarkdown(false));
        Assert.DoesNotContain("Скрытый храм", editor.HexKeyMarkdown(true));
        using var folder = new TempFolder();
        var gmPath = Path.Combine(folder.Path, "ведущий"); var playerPath = Path.Combine(folder.Path, "игрок");
        HexObsidianExporter.Write(gm, gmPath); HexObsidianExporter.Write(player, playerPath);
        var gmText = string.Join("\n", Directory.GetFiles(gmPath).Select(File.ReadAllText));
        var playerText = string.Join("\n", Directory.GetFiles(playerPath).Select(File.ReadAllText));
        Assert.Contains("Скрытый храм", gmText); Assert.Contains("parent_region", gmText);
        Assert.DoesNotContain("Скрытый храм", playerText); Assert.DoesNotContain("starter/ruins", playerText);
        Assert.Contains("Площадь", playerText);
    }
}
