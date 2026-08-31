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

    private static Guid DeterministicGuid(int seed, string category, int discriminator)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"stage13:{seed}:{category}:{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
