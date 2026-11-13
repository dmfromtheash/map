using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class SelectionRefreshQueueTests
{
    [Fact]
    public void Several_changes_keep_old_intermediate_and_new_rows_in_the_delta()
    {
        var group = Guid.NewGuid();
        var a = Stamp('a') with { GroupId = group }; var b = Stamp('b'); var c = Stamp('c');
        var editor = Editor(a, b, c);
        var delta = SelectionRefreshDelta.Empty;
        editor.SceneInvalidated += (_, e) => { if (e.Selection is { } selection) delta = delta.Merge(selection); };
        editor.SelectObject(a.Id); editor.SelectObject(b.Id); editor.SelectObject(c.Id);
        Assert.Equal(3, delta.Objects.Count);
        Assert.Contains(group, delta.Groups);
        Assert.Equal(3, delta.AssetKeys.Count);
        Assert.True(delta.Affects(new(ObjectTreeRowKind.AssetKind, "a", "", null, null, null, 1, false) { AssetKey = new string('a', 64) }));
        Assert.False(delta.Affects(new(ObjectTreeRowKind.AssetKind, "d", "", null, null, null, 1, false) { AssetKey = new string('d', 64) }));
        Assert.True(delta.Affects(new(ObjectTreeRowKind.Group, "g", "", null, null, group, 1, false)));
        editor.DeselectObject();
        Assert.Contains(c.Id, delta.Objects);
    }

    [Fact]
    public void Undo_and_redo_publish_removed_and_restored_selected_rows()
    {
        var a = Stamp('a'); var editor = Editor(a);
        editor.SelectObject(a.Id);
        SelectionRefreshDelta? delta = null;
        editor.SceneInvalidated += (_, e) => { if (e.Selection is { } selection) delta = selection; };
        editor.DeleteSelectedObject();
        Assert.Contains(a.Id, delta!.Objects);
        editor.Undo(); editor.Redo();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
    }

    [Fact]
    public void Latest_rows_replace_stale_work_and_yield_to_dispatcher_after_row_limit()
    {
        var posted = new Queue<Action>(); var output = new List<int>();
        var queue = new UiWorkQueue(posted.Enqueue, rowsPerSlice: 2, millisecondsPerSlice: double.MaxValue);
        queue.Replace("rows", Enumerable.Range(0, 10).Select(i => (Action)(() => output.Add(i))));
        queue.Replace("rows", Enumerable.Range(20, 5).Select(i => (Action)(() => output.Add(i))));
        Assert.Single(posted);
        posted.Dequeue()(); Assert.Equal(new[] { 20, 21 }, output); Assert.Single(posted);
        while (posted.TryDequeue(out var run)) run();
        Assert.Equal(new[] { 20, 21, 22, 23, 24 }, output);
    }

    [Fact]
    public void Elapsed_budget_yields_between_rows_and_clear_prevents_stale_callbacks()
    {
        var posted = new Queue<Action>(); var calls = 0;
        var queue = new UiWorkQueue(posted.Enqueue, rowsPerSlice: 16, millisecondsPerSlice: 0);
        queue.Replace("rows", Enumerable.Range(0, 10).Select(_ => (Action)(() => calls++)));
        posted.Dequeue()(); Assert.Equal(1, calls); Assert.Single(posted);
        queue.Clear();
        while (posted.TryDequeue(out var run)) run();
        Assert.Equal(1, calls);
    }

    private static SceneObject Stamp(char hash) => new(ObjectId.New(), SceneObjectKind.Stamp, "stamp",
        10, 10, 30, 30, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new(new string(hash, 64)) };
    private static EditorViewModel Editor(params SceneObject[] items) => new(
        new(DocumentId.New(), "test", DateTimeOffset.UnixEpoch, new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, items)]), null, false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
}
