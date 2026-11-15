using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[Collection("Editor refresh")]
public sealed class PlacementStepCostTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(200_000)]
    [InlineData(10_000, true)]
    public Task Native_place_and_undo_steps_with_owner_stamps(int count, bool differentKind = false) => EditorUiThread.Run(() =>
    {
        using var services = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var context = SynchronizationContext.Current;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var fixture = Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
        var folder = Path.Combine(Path.GetTempPath(), "place-cost-" + Guid.NewGuid().ToString("N"));
        var catalog = new AssetCatalog(folder);
        try
        {
            var record = catalog.Import(fixture, new(), DateTimeOffset.UnixEpoch);
            var resolver = new CatalogAssetContentResolver(catalog);
            var resolution = resolver.Resolve(record);
            Assert.NotNull(resolution.Reference);
            var documentReference = resolution.Reference!;
            if (differentKind)
            {
                var other = catalog.Import(Path.Combine(Path.GetDirectoryName(fixture)!, "owner-mountain-02.png"), new(), DateTimeOffset.UnixEpoch);
                documentReference = resolver.Resolve(other).Reference!;
                Assert.NotNull(documentReference);
            }
            var theme = new Map.App.App();
            theme.Initialize();
            var resources = theme.Resources;
            theme.Resources = new ResourceDictionary();
            var styles = theme.Styles.ToArray();
            theme.Styles.Clear();
            Run(16, measured: false);
            Run(count, measured: true);

            void Run(int size, bool measured)
            {
                var random = new Random(12345);
                var style = new StampStyle(documentReference.Sha256);
                var items = Enumerable.Range(0, size).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
                    "mountain " + i, random.NextDouble() * 130000, random.NextDouble() * 130000,
                    64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style }).ToArray();
                var layerId = LayerId.New();
                var document = new MapDocument(DocumentId.New(), "placement CPU probe", DateTimeOffset.UnixEpoch,
                    new(131072, 131072, "px", "#FFFFFFFF"),
                    [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                     new ObjectLayer(layerId, "objects", true, false, 1, items)], [documentReference]);
                var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
                    new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog,
                    assetResolver: resolver, availableFontFamilies: ["Arial"]);
                editor.DerivedIndexPreparation.GetAwaiter().GetResult();
                DocumentDerivedIndexes.Prepare(document);
                ObjectTreeRows.PrepareLayerIndexes(document);
                // The route enters placement after a drag/redo with the original stamp selected.
                editor.SelectObject(items[^1].Id);
                var view = UiProbe.Create(editor);
                view.Width = 1536;
                view.Height = 864;
                view.Resources = resources;
                view.Styles.AddRange(styles);
                var layoutRoot = new CpuLayoutRoot { Content = view, Width = 1536, Height = 864, RequestedThemeVariant = ThemeVariant.Dark };
                var visitedPerStep = new List<long>();
                var sources = new List<(object? Before, object? After)>();
                var layerRows = new List<(object? Before, object? After)>();
                var removalChecks = new List<long>();
                try
                {
                    UiProbe.Drain(view);
                    Layout();
                    EditorViewModel.CollectEditTimings = true;
                    editor.LastEditTimings.Clear();
                    var armStart = Stopwatch.GetTimestamp();
                    Assert.True(editor.ChooseGalleryVariant(catalog.Sets[0].Id, null));
                    UiProbe.Drain(view);
                    Layout();
                    if (measured) Console.WriteLine(FormattableString.Invariant($"ARM count={size} differentKind={differentKind} ms={Stopwatch.GetElapsedTime(armStart).TotalMilliseconds:F4}"));
                    for (var step = 0; step < 10; step++)
                    {
                        var index = step;
                        Measure("place", step, () =>
                        {
                            var p = editor.Session.Viewport.WorldToScreen(65536 - 160 + index * 32, 65536 + 120);
                            view.Canvas.MovePlacementPointerForSmoke(p.X, p.Y);
                        }, () => Assert.True(view.Canvas.CommitPlacementForSmoke()));
                        Assert.Equal(size + step + 1, Objects().Count);
                        Assert.Equal(step + 1, editor.Session.UndoDepth);
                        Assert.Equal(items[^1].Id, editor.Session.SelectedObjectId);
                        Assert.True(editor.HasPendingPlacement);
                        AssertRows(size + step + 1);
                    }
                    var placed = Objects().Skip(size).ToArray();
                    Assert.Equal(10, placed.Select(o => o.Name).Distinct().Count());
                    Assert.All(placed, o => Assert.Equal(record.Sha256, o.Stamp!.AssetSha256));
                    editor.CancelPlacement();
                    UiProbe.Drain(view);
                    Layout();
                    for (var step = 0; step < 10; step++)
                    {
                        Measure("undo-place", step, () => { }, editor.Undo);
                        Assert.Equal(size + 9 - step, Objects().Count);
                        Assert.Equal(9 - step, editor.Session.UndoDepth);
                        AssertRows(size + 9 - step);
                    }
                    Assert.Equal(items, Objects());
                    Assert.Equal(10, editor.Session.RedoDepth);
                    for (var step = 0; step < 10; step++) editor.Redo();
                    UiProbe.Drain(view);
                    Layout();
                    Assert.Equal(placed, Objects().Skip(size));
                    Assert.Same(documentReference, editor.Session.Document.Assets.Single(a => a.Sha256 == documentReference.Sha256));
                    Assert.Equal(differentKind ? 2 : 1, editor.Session.Document.Assets.Count);
                    if (measured) Assert.All(visitedPerStep, visited => Assert.InRange(visited, 0, 256));
                    if (measured) Assert.All(sources, pair => Assert.Same(pair.Before, pair.After));
                    if (measured) Assert.All(layerRows, pair => Assert.Same(pair.Before, pair.After));
                    if (measured) Assert.All(removalChecks, checks => Assert.InRange(checks, 0, 256));
                }
                finally
                {
                    EditorViewModel.CollectEditTimings = false;
                    UiProbe.Dispose(view);
                    layoutRoot.Content = null;
                    typeof(TopLevel).GetMethod("HandleClosed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(layoutRoot, null);
                    view.Styles.Clear();
                    view.Resources = new ResourceDictionary();
                    SynchronizationContext.SetSynchronizationContext(context);
                }

                IReadOnlyList<SceneObject> Objects() => ((ObjectLayer)editor.Session.Document.FindLayer(layerId)!).Objects;
                ListBox ObjectList() => (ListBox)typeof(Map.App.Views.EditorView).GetField("_objectList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                Control LayerRow() => ((StackPanel)typeof(Map.App.Views.EditorView).GetField("_layerList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Children[0];
                void AssertRows(int expected)
                {
                    var visibleCount = differentKind ? size : expected;
                    var shown = Assert.IsAssignableFrom<IReadOnlyList<ObjectTreeRow>>(ObjectList().ItemsSource);
                    Assert.Equal(visibleCount + 1, shown.Count);
                    Assert.Equal(visibleCount, shown[0].ChildCount);
                    Assert.Equal(differentKind ? items[^1].Id : Objects()[^1].Id, shown[1].Object!.Id);
                    // The Layers tab is retained but detached while the Assets tab is open.
                    var layerRow = Assert.IsType<Border>(LayerRow());
                    var panel = Assert.IsType<DockPanel>(layerRow.Child);
                    var select = Assert.IsType<ToggleButton>(panel.Children[^1]);
                    var label = Assert.IsType<DockPanel>(select.Content);
                    Assert.Equal(expected.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")), Assert.IsType<TextBlock>(label.Children[0]).Text);
                    Assert.Contains(EditorLabels.Objects(expected), AutomationProperties.GetName(select));
                }
                void Layout()
                {
                    layoutRoot.Measure(new Size(1536, 864));
                    layoutRoot.Arrange(new Rect(0, 0, 1536, 864));
                    layoutRoot.UpdateLayout();
                }
                void Measure(string route, int step, Action pointer, Action commit)
                {
                    editor.LastEditTimings.Clear();
                    var visited = SceneObjectList.EnumeratedOnThisThread;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var rebuilds = view.ObjectListRebuildCount;
                    var list = ObjectList();
                    var beforeSource = list.ItemsSource;
                    var beforeLayer = LayerRow();
                    var checks = ObjectTreeRows.KindRemovalChecksOnThisThread;
                    pointer();
                    var pointerMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    commit();
                    var vmMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    UiProbe.Drain(view);
                    var panelsMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Layout();
                    var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    var enumerated = SceneObjectList.EnumeratedOnThisThread - visited;
                    if (measured)
                    {
                        visitedPerStep.Add(enumerated);
                        removalChecks.Add(ObjectTreeRows.KindRemovalChecksOnThisThread - checks);
                        layerRows.Add((beforeLayer, LayerRow()));
                        if (route != "place" || step > 0)
                        {
                            sources.Add((beforeSource, list.ItemsSource));
                        }
                        Console.WriteLine(FormattableString.Invariant($"PLACE count={size} differentKind={differentKind} route={route} step={step} pointerMs={pointerMs:F4} vmMs={vmMs:F4} panelsMs={panelsMs:F4} totalMs={totalMs:F4} bytes={bytes} enumerated={enumerated} removalChecks={ObjectTreeRows.KindRemovalChecksOnThisThread - checks} rebuilt={view.ObjectListRebuildCount - rebuilds}"));
                        foreach (var timing in editor.LastEditTimings)
                            Console.WriteLine("SECTION count=" + size + " route=" + route + " step=" + step + " name=" + timing.Step
                                + " ms=" + timing.Ms.ToString("F4", CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        finally
        {
            catalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    });

    private sealed class CpuLayoutRoot : TopLevel
    {
        public CpuLayoutRoot() : base(CreatePlatform())
        {
            var renderer = typeof(TopLevel).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(this)!;
            renderer.GetType().GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(renderer, null);
        }
        private static ITopLevelImpl CreatePlatform()
        {
            var loop = typeof(Compositor).Assembly.GetType("Avalonia.Rendering.IRenderLoop")!;
            var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(loop).Invoke(locator, null)!;
            var constant = binding.GetType().GetMethod("ToConstant")!;
            if (constant.IsGenericMethodDefinition) constant = constant.MakeGenericMethod(loop);
            var timerType = typeof(Compositor).Assembly.GetType("Avalonia.Rendering.DefaultRenderTimer")!;
            var renderLoopType = typeof(Compositor).Assembly.GetType("Avalonia.Rendering.RenderLoop")!;
            var renderLoop = renderLoopType.GetMethod("FromTimer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, [Activator.CreateInstance(timerType, 60)]);
            constant.Invoke(binding, [renderLoop]);
            return DispatchProxy.Create<ITopLevelImpl, NoWindow>();
        }
    }

    public class NoWindow : DispatchProxy
    {
        private readonly Compositor _compositor = (Compositor)Activator.CreateInstance(typeof(Compositor),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [null, false], null)!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ClientSize" => new Size(1536, 864),
            "get_RenderScaling" or "get_DesktopScaling" => 1d,
            "get_Surfaces" => Array.Empty<object>(),
            "get_Compositor" => _compositor,
            _ => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }
}
