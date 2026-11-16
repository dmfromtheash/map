using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[Collection("Editor refresh")]
public sealed class SelectionStepCostTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    [InlineData(200_000)]
    public Task Native_select_step_with_owner_stamps(int count) => EditorUiThread.Run(() =>
    {
        using var services = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var context = SynchronizationContext.Current;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var art = File.ReadAllBytes(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(art));
        var theme = new Map.App.App();
        theme.Initialize();
        var resources = theme.Resources;
        theme.Resources = new ResourceDictionary();
        var styles = theme.Styles.ToArray();
        theme.Styles.Clear();
        // Exercise the same code on a separate small document before measuring either size.
        Run(16, measured: false);
        Run(count, measured: true);

        void Run(int size, bool measured)
        {
            var random = new Random(12345);
            var style = new StampStyle(sha);
            var items = Enumerable.Range(0, size).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
                "mountain " + i, i == size - 1 ? 65504 : random.NextDouble() * 130000,
                i == size - 1 ? 65512 : random.NextDouble() * 130000,
                64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style }).ToArray();
            var document = new MapDocument(DocumentId.New(), "selection CPU probe", DateTimeOffset.UnixEpoch,
                new(131072, 131072, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "objects", true, false, 1, items)],
                [new MapAssetReference(sha, "owner mountain", "image/png", "png", ProjectAssetStorage.Embedded,
                    DateTimeOffset.UnixEpoch, "owner", "owner", embeddedContent: art)]);
            var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
                new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
            editor.DerivedIndexPreparation.GetAwaiter().GetResult();
            editor.ActiveTool = EditorTool.Select;
            var view = UiProbe.Create(editor);
            view.Width = 1536;
            view.Height = 864;
            view.Resources = resources;
            view.Styles.AddRange(styles);
            var layoutRoot = new CpuLayoutRoot { Content = view, Width = 1536, Height = 864, RequestedThemeVariant = ThemeVariant.Dark };
            UiProbe.Drain(view);
            Layout();
            var selectedId = items[^1].Id;
            EditorViewModel.CollectEditTimings = true;
            var list = (ListBox)typeof(Map.App.Views.EditorView).GetField("_objectList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var unnecessaryPanels = new List<string>();
            var listRebuilds = new List<int>();
            try
            {
                for (var step = 0; step < 10; step++)
                {
                    editor.LastEditTimings.Clear();
                    var visited = SceneObjectList.EnumeratedOnThisThread;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var rebuilds = view.ObjectListRebuildCount;
                    // NativeFpsGrid.cs edit/select: both calls form ONE route step.
                    editor.DeselectObject();
                    editor.SelectObject(selectedId);
                    var vmMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    // No canvas frame on this Mac: include all queued production panel work.
                    UiProbe.Drain(view);
                    var panelsMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Layout();
                    var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    var enumerated = SceneObjectList.EnumeratedOnThisThread - visited;
                    var rebuilt = view.ObjectListRebuildCount - rebuilds;
                    Assert.Equal(selectedId, editor.Session.SelectedObjectId);
                    Assert.Equal("65504", editor.XText);
                    Assert.Same(document, editor.Session.Document);
                    Assert.Equal(0, editor.Session.UndoDepth);
                    Assert.Equal(sha, editor.OpenAssetKey);
                    var shown = Assert.IsAssignableFrom<IReadOnlyList<ObjectTreeRow>>(list.ItemsSource);
                    Assert.Equal(size + 1, shown.Count);
                    Assert.Equal(ObjectTreeRowKind.AssetKindBreadcrumb, shown[0].Kind);
                    Assert.Equal(selectedId, shown[1].Object!.Id);
                    if (measured)
                    {
                        Console.WriteLine(FormattableString.Invariant(
                            $"SELECT count={size} step={step} vmMs={vmMs:F4} panelsMs={panelsMs:F4} totalMs={totalMs:F4} bytes={bytes} enumerated={enumerated} rebuilt={rebuilt} realized={list.GetRealizedContainers().Count()} listHeight={list.Bounds.Height:F0}"));
                        listRebuilds.Add(rebuilt);
                        foreach (var timing in editor.LastEditTimings)
                            Console.WriteLine("SECTION count=" + size + " step=" + step + " name=" + timing.Step
                                + " ms=" + timing.Ms.ToString("F4", CultureInfo.InvariantCulture));
                        if (step > 0)
                            unnecessaryPanels.AddRange(editor.LastEditTimings.Where(t => t.Step.StartsWith("ui:deferred/", StringComparison.Ordinal)).Select(t => t.Step));
                    }
                }
                if (measured) Assert.Empty(unnecessaryPanels);
                if (measured) Assert.Equal(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, listRebuilds);
                editor.LastEditTimings.Clear();
                editor.SelectObject(items[^2].Id);
                UiProbe.Drain(view);
                Layout();
                Assert.Equal(items[^2].Name, Field<TextBlock>(view, "_contextTitle").Text);
                Assert.Contains(editor.LastEditTimings, t => t.Step == "ui:deferred/context");
                editor.DeselectObject();
                UiProbe.Drain(view);
                Layout();
                Assert.True(Field<Control>(view, "_selectionEmpty").IsEffectivelyVisible);
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
            void Layout()
            {
                layoutRoot.Measure(new Size(1536, 864));
                layoutRoot.Arrange(new Rect(0, 0, 1536, 864));
                layoutRoot.UpdateLayout();
            }
        }
    });

    [Theory]
    [InlineData(10_000, false)]
    [InlineData(200_000, false)]
    [InlineData(10_000, true)]
    public void Opening_one_kind_does_not_copy_its_objects(int count, bool uniqueKinds)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var art = File.ReadAllBytes(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(art));
        var style = new StampStyle(sha);
        var objects = Enumerable.Range(0, count).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "mountain " + i, i % 1000 * 64, i / 1000 * 48, 64, 48, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
            // Extra keys are metadata only; the selected kind still uses the owner's PNG.
            { Stamp = uniqueKinds && i < count - 1 ? new StampStyle(i.ToString("x64")) : style }).ToArray();
        var document = new MapDocument(DocumentId.New(), "owner stamp rows", DateTimeOffset.UnixEpoch,
            new(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects)],
            [new MapAssetReference(sha, "owner mountain", "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "owner", "owner", embeddedContent: art)]);
        ObjectTreeRows.PrepareLayerIndexes(document);
        DocumentDerivedIndexes.Prepare(document);
        _ = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: sha);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, [objects[^1].Id], openAssetKey: sha);
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine(FormattableString.Invariant($"OPEN-KIND count={count} kinds={(uniqueKinds ? count : 1)} allocated={allocated} ms={milliseconds:F4}"));
        Assert.Equal(uniqueKinds ? 2 : count + 1, rows.Count);
        Assert.Equal(ObjectTreeRowKind.AssetKindBreadcrumb, rows[0].Kind);
        Assert.Equal(uniqueKinds ? 1 : count, rows[0].ChildCount);
        Assert.Equal(objects[^1], rows[1].Object);
        Assert.True(rows[1].Selected);
        Assert.Equal(uniqueKinds ? objects[^1] : objects[0], rows[^1].Object);
        Assert.InRange(allocated, 0, 32_768);
    }

    private static T Field<T>(Map.App.Views.EditorView view, string name) => (T)typeof(Map.App.Views.EditorView)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

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
