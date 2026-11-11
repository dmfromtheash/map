using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The explanation cards of the asset panel (owner, 2026-11-11, variant 1): every card is made of real stamps of the
/// chosen asset with the panel's settings, and making it never touches the map or its history.
/// </summary>
public sealed class PanelDemoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-demo-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Fact]
    public void Every_card_is_made_of_the_chosen_asset_and_leaves_the_map_alone()
    {
        var editor = Editor(withAsset: true);
        editor.SetStampScalePercent(130, true);
        var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
        var sha = editor.CurrentVariant!.Sha256;
        foreach (var kind in Enum.GetValues<PanelDemoKind>())
        {
            for (var frame = 0; frame < 10; frame++)
            {
                var demo = editor.PanelDemo(kind, frame, 10);
                Assert.NotNull(demo);
                var stamps = demo!.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
                Assert.All(stamps, s => Assert.Equal(sha, s.Stamp!.AssetSha256));
                Assert.True(demo.View.Right > demo.View.Left && demo.View.Bottom > demo.View.Top);
                if (frame == 9 && kind != PanelDemoKind.DeletePath) Assert.NotEmpty(stamps);
            }
        }
        Assert.Same(before, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);
    }

    [Fact]
    public void The_reset_card_ends_at_the_base_size_and_the_scale_card_follows_the_setting()
    {
        var editor = Editor(withAsset: true);
        double Width(PanelDemoKind kind, int frame) =>
            editor.PanelDemo(kind, frame, 10)!.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single().Width;
        var plain = Width(PanelDemoKind.Reset, 9);
        editor.SetStampScalePercent(150, true);
        Assert.Equal(plain * 1.5, Width(PanelDemoKind.Reset, 0), 3);
        Assert.Equal(plain, Width(PanelDemoKind.Reset, 9), 3);
        // The scale card swings around the panel's own value.
        Assert.Equal(plain * 1.5 * .6, Width(PanelDemoKind.Scale, 0), 3);
    }

    [Fact]
    public void Without_a_chosen_asset_there_is_no_card()
    {
        var editor = Editor(withAsset: false);
        Assert.Null(editor.PanelDemo(PanelDemoKind.Scale, 0, 10));
    }

    private EditorViewModel Editor(bool withAsset)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "own.png");
        using (var surface = SKSurface.Create(new SKImageInfo(32, 24))!)
        {
            surface.Canvas.Clear(SKColors.SaddleBrown);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        var catalog = new AssetCatalog(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([path], "Гора", new(), DateTimeOffset.UnixEpoch);
        var document = new MapDocument(DocumentId.New(), "Demo", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog);
        if (withAsset) editor.SelectAssetSet(catalog.Sets[0].Id);
        return editor;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
