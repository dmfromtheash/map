using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;

namespace Map.Performance;

internal sealed record Stage13Fixture(
    MapDocument Document,
    ObjectLayer ObjectLayer,
    FixtureManifest Manifest,
    SceneObject PointTarget,
    WorldBounds Marquee);

internal static class Stage13Fixtures
{
    private const int CanvasSize = 65_536;
    private const int Columns = 256;
    private const double Spacing = 240;
    private const double ObjectSize = 32;

    public static Stage13Fixture Simple(int count, int seed)
    {
        var objects = new SceneObject[count];
        var groupCount = Math.Min(count / 10, 5_000);
        var groupedObjectCount = groupCount * 5;

        for (var index = 0; index < count; index++)
        {
            var x = (index % Columns) * Spacing + 24;
            var y = (index / Columns) * Spacing + 24;
            Guid? groupId = null;
            string? groupName = null;
            if (index < groupedObjectCount)
            {
                var groupIndex = index / 5;
                groupId = DeterministicGuid(seed, "group", groupIndex);
                groupName = $"group-{groupIndex:D5}";
            }

            objects[index] = new SceneObject(
                new ObjectId(DeterministicGuid(seed, "object", index)),
                SceneObjectKind.Rectangle,
                $"object-{index:D6}",
                x,
                y,
                ObjectSize,
                ObjectSize,
                index % 17 == 0 ? 12 : 0,
                "#FF557799",
                "#FF112233",
                1,
                visible: true,
                locked: false)
            {
                GroupId = groupId,
                GroupName = groupName,
                Sublayer = index % 19 == 0 ? (index % 5) - 2 : 0,
            };
        }

        var layer = new ObjectLayer(
            new LayerId(DeterministicGuid(seed, "layer", 1)),
            "objects",
            visible: true,
            locked: false,
            opacity: 1,
            objects);
        var document = new MapDocument(
            new DocumentId(DeterministicGuid(seed, "document", count)),
            $"simple-{count}",
            DateTimeOffset.UnixEpoch,
            new CanvasDefinition(CanvasSize, CanvasSize, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(
                    new LayerId(DeterministicGuid(seed, "layer", 0)),
                    "background",
                    visible: true,
                    locked: false,
                    opacity: 1,
                    "#FFFFFFFF"),
                layer,
            ]);

        var manifestWithoutHash = new
        {
            name = $"simple-{count / 1_000}k",
            seed,
            objectCount = count,
            rectangleCount = count,
            groupCount,
            groupedObjectCount,
            canvasWidth = CanvasSize,
            canvasHeight = CanvasSize,
            columns = Columns,
            spacing = Spacing,
            objectSize = ObjectSize,
        };
        var fixtureHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestWithoutHash)))).ToLowerInvariant();
        var manifest = new FixtureManifest(
            manifestWithoutHash.name,
            seed,
            count,
            count,
            groupCount,
            groupedObjectCount,
            CanvasSize,
            CanvasSize,
            fixtureHash);

        return new Stage13Fixture(
            document,
            layer,
            manifest,
            objects[^1],
            new WorldBounds(0, 0, 1_100, 1_100));
    }

    public static Stage13Fixture Mixed50K(int seed)
    {
        const int count = 50_000;
        var objects = new SceneObject[count];
        var kindCounts = new Dictionary<string, int>
        {
            ["rectangle"] = 20_000,
            ["ellipse"] = 10_000,
            ["stamp"] = 8_000,
            ["text"] = 6_000,
            ["line"] = 4_000,
            ["shape"] = 1_000,
            ["composition"] = 1_000,
        };
        const int groupCount = 2_500;
        const int groupedObjectCount = 10_000;
        const string MissingAssetHash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        for (var index = 0; index < count; index++)
        {
            var x = (index % Columns) * Spacing + 24;
            var y = (index / Columns) * Spacing + 24;
            var kind = index switch
            {
                < 20_000 => SceneObjectKind.Rectangle,
                < 30_000 => SceneObjectKind.Ellipse,
                < 38_000 => SceneObjectKind.Stamp,
                < 44_000 => SceneObjectKind.Text,
                < 48_000 => SceneObjectKind.Line,
                < 49_000 => SceneObjectKind.Shape,
                _ => SceneObjectKind.Composition,
            };
            var width = kind is SceneObjectKind.Text or SceneObjectKind.Line ? 96 : 40;
            var height = kind == SceneObjectKind.Line ? 48 : 40;
            var sceneObject = new SceneObject(
                new ObjectId(DeterministicGuid(seed, "mixed-object", index)),
                kind,
                $"mixed-{kind.ToString().ToLowerInvariant()}-{index:D6}",
                x, y, width, height,
                index % 23 == 0 ? 15 : 0,
                index % 2 == 0 ? "#FF557799" : "#FF8A6D4B",
                "#FF112233",
                1,
                visible: true,
                locked: false)
            {
                Sublayer = index % 13 == 0 ? (index % 5) - 2 : 0,
                GroupId = index < groupedObjectCount
                    ? DeterministicGuid(seed, "mixed-group", index / 4)
                    : null,
                GroupName = index < groupedObjectCount ? $"mixed-group-{index / 4:D4}" : null,
            };

            objects[index] = kind switch
            {
                SceneObjectKind.Stamp => sceneObject with
                {
                    Stamp = new StampStyle(MissingAssetHash)
                    {
                        Blur = index % 5 == 0 ? 2 : 0,
                        ShadowOpacity = index % 3 == 0 ? 0.35 : 0,
                    },
                },
                SceneObjectKind.Text => sceneObject with
                {
                    Text = new TextStyle
                    {
                        Content = $"T{index % 100}",
                        FontSize = 24,
                        FillColor = "#FF24364A",
                        OutlineWidth = index % 4 == 0 ? 1 : 0,
                        ShadowOpacity = index % 3 == 0 ? 0.3 : 0,
                    },
                },
                SceneObjectKind.Line => sceneObject with
                {
                    Line = new LineStyle(
                    [
                        new VectorNode(
                            new VectorNodeId(DeterministicGuid(seed, "mixed-line-node-a", index)),
                            0, 0.75, VectorNodeKind.Curve, controlOutX: 0.3, controlOutY: -0.5),
                        new VectorNode(
                            new VectorNodeId(DeterministicGuid(seed, "mixed-line-node-b", index)),
                            1, 0.75, VectorNodeKind.Curve, controlInX: -0.3, controlInY: -0.5),
                    ],
                    new VectorStrokeStyle
                    {
                        Width = 4,
                        ShadowOpacity = index % 3 == 0 ? 0.25 : 0,
                    }),
                },
                SceneObjectKind.Shape => sceneObject with
                {
                    Shape = new ShapeStyle(
                    [
                        new ShapeRegion(
                            new ShapeRegionId(DeterministicGuid(seed, "mixed-region", index)),
                            ShapeCombineMode.Add,
                            ShapePrimitiveKind.Polygon,
                            [
                                new VectorNode(new VectorNodeId(DeterministicGuid(seed, "mixed-shape-a", index)), 0.5, 0),
                                new VectorNode(new VectorNodeId(DeterministicGuid(seed, "mixed-shape-b", index)), 1, 1),
                                new VectorNode(new VectorNodeId(DeterministicGuid(seed, "mixed-shape-c", index)), 0, 1),
                            ]),
                    ],
                    new ShapeFillStyle { Color = "#AA507A5B" },
                    new VectorStrokeStyle { Width = 2, Blur = index % 4 == 0 ? 1 : 0 }),
                },
                SceneObjectKind.Composition => sceneObject with
                {
                    Composition = new EditableComposition(
                        new CompositionId(DeterministicGuid(seed, "mixed-composition", index)),
                        new DocumentId(DeterministicGuid(seed, "mixed-source", index)),
                        $"composition-{index:D6}",
                        40, 40,
                        [
                            new SceneObject(
                                new ObjectId(DeterministicGuid(seed, "mixed-child", index)),
                                SceneObjectKind.Rectangle,
                                "child",
                                4, 4, 32, 32, 0,
                                "#FF79638E", "#FF2A2130", 1, true, false),
                        ]),
                },
                _ => sceneObject,
            };
        }

        var maskPixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var mask = new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), maskPixels), enabled: true);
        var layer = new ObjectLayer(
            new LayerId(DeterministicGuid(seed, "mixed-layer", 1)),
            "mixed-objects", true, false, 1, objects, mask);
        var document = new MapDocument(
            new DocumentId(DeterministicGuid(seed, "mixed-document", count)),
            "mixed-50k",
            DateTimeOffset.UnixEpoch,
            new CanvasDefinition(CanvasSize, CanvasSize, "px", "#FFEEF1F5"),
            [
                new BaseColorLayer(
                    new LayerId(DeterministicGuid(seed, "mixed-layer", 0)),
                    "background", true, false, 1, "#FFEEF1F5"),
                layer,
                new FilterLayer(
                    new LayerId(DeterministicGuid(seed, "mixed-layer", 2)),
                    "mixed-filter", true, false, 1,
                    new FilterSettings { Saturation = 0.08, Temperature = 0.05 }),
            ]);
        var expensiveEffects = objects.Count(item =>
            item.Stamp is { Blur: > 0 } or { ShadowOpacity: > 0 }
            || item.Text is { OutlineWidth: > 0 } or { ShadowOpacity: > 0 }
            || item.Line?.Stroke is { ShadowOpacity: > 0 }
            || item.Shape?.Stroke is { Blur: > 0 });
        var manifestWithoutHash = new
        {
            name = "mixed-50k",
            seed,
            objectCount = count,
            groupCount,
            groupedObjectCount,
            canvasWidth = CanvasSize,
            canvasHeight = CanvasSize,
            kindCounts,
            expensiveEffects,
            materializedMaskTileCount = 1,
        };
        var fixtureHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestWithoutHash)))).ToLowerInvariant();
        var manifest = new FixtureManifest(
            manifestWithoutHash.name,
            seed,
            count,
            kindCounts["rectangle"],
            groupCount,
            groupedObjectCount,
            CanvasSize,
            CanvasSize,
            fixtureHash)
        {
            KindCounts = kindCounts,
            ExpensiveEffectCount = expensiveEffects,
            MaterializedMaskTileCount = 1,
        };

        return new Stage13Fixture(
            document,
            layer,
            manifest,
            objects[^1],
            new WorldBounds(0, 0, 1_100, 1_100));
    }

    private static Guid DeterministicGuid(int seed, string category, int discriminator)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"stage13:{seed}:{category}:{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
