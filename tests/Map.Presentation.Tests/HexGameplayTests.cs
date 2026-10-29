using Map.Core.Documents;
using Map.Core.Resources;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class HexGameplayTests
{
    private static EditorViewModel Editor()
    {
        var d = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 48, 5, 5, "#FFFFFFFF");
        return new EditorViewModel(d, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), memory: new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
                16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB)));
    }

    [Fact]
    public void Party_drag_has_one_undo_per_step_and_history_clear_keeps_visits()
    {
        var e = Editor(); var l = e.ActiveHexLayer!; var a = l.Board.Layout.FromOffset(new(1, 1)); var b = a.Neighbors().First();
        e.OpenHexKey(a); e.AddHexPoi(); e.OpenHexKey(b); e.AddHexPoi(); e.HexFogEnabled = true; e.HexPartyRadius = 0;
        e.HexMode = HexToolMode.Party;
        var depth = e.Session.UndoDepth;
        var p = l.Board.Layout.Center(a); var q = l.Board.Layout.Center(b);
        Assert.True(e.BeginHexGesture(p.X, p.Y)); e.ContinueHexGesture(q.X, q.Y); e.EndHexGesture();
        Assert.Equal(depth + 2, e.Session.UndoDepth); Assert.Equal(b, e.ActiveHexLayer!.Party.Position);
        Assert.True(e.ActiveHexLayer.Board[b]!.Revealed);
        e.Undo(); Assert.Equal(a, e.ActiveHexLayer!.Party.Position); Assert.False(e.ActiveHexLayer.Board[b]!.Revealed);
        e.Redo(); e.ClearHexPartyHistory(); Assert.Empty(e.ActiveHexLayer!.Party.History);
        Assert.Contains(b, e.ActiveHexLayer.Party.Visited);
        e.Undo(); Assert.Equal(new[] { a, b }, e.ActiveHexLayer!.Party.History);
        // Drag the existing marker with the regular drawing mode; it must not paint terrain.
        e.HexMode = HexToolMode.Place;
        Assert.True(e.BeginHexGesture(q.X, q.Y)); e.ContinueHexGesture(p.X, p.Y); e.EndHexGesture();
        Assert.Equal(a, e.ActiveHexLayer!.Party.Position);
    }

    [Fact]
    public void Rumor_exports_json_markdown_obsidian_hide_truth_and_respect_reveal()
    {
        var e = Editor(); var h = e.ActiveHexLayer!.Board.Layout.FromOffset(new(1, 1));
        e.OpenHexKey(h); e.AddHexPoi(); e.SetHexPoi(0, e.HexPoints[0] with { Name = "Правда о точке", Rumor = "Слух о точке", ApproximateLabel = "Примерная точка" });
        e.AddHexRegion("Истинный край"); e.SetHexRegionKnowledge(1, "Слух о крае", "Примерный край");
        e.SetHexKey(new HexNote("Истинное имя", gmText: "Секрет ведущего", playerText: "Истинный текст", childMap: "секрет.mapproj")
        { Rumor = "Слух о месте", ApproximateLabel = "Примерное имя", RevealCondition = RevealCondition.Manual });
        var player = new ExportScene(e.Session.Document, recipient: ExportRecipient.Players);
        Assert.Empty(player.Layers.OfType<HexLayer>().Single().Notes);
        e.SetHexKey(e.HexKeyNote! with { ManuallyRevealed = true });
        e.HexCampaignDay = 14; e.MoveHexParty(h);
        player = new ExportScene(e.Session.Document, recipient: ExportRecipient.Players);
        var json = HexJsonFormat.ToJson(player).ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var md = e.HexKeyMarkdown(true);
        var folder = Path.Combine(Path.GetTempPath(), "map-r17-" + Guid.NewGuid());
        try
        {
            HexObsidianExporter.Write(player, folder);
            var texts = string.Join("\n", Directory.GetFiles(folder).Select(File.ReadAllText));
            foreach (var content in new[] { json, md, texts })
            {
                Assert.Contains("Слух о месте", content); Assert.Contains("Примерное имя", content);
                Assert.Contains("Слух о крае", content); Assert.Contains("Слух о точке", content);
                foreach (var secret in new[] { "Истинное имя", "Истинный текст", "Секрет ведущего", "секрет.mapproj", "Правда о точке", "Истинный край" })
                    Assert.DoesNotContain(secret, content);
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        var gmJson = HexJsonFormat.ToJson(e.Session.Document).ToJsonString();
        var restored = HexJsonFormat.Read(gmJson, e.ActiveHexLayer!);
        Assert.Equal(e.HexKeyNote, restored.Notes[h]); Assert.Equal(e.HexPoints[0], restored.Board[h]!.PointsOfInterest[0]);
        Assert.Equal(e.ActiveHexLayer!.Party.Position, restored.Party.Position); Assert.Equal(14, restored.CampaignDay);
        Assert.Equal("Истинное имя", e.HexKeyNote!.Title);
        e.Undo(); e.Undo(); Assert.Equal(0, e.HexCampaignDay);
    }
}
