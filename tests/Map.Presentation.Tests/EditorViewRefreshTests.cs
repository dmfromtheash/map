using System.Reflection;
using Avalonia.Controls;
using Map.App.Views;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[CollectionDefinition("Editor refresh", DisableParallelization = true)]
public sealed class EditorRefreshCollection;

[Collection("Editor refresh")]
public sealed class EditorViewRefreshTests
{
    [Fact]
    public Task Exact_zoom_edits_leave_overview_preparation_for_the_frame_that_needs_it() => EditorUiThread.Run(() =>
    {
        var context = SynchronizationContext.Current;
        var item = Item(20);
        var document = new MapDocument(DocumentId.New(), "overview edit", DateTimeOffset.UnixEpoch,
            new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [item])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
        var view = UiProbe.Create(editor);
        try
        {
            Assert.True(editor.Session.Viewport.Scale >= .25);
            Assert.True(SpinWait.SpinUntil(() => view.Canvas.BackgroundOverviewStatistics.Idle, 5000));
            var prepared = view.Canvas.BackgroundOverviewStatistics.Plans;
            Assert.True(prepared > 0);
            editor.AddObject(SceneObjectKind.Rectangle);
            UiProbe.Drain(view);
            Assert.True(SpinWait.SpinUntil(() => view.Canvas.BackgroundOverviewStatistics.Idle, 5000));
            Assert.Equal(prepared, view.Canvas.BackgroundOverviewStatistics.Plans);
            Assert.Equal(1, editor.Session.UndoDepth);
            editor.Undo();
            Assert.Equal(item, Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects));
            editor.Session.Viewport.ZoomAt(0, 0, .01);
            editor.AddObject(SceneObjectKind.Rectangle);
            UiProbe.Drain(view);
            Assert.True(SpinWait.SpinUntil(() => view.Canvas.BackgroundOverviewStatistics.Idle, 5000));
            Assert.True(view.Canvas.BackgroundOverviewStatistics.Plans > prepared);
        }
        finally { UiProbe.Dispose(view); SynchronizationContext.SetSynchronizationContext(context); }
    });

    [Fact]
    public Task Selection_burst_has_one_flush_and_preserves_items_source_and_latest_inspector() => EditorUiThread.Run(() =>
    {
        var context = SynchronizationContext.Current;
        var a = Item(20); var b = Item(60);
        var doc = new MapDocument(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
            new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [a, b])]);
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
        editor.ShowPanelSection(PanelSection.Other);
        var view = UiProbe.Create(editor);
        UiProbe.Drain();
        EditorViewModel.CollectEditTimings = true;
        editor.LastEditTimings.Clear();
        var list = Field<ListBox>(view, "_objectList"); var source = list.ItemsSource;
        try
        {
            editor.SelectObject(a.Id); editor.SelectObject(b.Id);
            UiProbe.Drain();
            Assert.Equal(1, editor.LastEditTimings.Count(step => step.Step == "ui:status"));
            Assert.Same(source, list.ItemsSource);
            Assert.Equal(b.Id, editor.Session.SelectedObjectId);
            Assert.Equal(editor.XText, Field<TextBox>(view, "_xBox").Text);
            // Input can arrive between the Render-priority flush and Background row work.
            // Replacing an unfinished row iterator must retain every dirty old selection.
            editor.SelectObject(a.Id);
            Flush(view);
            editor.SelectObject(b.Id);
            Flush(view);
            var pending = Field<SelectionRefreshDelta>(view, "_queuedRowSelectionDelta");
            Assert.Contains(a.Id, pending.Objects); Assert.Contains(b.Id, pending.Objects);
            UiProbe.Drain();
            var note = Field<StackPanel>(view, "_noteList").Children.Single();
            var version = Field<StackPanel>(view, "_checkpointList").Children.Single();
            editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
            editor.UpdatePlacementPreview(200, 200);
            Assert.True(editor.CommitPendingPlacement());
            UiProbe.Drain();
            Assert.Same(note, Field<StackPanel>(view, "_noteList").Children.Single());
            Assert.Same(version, Field<StackPanel>(view, "_checkpointList").Children.Single());
        }
        finally
        {
            EditorViewModel.CollectEditTimings = false;
            UiProbe.Dispose(view);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    });
    private static T Field<T>(EditorView view, string name) =>
        (T)typeof(EditorView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    private static void Flush(EditorView view) => typeof(EditorView)
        .GetMethod("FlushEditorUi", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null);
    private static SceneObject Item(double x) => new(ObjectId.New(), SceneObjectKind.Rectangle, "item",
        x, 20, 30, 30, 0, "#FFFFFFFF", "#FF000000", 1, true, false);
}
