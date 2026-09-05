using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class ReadinessPreviewTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("map-readiness-preview-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public async Task Saved_recent_recovery_and_version_previews_use_the_same_real_resources()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "map.mapproj");
        var document = Document(temp.Path);
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var editor = Editor(document, path, store, recent);
        editor.SelectObject(document.Layers.OfType<ObjectLayer>().Single().Objects[0].Id);
        Assert.True(await editor.SaveAsync());
        var saved = store.Saved[path];
        Assert.Same(saved.Preview, recent.Items.Single().ThumbnailPng);
        CheckPixels(saved.Preview);
        Assert.Same(document, saved.Document);
        MapProjectStore.Save(document, path, saved.Metadata, saved.Preview);
        var reopened = MapProjectStore.Open(path);
        Assert.Equal(saved.Preview, reopened.PreviewPng);
        using (var images = new ExportStampImageSource(reopened.Document, path, null))
            Assert.Equal(saved.Preview, ThumbnailRenderer.RenderPng(reopened.Document, stamps: images));

        editor.SetSelectedOpacityPercent(90);
        Assert.True(await editor.TryAutosaveAsync());
        Assert.NotEqual(saved.Preview, store.Autosaves.Single().Preview);
        Assert.True(await editor.CreateVersionAsync());
        var checkpoint = editor.Session.Document.Checkpoints.Single();
        using (var images = new ExportStampImageSource(checkpoint.Snapshot.Materialize(document.Id), path, null))
            Assert.Equal(checkpoint.PreviewPng, ThumbnailRenderer.RenderPng(
                checkpoint.Snapshot.Materialize(document.Id), MapCheckpointLimits.PreviewWidth,
                MapCheckpointLimits.PreviewHeight, images));

        File.Delete(Path.Combine(temp.Path, "linked.png"));
        Assert.True(await editor.SaveAsync());
        Assert.Equal(SaveState.Saved, editor.SaveState);
        Assert.True(store.Saved[path].Preview.Length > 100);
    }

    [Fact]
    public async Task Durable_save_survives_a_recent_list_failure_and_later_edits_stay_dirty()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "map.mapproj");
        var document = Document(temp.Path);
        var store = new FakeProjectStore();
        var editor = Editor(document, path, store, new BrokenRecent());
        Assert.True(await editor.SaveAsync());
        Assert.Equal(SaveState.Saved, editor.SaveState);
        Assert.Contains("сохранена", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);

        store.ManualSaveEntered = new ManualResetEventSlim();
        store.ManualSaveRelease = new ManualResetEventSlim();
        var saving = editor.SaveAsync();
        Assert.True(await Task.Run(() => store.ManualSaveEntered.Wait(TimeSpan.FromSeconds(10))));
        editor.AddObject(SceneObjectKind.Rectangle);
        var current = editor.Session.Document;
        store.ManualSaveRelease.Set();
        Assert.True(await saving);
        Assert.Same(document, store.Saved[path].Document);
        Assert.Same(current, editor.Session.Document);
        Assert.True(editor.Session.IsDirty);
        store.ManualSaveEntered.Dispose(); store.ManualSaveRelease.Dispose();
    }

    private static EditorViewModel Editor(MapDocument document, string path, FakeProjectStore store, IRecentMaps recent) =>
        new(document, path, true, store, recent, new FakeFilePicker(), new FakePrompts(), new FakeLog());

    private static MapDocument Document(string folder)
    {
        byte[] Image(SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(16, 16));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        var green = Image(SKColors.Lime); var red = Image(new SKColor(255, 0, 0, 128));
        File.WriteAllBytes(Path.Combine(folder, "linked.png"), red);
        var greenSha = Convert.ToHexStringLower(SHA256.HashData(green));
        var redSha = Convert.ToHexStringLower(SHA256.HashData(red));
        SceneObject Stamp(string sha, int x, double opacity) => new(ObjectId.New(), SceneObjectKind.Stamp,
            "цветной штамп", x, 20, 80, 80, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha), ObjectOpacity = opacity };
        return new MapDocument(DocumentId.New(), "Проверка миниатюр", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(320, 180, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Штампы", true, false, 1, [Stamp(greenSha, 20, 1), Stamp(redSha, 60, .5)])],
            [new MapAssetReference(greenSha, "embedded", "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "test", "test", embeddedContent: green),
             new MapAssetReference(redSha, "linked", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "test", "test", relativePath: "linked.png")]);
    }

    private static void CheckPixels(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(40, 50));
        var overlap = bitmap.GetPixel(80, 50);
        Assert.InRange(overlap.Red, 63, 65); Assert.InRange(overlap.Green, 190, 192);
        Assert.Equal(0, overlap.Blue); Assert.Equal(255, overlap.Alpha);
    }

    private sealed class BrokenRecent : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];
        public void Remove(string path) { }
        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта") => throw new IOException("recent locked");
    }
}
