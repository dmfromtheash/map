using System.Runtime.CompilerServices;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The asset catalog lives for the whole session; an editor only while its map is open. Live 2026-11-15 (PC and
/// USER): every opened map stayed in memory with its view, canvas and image caches (+~60 MiB per open, 4 -> 24
/// editors in a heap snapshot) because the catalog held each editor through a strong event subscription.
/// </summary>
public sealed class EditorLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-editor-lifetime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_closed_editor_is_not_kept_alive_by_the_session_asset_catalog()
    {
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var editors = OpenAndDrop(catalog, count: 3);
        // Background work an editor starts on open may hold it for a moment; it must let go within seconds.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!editors.Any(e => e.TryGetTarget(out _))) break;
            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);
        Assert.All(editors, editor => Assert.False(editor.TryGetTarget(out _), "A dropped editor is still reachable."));
        GC.KeepAlive(catalog);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<EditorViewModel>> OpenAndDrop(AssetCatalog catalog, int count)
    {
        var result = new List<WeakReference<EditorViewModel>>();
        for (var i = 0; i < count; i++)
        {
            var document = new MapDocument(DocumentId.New(), "lifetime", DateTimeOffset.UnixEpoch, new CanvasDefinition(800, 600, "px", "#FF20242B"),
                [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
            var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
                new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
            result.Add(new WeakReference<EditorViewModel>(editor));
        }
        return result;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
