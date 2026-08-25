using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;

namespace Map.Spike.Persistence.Container;

/// <summary>
/// Turns a document into container bytes and back. Serialization is deterministic: fixed property
/// order, sorted tile order and a fixed ZIP entry timestamp, so the same logical document always
/// produces the same file bytes.
/// </summary>
public static class SpikeDocumentSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void Write(SpikeDocument document, Stream destination)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var scene = BuildScene(document);
        var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene, JsonOptions);

        var tileManifest = new TileManifestDto();
        var tilePayloads = new List<(string Entry, byte[] Data)>();

        CollectRasterTiles(document.Brush, tilePayloads);
        CollectRasterTiles(document.Decor, tilePayloads);
        CollectMaskTiles(document.Mask, tilePayloads);

        foreach (var (Entry, Data) in tilePayloads)
        {
            tileManifest.Tiles.Add(new TileEntryDto
            {
                Entry = Entry,
                Length = Data.Length,
                Sha256 = Sha256Hex(Data),
            });
        }

        var tileManifestBytes = JsonSerializer.SerializeToUtf8Bytes(tileManifest, JsonOptions);

        var manifest = new ManifestDto
        {
            DocumentId = document.DocumentId.ToString("D"),
            Title = document.Title,
            CanvasWidth = document.CanvasWidth,
            CanvasHeight = document.CanvasHeight,
            SchemaVersion = document.SchemaVersion,
            CreatedUtc = document.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ModifiedUtc = document.ModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Revision = document.Revision,
            SceneSha256 = Sha256Hex(sceneBytes),
            TileManifestSha256 = Sha256Hex(tileManifestBytes),
        };

        WriteEntry(archive, SpikeFormat.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        WriteEntry(archive, SpikeFormat.SceneEntry, sceneBytes);
        WriteEntry(archive, SpikeFormat.TileManifestEntry, tileManifestBytes);

        foreach (var (Entry, Data) in tilePayloads)
        {
            WriteEntry(archive, Entry, Data);
        }
    }

    public static SpikeDocument Read(Stream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        var manifestBytes = ReadEntry(archive, SpikeFormat.ManifestEntry);
        var manifest = JsonSerializer.Deserialize<ManifestDto>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("Manifest is empty.");

        if (manifest.Format != SpikeFormat.FormatId)
        {
            throw new InvalidDataException($"Unexpected container format: {manifest.Format}");
        }

        if (manifest.SchemaVersion != SpikeFormat.SchemaVersion)
        {
            throw new InvalidDataException(
                $"Schema version {manifest.SchemaVersion} is not supported; expected {SpikeFormat.SchemaVersion}.");
        }

        var sceneBytes = ReadEntry(archive, SpikeFormat.SceneEntry);
        if (!string.Equals(Sha256Hex(sceneBytes), manifest.SceneSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("scene.json checksum does not match the manifest.");
        }

        var tileManifestBytes = ReadEntry(archive, SpikeFormat.TileManifestEntry);
        if (!string.Equals(Sha256Hex(tileManifestBytes), manifest.TileManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("tile-manifest.json checksum does not match the manifest.");
        }

        var scene = JsonSerializer.Deserialize<SceneDto>(sceneBytes, JsonOptions)
            ?? throw new InvalidDataException("scene.json is empty.");
        var tileManifest = JsonSerializer.Deserialize<TileManifestDto>(tileManifestBytes, JsonOptions)
            ?? throw new InvalidDataException("tile-manifest.json is empty.");

        var objects = new ObjectLayer(LayerId.Parse(scene.ObjectLayerId), "objects");
        foreach (var o in scene.Objects)
        {
            objects.Add(new SpikeObject
            {
                Id = ObjectId.Parse(o.Id),
                Kind = Enum.Parse<SpikeObjectKind>(o.Kind),
                Center = new Vec2(o.CenterX, o.CenterY),
                Size = o.Size,
                Rotation = o.Rotation,
                ColorArgb = o.ColorArgb,
            });
        }

        objects.RebuildIndex();

        var tileSize = scene.TileSize;
        var brush = new RasterLayer(LayerId.Parse(scene.BrushLayerId), "brush", tileSize);
        var decor = new RasterLayer(LayerId.Parse(scene.DecorLayerId), "decor", tileSize);
        var mask = new MaskLayer(
            LayerId.Parse(scene.MaskLayerId),
            LayerId.Parse(scene.MaskTargetLayerId),
            "brush-mask",
            tileSize);

        foreach (var tile in tileManifest.Tiles)
        {
            var data = ReadEntry(archive, tile.Entry);

            if (data.Length != tile.Length)
            {
                throw new InvalidDataException(
                    $"Tile {tile.Entry} has length {data.Length}, expected {tile.Length}.");
            }

            if (!string.Equals(Sha256Hex(data), tile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Tile {tile.Entry} checksum does not match the tile manifest.");
            }

            ApplyTile(tile.Entry, data, brush, decor, mask);
        }

        return DocumentRehydrator.Rehydrate(
            Guid.Parse(manifest.DocumentId),
            manifest.Title,
            manifest.CanvasWidth,
            manifest.CanvasHeight,
            manifest.SchemaVersion,
            DateTimeOffset.Parse(manifest.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(manifest.ModifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            manifest.Revision,
            objects,
            brush,
            decor,
            mask);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ApplyTile(string entry, byte[] data, RasterLayer brush, RasterLayer decor, MaskLayer mask)
    {
        var parsed = ParseTileEntry(entry);

        if (parsed.Kind == "rasters" && parsed.LayerId == brush.Id.ToString())
        {
            brush.SetTile(parsed.Key, data);
            return;
        }

        if (parsed.Kind == "rasters" && parsed.LayerId == decor.Id.ToString())
        {
            decor.SetTile(parsed.Key, data);
            return;
        }

        if (parsed.Kind == "masks" && parsed.LayerId == mask.Id.ToString())
        {
            mask.SetTile(parsed.Key, data);
            return;
        }

        throw new InvalidDataException($"Tile entry {entry} does not belong to any known layer.");
    }

    private static (string Kind, string LayerId, TileKey Key) ParseTileEntry(string entry)
    {
        // Canonical form is "<kind>/<layer-guid>/<x>_<y>.bin". Anything else is rejected so a
        // crafted archive cannot escape the container (docs/04 import-safety rule).
        if (entry.Contains("..", StringComparison.Ordinal) || entry.Contains('\\'))
        {
            throw new InvalidDataException($"Unsafe entry name: {entry}");
        }

        var parts = entry.Split('/');
        if (parts.Length != 3 || !parts[2].EndsWith(".bin", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected tile entry: {entry}");
        }

        return (parts[0], parts[1], TileKey.Parse(parts[2][..^4]));
    }

    private static SceneDto BuildScene(SpikeDocument document)
    {
        var scene = new SceneDto
        {
            ObjectLayerId = document.Objects.Id.ToString(),
            BrushLayerId = document.Brush.Id.ToString(),
            DecorLayerId = document.Decor.Id.ToString(),
            MaskLayerId = document.Mask.Id.ToString(),
            MaskTargetLayerId = document.Mask.TargetLayerId.ToString(),
            TileSize = document.Brush.TileSize,
        };

        foreach (var o in document.Objects.Objects)
        {
            scene.Objects.Add(new SceneObjectDto
            {
                Id = o.Id.ToString(),
                Kind = o.Kind.ToString(),
                CenterX = o.Center.X,
                CenterY = o.Center.Y,
                Size = o.Size,
                Rotation = o.Rotation,
                ColorArgb = o.ColorArgb,
            });
        }

        return scene;
    }

    private static void CollectRasterTiles(RasterLayer layer, List<(string Entry, byte[] Data)> into)
    {
        foreach (var key in Sorted(layer.TileKeys))
        {
            var tile = layer.TryGetTile(key);

            // Fully transparent tiles are never stored (docs/05 raster-tile rule).
            if (tile is null || IsFullyTransparent(tile))
            {
                continue;
            }

            into.Add(($"{SpikeFormat.RasterPrefix}{layer.Id}/{key}.bin", tile));
        }
    }

    private static void CollectMaskTiles(MaskLayer mask, List<(string Entry, byte[] Data)> into)
    {
        foreach (var key in Sorted(mask.TileKeys))
        {
            var tile = mask.TryGetTile(key);
            if (tile is null)
            {
                continue;
            }

            into.Add(($"{SpikeFormat.MaskPrefix}{mask.Id}/{key}.bin", tile));
        }
    }

    private static IEnumerable<TileKey> Sorted(IEnumerable<TileKey> keys) =>
        keys.OrderBy(k => k.Y).ThenBy(k => k.X);

    private static bool IsFullyTransparent(byte[] rgbaPremultiplied)
    {
        for (var i = 3; i < rgbaPremultiplied.Length; i += RasterLayer.BytesPerPixel)
        {
            if (rgbaPremultiplied[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = SpikeFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidDataException($"Container is missing required entry: {name}");

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
