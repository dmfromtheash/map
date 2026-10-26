using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class GenerationControlTests
{
    private static EditorViewModel Editor(MapDocument? document = null)
    {
        document ??= HexMapFactory.Create("R20", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 64, 64, "#FFFFFFFF");
        var memory = new WorkingMemoryBudget(() => new(16L * 1024 * WorkingMemoryBudget.MiB, 8L * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        return new(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory)
        { ActiveTool = EditorTool.Hex };
    }

    [Fact]
    public async Task Cancel_preview_and_one_apply_have_explicit_document_boundaries()
    {
        var editor = Editor(); var before = editor.Session.Document;
        editor.RunHexGeneratorAll(); editor.CancelGeneration(); await editor.PendingGeneration;
        Assert.Same(before, editor.Session.Document); Assert.False(editor.GenerationHasPreview);
        editor.RunHexGeneratorStep(HexGenerationStep.Land); await editor.PendingGeneration;
        Assert.Same(before, editor.Session.Document); Assert.NotSame(before, editor.RenderDocument);
        Assert.True(editor.ApplyGeneration()); Assert.Equal(1, editor.Session.UndoDepth);
        editor.Undo(); Assert.Equal(before.Layers, editor.Session.Document.Layers);
    }

    [Fact]
    public async Task An_edit_after_preview_hides_stale_overlay_and_apply_explains_conflict()
    {
        var editor = Editor(); editor.RunHexGeneratorStep(HexGenerationStep.Land); await editor.PendingGeneration;
        editor.HexMode = HexToolMode.Protect;
        var layer = editor.ActiveHexLayer!; var center = layer.Board.Layout.Center(layer.Board.Layout.FromOffset(new(4, 4)));
        editor.BeginHexGesture(center.X, center.Y); editor.EndHexGesture();
        var edited = editor.Session.Document;
        Assert.True(editor.GenerationConflict); Assert.Same(edited, editor.RenderDocument);
        Assert.False(editor.ApplyGeneration()); Assert.Same(edited, editor.Session.Document);
        Assert.Contains("Пересчитать", editor.StatusDetail, StringComparison.Ordinal);
        editor.RecalculateGeneration(); await editor.PendingGeneration; Assert.True(editor.ApplyGeneration());
        Assert.Null(editor.ActiveHexLayer!.Board[layer.Board.Layout.FromOffset(new(4, 4))]);
    }

    [Fact]
    public async Task Stored_recipe_restores_settings_and_empty_local_selection_cannot_regenerate_everything()
    {
        var editor = Editor(); editor.HexGeneratorSettings = new() { Seed = 123, Moisture = .5 };
        editor.RunHexGeneratorStep(HexGenerationStep.Land); await editor.PendingGeneration; editor.ApplyGeneration();
        var reopened = Editor(editor.Session.Document);
        Assert.Equal(editor.HexGeneratorSettings, reopened.HexGeneratorSettings);
        reopened.GenerationSelectionOnly = true; var before = reopened.Session.Document;
        reopened.RunHexGeneratorStep(HexGenerationStep.Land); await reopened.PendingGeneration;
        Assert.Same(before, reopened.Session.Document); Assert.Contains("выделите", reopened.StatusDetail, StringComparison.Ordinal);
    }
}
