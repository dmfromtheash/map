using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] OwnerArtRequired =
    [
        "owner-textures-offered", "texture-picker-under-new-land", "texture-chosen-before-land",
        "far-view-uses-small-step", "close-view-uses-master", "shots-saved", "placed-from-catalog", "resources-disposed",
    ];

    /// <summary>
    /// The owner's redrawn mountains and grounds (2026-10-18) in the real editor. The library is a
    /// copy of the installed one (MAP_OWNER_ART_LIBRARY); the owner's 1408 px originals
    /// (MAP_OWNER_ART_ORIGINALS) are imported beside them only inside this workspace, placed over
    /// the same ground, so every shot shows the source above and the redrawn picture below at the
    /// same size. The ground is chosen in the land panel before the land exists.
    /// </summary>
    private async Task OwnerArtOnly()
    {
        var library = Environment.GetEnvironmentVariable("MAP_OWNER_ART_LIBRARY")
            ?? throw new InvalidOperationException("MAP_OWNER_ART_LIBRARY is not set");
        var originals = Environment.GetEnvironmentVariable("MAP_OWNER_ART_ORIGINALS")
            ?? throw new InvalidOperationException("MAP_OWNER_ART_ORIGINALS is not set");
        var root = Path.Combine(_workspace, "library");
        CopyDirectory(library, root);
        var catalog = new Map.Assets.AssetCatalog(root);
        // In the order of the owner's files 1.png … 6.png.
        string[] names = ["Ледяной пик", "Двуглавая гора", "Гора с ледником", "Горная гряда", "Ледяные шпили", "Ледяной клык"];
        var redrawn = names.Select(name => catalog.Records.Single(r => r.Pack == "Мои горы" && r.DisplayName == name)).ToArray();
        Require(redrawn.Length == 6, $"expected 6 redrawn mountains, found {redrawn.Length}");
        var sources = Directory.GetFiles(originals, "*.png").Order().ToArray();
        var before = sources.Select(path => catalog.Import(path, new Map.Assets.AssetImportOptions { Pack = "Исходники" }, DateTimeOffset.UnixEpoch)).ToArray();

        const double width = 600, gap = 50, left = 120;
        var objects = new List<SceneObject>();
        var rects = new List<(double X, double Y, double W, double H)>();
        for (var i = 0; i < redrawn.Length; i++)
        {
            var record = redrawn[i];
            var h = width * record.Height / record.Width;
            var x = left + (i * (width + gap)); var y = 1150.0;
            rects.Add((x, y, width, h));
            objects.Add(StampAt(record.DisplayName, record.Sha256, x, y, width, h));
            // The source's opaque box plus the 12 px margin the redrawn file keeps around it.
            var (bx, by, bw, bh) = OpaqueBox(sources[i]);
            var k = width / bw;
            objects.Add(StampAt("Исходник " + (i + 1), before[i].Sha256, x - (bx * k), 250 - (by * k), 1408 * k, 1408 * k));
        }
        var sea = "#FF9DB7C2";
        var document = new MapDocument(DocumentId.New(), "Мои горы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 2048, "px", sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, sea), new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects])]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document, assetCatalog: catalog); await Settle();
        await Click("LandToolButton"); await Settle();

        // The ground is in sight under «Новая земля» and is chosen before any land exists.
        var button = Find<Control>("NewLandButton");
        var picker = Find<Control>("NewLandTexturePicker");
        var buttonY = button.TranslatePoint(new Point(0, 0), _view)!.Value.Y;
        var pickerY = picker.TranslatePoint(new Point(0, 0), _view)!.Value.Y;
        var hint = Find<Control>("LandSizeSlider").TranslatePoint(new Point(0, 0), _view)!.Value.Y;
        // Directly below the button: nothing but the «Текстура земли» caption between them.
        var below = pickerY - (buttonY + button.Bounds.Height);
        _report.Checks["texture-picker-under-new-land"] = picker.IsEffectivelyVisible && below > 0 && below < 64 && pickerY < hint;
        Console.WriteLine($"owner-art: button y {buttonY:0}..{buttonY + button.Bounds.Height:0}, picker y {pickerY:0}, size slider y {hint:0}");
        // Only the owner's own grounds are offered: every other texture was removed on the owner's request.
        var offered = _view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Name?.StartsWith("NewLandTexturePicker_", StringComparison.Ordinal) == true && b.Name != "NewLandTexturePicker_none")
            .Select(b => b.Name).ToArray();
        _report.Checks["owner-textures-offered"] = offered.SequenceEqual(new[]
            { MapFillTextures.OwnerSnow, MapFillTextures.OwnerSnowSparkle, MapFillTextures.OwnerSnowSoft, MapFillTextures.OwnerIceWash }
            .Select(id => "NewLandTexturePicker_" + id));
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".panel.png"));

        await Click("NewLandTexturePicker_none");
        var plainFirst = _editor.NewLandTextureId is null;
        await Click("NewLandTexturePicker_" + MapFillTextures.OwnerSnow);
        var chosenFirst = plainFirst && _editor.NewLandTextureId == MapFillTextures.OwnerSnow && _editor.Session.Document.Layers.OfType<LandLayer>().Count() == 0;
        await Click("NewLandButton");
        var land = (LandLayer)_editor.ActiveLayer!;
        var island = land with { GeometryTiles = IslandTiles(4096, 2048) };
        _editor.Session.Execute(UpdateLayerCommand.Replace(land, island, "Island"));
        await Settle();
        _report.Checks["texture-chosen-before-land"] = chosenFirst && island.EdgeStyle.FillTextureId == MapFillTextures.OwnerSnow
            && island.EdgeStyle.FillTextureOpacity == 1;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".panel-chosen.png"));

        var shots = 0;
        async Task Shot(string name, double worldX, double worldY, double scale)
        {
            var canvas = _view.Canvas.Bounds;
            _editor.Session.Viewport.Set(worldX - (canvas.Width / 2 / scale), worldY - (canvas.Height / 2 / scale), scale);
            await Frame(); await Task.Delay(2500); await Frame(); await Task.Delay(500); await Frame();
            SaveVisual(Path.ChangeExtension(Options.OutputPath, "." + name + ".png")); shots++;
        }

        await Shot("far", 2048, 1024, 0.25);
        var images = _editor.StampImages!;
        var far = images.TryGetImageForPixelSize(redrawn[0].Sha256, width * .25, rects[0].H * .25);
        _report.Checks["far-view-uses-small-step"] = far is { Width: <= 512 };
        for (var i = 0; i < 6; i++)
        {
            var (x, y, w, h) = rects[i];
            await Shot($"m{i + 1}-100", x + (w / 2), (250 + y + h) / 2, 0.55);
        }
        foreach (var i in new[] { 0, 2, 5 })
        {
            var (x, y, w, h) = rects[i];
            foreach (var (zoom, label) in new[] { (4.0, "400"), (16.0, "1600") })
            {
                await Shot($"m{i + 1}-{label}-new", x + (w * .5), y + (h * .32), zoom);
                await Shot($"m{i + 1}-{label}-old", x + (w * .5), 250 + (h * .32), zoom);
            }
        }
        var close = images.TryGetImageForPixelSize(redrawn[0].Sha256, width * 16, rects[0].H * 16);
        _report.Checks["close-view-uses-master"] = close?.Width == redrawn[0].Width;

        // The ground itself: far, ordinary and close.
        await Shot("snow-100", 2048, 1900, 1);
        await Shot("snow-800", 2048, 1900, 8);
        await Shot("snow-far", 2048, 1024, 0.25);
        foreach (var id in new[] { MapFillTextures.OwnerSnowSparkle, MapFillTextures.OwnerSnowSoft, MapFillTextures.OwnerIceWash })
        {
            await Click("NewLandTexturePicker_" + id); await Settle();
            await Shot(id + "-100", 2048, 1900, 1);
            await Shot(id + "-800", 2048, 1900, 8);
            await Shot(id + "-far", 2048, 1024, 0.25);
        }
        _report.Checks["shots-saved"] = shots == 1 + 6 + 12 + 3 + 9;

        // The owner's way in: the asset catalog shows «Мои горы», one is chosen and placed.
        _editor.SelectLayer(document.Layers.OfType<ObjectLayer>().Single().Id);
        _editor.ActiveTool = EditorTool.Stamp; _view.OpenAssetCatalog(); await Settle();
        await Task.Delay(2500); await Settle();
        var gallery = _view.AssetCatalogWindowForSmoke!;
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)gallery.Bounds.Width, (int)gallery.Bounds.Height)))
        {
            bitmap.Render(gallery);
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, ".catalog.png"));
            bitmap.Save(file);
        }
        var peak = catalog.Sets.Single(set => set.Name == "Ледяной пик");
        var chosen = _editor.ChooseGalleryVariant(peak.Id, null);
        gallery.Close(); await Settle();
        _editor.Session.Viewport.Set(1500, 1500, 0.5); await Frame();
        var count = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        Point(2000, 1900); await Frame(); await Task.Delay(1500); await Frame();
        var placed = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        _report.Checks["placed-from-catalog"] = chosen && placed.Length == count + 1 && placed[^1].Stamp?.AssetSha256 == redrawn[0].Sha256;
        _editor.HandleEscape(); await Settle(); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".placed.png"));
    }

    private static SceneObject StampAt(string name, string sha, double x, double y, double w, double h) =>
        new(ObjectId.New(), SceneObjectKind.Stamp, name, x, y, w, h, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(sha) };

    private static (double X, double Y, double W, double H) OpaqueBox(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        int x0 = bitmap.Width, y0 = bitmap.Height, x1 = 0, y1 = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 8) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        const int margin = 12;
        x0 = Math.Max(0, x0 - margin); y0 = Math.Max(0, y0 - margin);
        x1 = Math.Min(bitmap.Width - 1, x1 + margin); y1 = Math.Min(bitmap.Height - 1, y1 + margin);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Land over most of the map with a wavy coast, leaving a strip of sea at the edges.</summary>
    private static TileMap IslandTiles(int width, int height)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < height / TileMap.TileSize; ty++)
            for (var tx = 0; tx < width / TileMap.TileSize; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = (tx * TileMap.TileSize) + x, wy = (ty * TileMap.TileSize) + y;
                        var edge = Math.Min(Math.Min(wx, width - wx), Math.Min(wy, height - wy));
                        var shore = 90 + (30 * Math.Sin(wx / 97)) + (20 * Math.Sin(wy / 61));
                        bytes[(y * TileMap.TileSize) + x] = (byte)Math.Clamp((edge - shore) * 32, 0, 255);
                    }
                tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return tiles;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.OrdinalIgnoreCase), true);
    }
}
