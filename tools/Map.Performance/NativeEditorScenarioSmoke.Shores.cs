using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] ShoreRequired =
    [
        "new-map-look", "same-level-carves-earth", "higher-water-carves-earth",
        "lower-water-is-covered", "width-rebuilds-banks", "width-undo-exact",
        "shore-map-saved-and-reopened", "native-shore-frame", "resources-disposed",
    ];

    private async Task ShoreOnly()
    {
        _view.Canvas.DisposeRenderCaches();
        OpenEditor(ShoreScene());
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        var river = _editor.Rivers.Single();
        _editor.SelectRiver(river.Id);
        await Click("RiverToolButton");
        var original = _editor.Session.Document;
        _report.Checks["new-map-look"] = river.Style == RiverStyle.Natural() && river.Style.CutsLand
            && original.Layers.OfType<LandLayer>().All(l => l.EdgeStyle == LandEdgeStyle.SoftWatercolor())
            && original.Layers[0] is BaseColorLayer { Sea: not null };

        // Hide the paint of the water to prove the earth really is removed at the right levels: inside the lake the
        // map is then the sea under no land at all, or - water below the earth - the land with no river.
        _editor.SetRiverOpacityPercent(0);
        var noLand = _editor.Session.Document with
        { Layers = [.. _editor.Session.Document.Layers.Select(l => l is LandLayer land ? land with { Visible = false } : l)] };
        bool Same(SKBitmap map, SKBitmap reference)
        {
            var a = map.GetPixel(645, 180); var b = reference.GetPixel(645, 180);
            return Math.Abs(a.Red - b.Red) <= 2 && Math.Abs(a.Green - b.Green) <= 2 && Math.Abs(a.Blue - b.Blue) <= 2;
        }
        using (var equal = ShoreBitmap())
        using (var sea = ShoreBitmap(document: noLand))
            _report.Checks["same-level-carves-earth"] = Same(equal, sea);
        _editor.SetSelectedWaterLevel(2);
        using (var above = ShoreBitmap())
        using (var sea = ShoreBitmap(document: noLand))
            _report.Checks["higher-water-carves-earth"] = Same(above, sea);
        _editor.SetSelectedWaterLevel(-1);
        var noRiver = _editor.Session.Document with
        { Layers = [.. _editor.Session.Document.Layers.Select(l => l is ObjectLayer o ? o with { Rivers = [] } : l)] };
        using (var below = ShoreBitmap())
        using (var land = ShoreBitmap(document: noRiver))
            _report.Checks["lower-water-is-covered"] = Same(below, land);
        while (_editor.Session.UndoDepth > 0) _editor.Undo();
        _editor.SelectRiver(river.Id);
        using (var normal = ShoreBitmap())
        {
            _editor.SetRiverWidthScalePercent(145);
            using var wide = ShoreBitmap();
            _report.Checks["width-rebuilds-banks"] = normal.Pixels.Zip(wide.Pixels).Count(p => p.First != p.Second) > 3000;
        }
        _editor.Undo();
        _report.Checks["width-undo-exact"] = ReferenceEquals(original, _editor.Session.Document);
        _editor.HandleEscape(); await Frame();
        _report.Checks["native-shore-frame"] = ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".shore-editor.png");
        SaveVisual(_report.Screenshot);
        using (var map = ShoreBitmap()) SaveShore(map, "shore-map");
        using (var mouths = ShoreBitmap(new WorldBounds(300, 530, 805, 768), 2)) SaveShore(mouths, "shore-mouths");
        await Click("SaveButton");
        await Until(() => File.Exists(Path.Combine(_workspace, "river.mapproj")) && !_editor.Session.IsDirty);
        _report.SavedProject = Path.Combine(_workspace, "river.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var restored = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        _report.Checks["shore-map-saved-and-reopened"] = restored.Style == river.Style
            && restored.Channels.Length == river.Channels.Length
            && restored.Channels.Select(c => c.Source).SequenceEqual(river.Channels.Select(c => c.Source))
            && restored.Channels.Select(c => c.Mouth).SequenceEqual(river.Channels.Select(c => c.Mouth));
    }

    private SKBitmap ShoreBitmap(WorldBounds? region = null, double scale = 1, MapDocument? document = null)
    {
        var view = region ?? new WorldBounds(0, 0, 1024, 768);
        var info = new SKImageInfo((int)((view.Right - view.Left) * scale), (int)((view.Bottom - view.Top) * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document ?? _editor.Session.Document,
            new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static void SaveShore(SKBitmap bitmap, string suffix)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.ChangeExtension(Options.OutputPath, suffix + ".png"));
        data.SaveTo(file);
    }

    private static MapDocument ShoreScene()
    {
        var land = TileMap.Empty(1);
        for (var ty = 0; ty < 3; ty++) for (var tx = 0; tx < 4; tx++)
        {
            var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++) for (var x = 0; x < TileMap.TileSize; x++)
            {
                var wx = tx * TileMap.TileSize + x; var wy = ty * TileMap.TileSize + y;
                var coast = 605 + 34 * Math.Sin(wx * .010) + 14 * Math.Sin(wx * .029)
                    + 145 * Math.Exp(-Math.Pow((wx - 495) / 190.0, 2));
                coverage[y * TileMap.TileSize + x] = (byte)Math.Clamp((coast - wy + .5) * 255, 0, 255);
            }
            land = land.With(new TileKey(tx, ty), coverage);
        }
        var earth = new LandLayer(LayerId.New(), "Земля · уровень 0", true, false, 1, land, CurrentLook.Land());
        var lake = RiverEditor.NewLake([new(596, 96), new(707, 117), new(738, 186), new(690, 251), new(587, 250), new(540, 179)])
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae01"), Name = "Верхнее озеро" };
        var main = RiverEditor.NewChannel([new(550, 267), new(493, 330), new(552, 399), new(481, 488), new(526, 604), new(439, 756)], 26)
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae02"), Name = "Большая река",
                Source = new RiverJunction(lake.Id, lake.Nodes[4].Id, .16) };
        main = main with { Nodes = [.. main.Nodes.Select((n, i) => n with { Width = 15 + i * 7 })] };
        var tributary = RiverEditor.NewChannel([new(117, 95), new(194, 153), new(165, 222), new(287, 252), new(249, 328), new(399, 353)], 11)
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae03"), Name = "Извилистый приток",
                Mouth = new RiverJunction(main.Id, main.Nodes[1].Id, .5) };
        var right = RiverEditor.NewChannel([new(869, 331), new(826, 399), new(897, 449), new(810, 499), new(662, 446)], 10)
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae04"), Name = "Правый приток",
                Mouth = new RiverJunction(main.Id, main.Nodes[2].Id, .55) };
        var branch = RiverEditor.NewChannel([new(460, 451), new(365, 522), new(354, 609), new(397, 680)], 19)
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae05"), Name = "Западный рукав",
                Source = new RiverJunction(main.Id, main.Nodes[2].Id, .5),
                Mouth = new RiverJunction(main.Id, main.Nodes[4].Id, .55) };
        var delta = RiverEditor.NewChannel([new(561, 533), new(636, 597), new(622, 676), new(724, 751)], 19)
            with { Id = new Guid("83fa519d-f65b-4fca-8f69-32c57a9cae06"), Name = "Восточное устье",
                Source = new RiverJunction(main.Id, main.Nodes[3].Id, .45) };
        var river = new River { Name = "Озеро, река и дельта", Style = CurrentLook.River(),
            Channels = [lake, main, tributary, right, branch, delta] };
        river.Validate();
        return new MapDocument(DocumentId.New(), "Живые реки", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF74A7A1"),
            [CurrentLook.Page("Море", "#FF74A7A1"), earth,
                new ObjectLayer(LayerId.New(), "Реки и озеро", true, false, 1, []) { Rivers = [river] }]);
    }
}
