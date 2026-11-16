using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[Collection("Editor refresh")]
public sealed class EditUiFrameBudgetTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public Task Native_ladder_edits_leave_panel_construction_outside_the_next_frame(int count) => EditorUiThread.Run(() =>
    {
        using var scope = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var context = SynchronizationContext.Current;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var folder = Path.Combine(Path.GetTempPath(), "edit-budget-" + Guid.NewGuid().ToString("N"));
        using var catalog = new AsyncCatalog(new AssetCatalog(folder));
        var record = catalog.Value.Import(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"), new(), DateTimeOffset.UnixEpoch);
        var resolver = new CatalogAssetContentResolver(catalog.Value);
        var reference = resolver.Resolve(record).Reference!;
        var theme = new Map.App.App(); theme.Initialize();
        var resources = theme.Resources; theme.Resources = new ResourceDictionary();
        var styles = theme.Styles.ToArray(); theme.Styles.Clear();
        try { Run(16, false); Run(count, true); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }

        void Run(int size, bool measured)
        {
            var objects = Enumerable.Range(0, size).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
                "mountain " + i, 65000 + i % 10 * 80, 65000 + i / 10 * 64, 64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new(reference.Sha256) }).ToArray();
            var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
            var document = new MapDocument(DocumentId.New(), "UI edit budget", DateTimeOffset.UnixEpoch,
                new(131072, 131072, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), layer], [reference]);
            var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
                new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog.Value,
                assetResolver: resolver, availableFontFamilies: ["Arial"]);
            editor.DerivedIndexPreparation.GetAwaiter().GetResult();
            DocumentDerivedIndexes.Prepare(document);
            ObjectTreeRows.PrepareLayerIndexes(document);
            var view = UiProbe.Create(editor);
            view.Width = 1536; view.Height = 864; view.Resources = resources; view.Styles.AddRange(styles);
            var layout = new PlacementStepCostTests.CpuLayoutRoot { Content = view, Width = 1536, Height = 864, RequestedThemeVariant = ThemeVariant.Dark };
            var violations = new List<string>();
            try
            {
                UiProbe.Drain(view); Layout();
                EditorViewModel.CollectEditTimings = true;
                foreach (var route in new[] { "select", "drag", "place-10" })
                {
                    if (route == "place-10") { Assert.True(editor.ChooseGalleryVariant(catalog.Value.Sets[0].Id, null)); UiProbe.Drain(view); Layout(); }
                    for (var step = 0; step < 10; step++)
                    {
                        editor.LastEditTimings.Clear();
                        var visited = SceneObjectList.EnumeratedOnThisThread;
                        var allocated = GC.GetAllocatedBytesForCurrentThread();
                        var started = Stopwatch.GetTimestamp();
                        if (route == "select") { editor.DeselectObject(); editor.SelectObject(objects[^1].Id); }
                        if (route == "drag")
                        {
                            var selected = editor.Session.SelectedObject!;
                            var x = selected.X + selected.Width / 2; var y = selected.Y + selected.Height / 2;
                            Assert.True(editor.BeginDrag(x, y)); editor.UpdateDragTo(x + 3, y + 2); editor.EndDrag();
                        }
                        if (route == "place-10")
                        {
                            var p = editor.Session.Viewport.WorldToScreen(65376 + step * 32, 65656);
                            view.Canvas.MovePlacementPointerForSmoke(p.X, p.Y);
                            Assert.True(view.Canvas.CommitPlacementForSmoke());
                        }
                        var commandMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        // This is the dispatcher work that can run ahead of the next render.
                        // Background and Loaded panel jobs settle below, outside this budget.
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render); Layout();
                        var frameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        var enumerated = SceneObjectList.EnumeratedOnThisThread - visited;
                        var frameSections = editor.LastEditTimings.Select(t => t.Step).ToArray();
                        UiProbe.Drain(view); Layout();
                        var settledMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (measured)
                        {
                            Console.WriteLine(FormattableString.Invariant($"EDIT-UI count={size} route={route} step={step} commandMs={commandMs:F4} frameMs={frameMs:F4} settledMs={settledMs:F4} frameBytes={bytes} enumerated={enumerated}"));
                            foreach (var timing in editor.LastEditTimings)
                                Console.WriteLine(FormattableString.Invariant($"EDIT-UI-PHASE count={size} route={route} step={step} name={timing.Step} ms={timing.Ms:F4}"));
                            // 128 KiB allows command/delta nodes, inspector values and layout.
                            // Building a panel costs megabytes and must run after the frame.
                            if (bytes > 128 * 1024) violations.Add($"{route}/{step}: {bytes} frame bytes > 128 KiB");
                            if (enumerated > 256) violations.Add($"{route}/{step}: {enumerated} unrelated object visits");
                            if (frameSections.Contains("ui:follow-selection") || frameSections.Contains("ui:stamp-tool-panel"))
                                violations.Add($"{route}/{step}: panel construction before frame");
                            if (route == "drag" && editor.LastEditTimings.Any(t => t.Step == "ui:deferred/context"))
                                violations.Add($"{route}/{step}: translation refreshed unchanged stamp properties");
                        }
                        Assert.Equal(editor.XText, Field<TextBox>("_xBox").Text);
                    }
                }
                Assert.Equal(size + 10, ((ObjectLayer)editor.Session.Document.FindLayer(layer.Id)!).Objects.Count);
                Assert.Equal(20, editor.Session.UndoDepth);
                var edited = ((ObjectLayer)editor.Session.Document.FindLayer(layer.Id)!).Objects.ToArray();
                editor.CancelPlacement();
                for (var i = 0; i < 20; i++) editor.Undo();
                Assert.Equal(objects, ((ObjectLayer)editor.Session.Document.FindLayer(layer.Id)!).Objects);
                for (var i = 0; i < 20; i++) editor.Redo();
                Assert.Equal(edited, ((ObjectLayer)editor.Session.Document.FindLayer(layer.Id)!).Objects);
                if (measured) Assert.Empty(violations);
            }
            finally
            {
                EditorViewModel.CollectEditTimings = false; UiProbe.Dispose(view); layout.Content = null;
                typeof(TopLevel).GetMethod("HandleClosed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(layout, null);
                view.Styles.Clear(); view.Resources = new ResourceDictionary();
            }
            void Layout() { layout.Measure(new Size(1536, 864)); layout.Arrange(new Rect(0, 0, 1536, 864)); layout.UpdateLayout(); }
            T Field<T>(string name) => (T)typeof(Map.App.Views.EditorView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        }
    });

    private sealed class AsyncCatalog(AssetCatalog value) : IDisposable
    {
        public AssetCatalog Value => value;
        public void Dispose()
        {
            var folder = value.Root;
            value.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
