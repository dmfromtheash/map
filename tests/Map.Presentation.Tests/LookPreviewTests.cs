using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>«Вид»: pointing at a look shows the whole map in it without touching the map or its history.</summary>
public sealed class LookPreviewTests
{
    [Fact]
    public void A_trial_look_is_shown_not_stored_and_ends_with_any_edit()
    {
        var editor = new EditorViewModel(new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(512, 512, "px", "#FF1E2430"), [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF1E2430")]),
            null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var map = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        var trial = map with { Style = MapStyle.For(MapStylePreset.OldMapBlaeu) with { Enabled = true } };

        editor.PreviewLook(trial);
        Assert.Same(trial, editor.RenderDocument);
        Assert.True(editor.LookPreviewShown);
        Assert.Same(map, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);

        editor.PreviewLook(null);
        Assert.False(editor.LookPreviewShown);
        Assert.Null(editor.RenderDocument.Style);

        // A trial made from an older map is never shown over a newer one.
        editor.PreviewLook(trial);
        editor.ApplyStylePreset(MapStylePreset.InkPenHatching);
        Assert.False(editor.LookPreviewShown);
        Assert.Equal(MapStylePreset.InkPenHatching, editor.RenderDocument.Style!.Preset);

        // «Сравнить»: the map without style and light.
        Assert.Null(editor.LookWithoutEffects().Style);
        Assert.Null(editor.LookWithoutEffects().Lighting);
    }
}
