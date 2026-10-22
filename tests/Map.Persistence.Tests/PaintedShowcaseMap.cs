using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// MAP_SHOWCASE_MAP=&lt;file.mapproj&gt; writes a map that shows the owner's painted manner in one
/// view - a raised island with «Как горы» cliffs and the ink coast, a painted river and road with
/// a bridge, and the «Парадный» decorations - for a live look in the real window.
/// </summary>
public sealed class PaintedShowcaseMap
{
    [Fact]
    public void Write_the_showcase_map()
    {
        var path = Environment.GetEnvironmentVariable("MAP_SHOWCASE_MAP");
        if (string.IsNullOrWhiteSpace(path)) return;
        const int width = 1600, height = 1000;
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < height; ty++)
            for (var tx = 0; tx * TileMap.TileSize < width; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var value = Island(tx * TileMap.TileSize + x, ty * TileMap.TileSize + y);
                        bytes[y * TileMap.TileSize + x] = value;
                        any |= value > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        var land = new LandLayer(LayerId.New(), "Остров", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = 1 };
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Natural(),
            Channels = [RiverEditor.NewChannel([new(560, 300), new(700, 420), new(760, 560), new(900, 700), new(1000, 820)], 26)],
        };
        var road = RoadEditor.NewRoad([new(420, 640), new(620, 600), new(820, 620), new(1040, 560), new(1220, 470)], 18, RoadStyle.Painted(), "Тракт", seed: 5);
        SceneObject Decoration(DecorationStyle style, int x, int y, int w, int h) =>
            new(ObjectId.New(), SceneObjectKind.Decoration, DecorationStyle.DefaultName(style.Kind), x, y, w, h, 0,
                "#00000000", "#00000000", 0, true, false) { Decoration = style };
        var decorations = new ObjectLayer(LayerId.New(), "Оформление", true, false, 1,
        [
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Cartouche, "Северные земли") with { Subtitle = "карта королевства" }, 520, 40, 560, 170),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Compass), 1320, 60, 230, 230),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Legend), 40, 680, 260, 250),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.ScaleBar), 1150, 900, 400, 60),
        ]);
        var water = new ObjectLayer(LayerId.New(), "Реки и дороги", true, false, 1, []) { Rivers = [river with { Level = 1 }], Roads = [road with { Level = 1 }] };
        var document = new MapDocument(DocumentId.New(), "Живописный стиль", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", "#FFB9CBD2") { Scale = new MapScale(0.25, "км") },
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FFB9CBD2"), land, water, decorations]);
        document = MapDecorations.Refresh(document);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(File.Exists(path));
    }

    private static byte Island(int x, int y)
    {
        const double cx = 800, cy = 560, rx = 560, ry = 330;
        var angle = Math.Atan2(y - cy, x - cx);
        var wobble = 1 + 0.09 * Math.Sin(angle * 5 + 1) + 0.05 * Math.Sin(angle * 11 + 2.3) + 0.03 * Math.Sin(angle * 23);
        var distance = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2)) / wobble;
        return (byte)Math.Clamp((1 - distance) * 255 * 40, 0, 255);
    }
}
