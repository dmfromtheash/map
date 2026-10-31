using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>What a create button's live picture shows being made.</summary>
public enum CreateDemoKind { River, Land, Road, Lake }

/// <summary>
/// The live pictures of the create buttons (owner, 2026-10-31: «должны показывать ровно как у нас это делается на карте
/// с нашими асетами… 1 в 1 всё как в настоящем использовании»). A frame is a small map made the way the editor makes the
/// real thing - the same calls, the river «Живая» at the width of new nodes, the road of new roads, new land with the
/// chosen ground texture - on this map's own page, sea, style and light. The view renders the frames with the program's
/// renderer; nothing here touches the map itself.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The demo map's size in map units: the strip of a create button at twice its 300×64 DIP.</summary>
    public const int CreateDemoWidth = 600, CreateDemoHeight = 128;

    /// <summary>Changes when anything a demo is drawn with changes, so the frames are drawn again only then.</summary>
    public string CreateDemoSignature
    {
        get
        {
            var document = Session.Document;
            var page = document.Layers.OfType<BaseColorLayer>().FirstOrDefault();
            return string.Join('|', page?.Color, page?.Sea?.GetHashCode(), document.Style?.GetHashCode(), document.Lighting?.GetHashCode(),
                NewLandTextureId, NewRiverWidth, NewRoadWidth, Services.RegionalMapFactory.IsRegional(document));
        }
    }

    /// <summary>Frame <paramref name="frame"/> of <paramref name="frames"/>: the thing grown that far.</summary>
    public MapDocument CreateDemoFrame(CreateDemoKind kind, int frame, int frames)
    {
        var t = frames <= 1 ? 1 : Math.Clamp(frame / (double)(frames - 1), 0, 1);
        var document = Session.Document;
        var page = document.Layers.OfType<BaseColorLayer>().FirstOrDefault() is { } own
            ? own with { Id = LayerId.New() }
            : new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, document.Canvas.Background);
        var groundStyle = WithGroundTexture(LandLayer.CreateEmpty(LayerId.New(), "Земля").EdgeStyle, NewLandTextureId);
        var layers = new List<MapLayer> { page };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        switch (kind)
        {
            case CreateDemoKind.Land:
                // An island growing out of the sea, as a land stroke grows under the brush.
                layers.Add(Land(groundStyle, (x, y) =>
                {
                    double dx = (x - 300) / (250 * t + 1), dy = (y - 66) / (50 * t + 1);
                    var wobble = 1 + .12 * Math.Sin(x / 37.0) + .08 * Math.Cos(y / 23.0);
                    return dx * dx + dy * dy < wobble * wobble && t > .05;
                }));
                break;
            case CreateDemoKind.River:
            {
                layers.Add(Land(groundStyle, static (_, _) => true));
                // Node by node from the source to the mouth, as the clicks place them.
                VectorPoint[] path = [new(20, 40), new(130, 26), new(220, 70), new(330, 92), new(440, 58), new(580, 84)];
                var shown = path.Take(Math.Max(2, (int)Math.Round(2 + (path.Length - 2) * t))).ToArray();
                var channel = RiverEditor.NewChannel(shown, NewRiverWidth);
                objects = objects with { Rivers = [new River { Name = "Река", Channels = [channel], Style = RiverStyle.Natural() }] };
                break;
            }
            case CreateDemoKind.Lake:
            {
                layers.Add(Land(groundStyle, static (_, _) => true));
                // The shore clicked round and closed: the lake opens to its full size.
                var r = .3 + .7 * t;
                VectorPoint[] shore = [.. Enumerable.Range(0, 12).Select(i =>
                {
                    var a = i * Math.Tau / 12;
                    var k = 1 + .15 * Math.Sin(3 * a);
                    return new VectorPoint(300 + Math.Cos(a) * 170 * k * r, 64 + Math.Sin(a) * 44 * k * r);
                })];
                objects = objects with { Rivers = [new River { Name = "Озеро", Channels = [RiverEditor.NewLake(shore)], Style = RiverStyle.Natural() }] };
                break;
            }
            case CreateDemoKind.Road:
            {
                layers.Add(Land(groundStyle, static (_, _) => true));
                VectorPoint[] path = [new(20, 100), new(150, 64), new(270, 78), new(400, 36), new(580, 26)];
                var shown = path.Take(Math.Max(2, (int)Math.Round(2 + (path.Length - 2) * t))).ToArray();
                var style = Services.RegionalMapFactory.IsRegional(document) ? Services.RegionalMapFactory.RoadStyle : RoadStyle.Painted();
                objects = objects with { Roads = [RoadEditor.NewRoad(shown, NewRoadWidth, style, "Дорога", seed: 7)] };
                break;
            }
        }
        layers.Add(objects);
        return new MapDocument(DocumentId.New(), "Пример", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(CreateDemoWidth, CreateDemoHeight, "px", document.Canvas.Background), layers)
        { Style = document.Style, Lighting = document.Lighting };
    }

    private static LandLayer Land(LandEdgeStyle style, Func<int, int, bool> inside)
    {
        var tiles = TileMap.Empty(1);
        for (var tx = 0; tx * TileMap.TileSize < CreateDemoWidth; tx++)
            for (var ty = 0; ty * TileMap.TileSize < CreateDemoHeight; ty++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                        if (inside(tx * TileMap.TileSize + x, ty * TileMap.TileSize + y)) { bytes[y * TileMap.TileSize + x] = 255; any = true; }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, style);
    }
}
