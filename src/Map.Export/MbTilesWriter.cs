using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// Writes a <see cref="PngTilePyramid"/> as one MBTiles file (DEC-051): an SQLite database with
/// the standard <c>metadata</c> table and a <c>tiles</c> view, TMS row order, zoom 0 = the coarsest
/// level and the highest zoom = every source pixel. The map is a plain picture, not a
/// geographic one, so no bounds are claimed; the tile edge is recorded as <c>tile_size</c>.
/// Tiles are lossless WebP (about 30 % smaller than PNG on the owner's painted land, 2026-10-30),
/// each decoded back and compared with its source pixels; a picture whose WebP would differ in any
/// tile is written as PNG instead. Identical tiles are stored once (<c>images</c> + <c>map</c>, the
/// usual deduplicated MBTiles layout) and fully transparent tiles are left out, as MBTiles allows.
/// </summary>
public static class MbTilesWriter
{
    public const string Extension = ".mbtiles";

    /// <summary>Writes every tile of the pyramid into <paramref name="path"/> and returns the tile count.</summary>
    public static int Write(PngTilePyramid pyramid, string path, string name,
        CancellationToken cancellationToken = default, Action<double>? progress = null, int parallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Write(pyramid, path, name, webp: true, cancellationToken, progress, parallelism);
        }
        catch (WebpNotExactException)
        {
            return Write(pyramid, path, name, webp: false, cancellationToken, progress, parallelism);
        }
    }

    private static int Write(PngTilePyramid pyramid, string path, string name, bool webp,
        CancellationToken cancellationToken, Action<double>? progress, int parallelism)
    {
        if (File.Exists(path)) File.Delete(path);
        var tiles = new List<(int Level, int Column, int Row)>();
        for (var level = 0; level < pyramid.Levels; level++)
        {
            var (columns, rows) = Grid(pyramid, level);
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                tiles.Add((level, column, row));
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection,
            "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;"
            + "CREATE TABLE metadata (name text, value text);"
            + "CREATE TABLE images (tile_id text PRIMARY KEY, tile_data blob);"
            + "CREATE TABLE map (zoom_level integer, tile_column integer, tile_row integer, tile_id text);"
            + "CREATE UNIQUE INDEX map_index ON map (zoom_level, tile_column, tile_row);"
            + "CREATE VIEW tiles AS SELECT map.zoom_level AS zoom_level, map.tile_column AS tile_column,"
            + " map.tile_row AS tile_row, images.tile_data AS tile_data FROM map JOIN images ON images.tile_id = map.tile_id;");

        var maxZoom = pyramid.Levels - 1;
        var written = 0;
        var stored = new HashSet<string>(StringComparer.Ordinal);
        using (var transaction = connection.BeginTransaction())
        {
            using (var metadata = connection.CreateCommand())
            {
                metadata.Transaction = transaction;
                metadata.CommandText = "INSERT INTO metadata (name, value) VALUES ($name, $value)";
                var key = metadata.Parameters.Add("$name", SqliteType.Text);
                var value = metadata.Parameters.Add("$value", SqliteType.Text);
                foreach (var (k, v) in new (string, string)[]
                {
                    ("name", string.IsNullOrWhiteSpace(name) ? "Map" : name),
                    ("format", webp ? "webp" : "png"),
                    ("type", "overlay"),
                    ("version", "1"),
                    ("scheme", "tms"),
                    ("minzoom", "0"),
                    ("maxzoom", maxZoom.ToString(CultureInfo.InvariantCulture)),
                    ("tile_size", pyramid.TileEdge.ToString(CultureInfo.InvariantCulture)),
                    ("map_width", pyramid.Width.ToString(CultureInfo.InvariantCulture)),
                    ("map_height", pyramid.Height.ToString(CultureInfo.InvariantCulture)),
                    ("description", "Map picture pyramid: zoom 0 is the coarsest level, the highest zoom keeps every pixel; not georeferenced."),
                })
                {
                    key.Value = k; value.Value = v;
                    metadata.ExecuteNonQuery();
                }
            }

            using var image = connection.CreateCommand();
            image.Transaction = transaction;
            image.CommandText = "INSERT INTO images (tile_id, tile_data) VALUES ($id, $data)";
            var imageId = image.Parameters.Add("$id", SqliteType.Text);
            var imageData = image.Parameters.Add("$data", SqliteType.Blob);
            using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = "INSERT INTO map (zoom_level, tile_column, tile_row, tile_id) VALUES ($z, $x, $y, $id)";
            var z = map.Parameters.Add("$z", SqliteType.Integer);
            var x = map.Parameters.Add("$x", SqliteType.Integer);
            var y = map.Parameters.Add("$y", SqliteType.Integer);
            var mapId = map.Parameters.Add("$id", SqliteType.Text);

            // Tiles are encoded and checked on every core a batch at a time; SQLite is written on
            // this thread in the fixed tile order, so the file is the same whatever the core count.
            var workers = parallelism > 0 ? parallelism : Environment.ProcessorCount;
            var batch = new (string? Id, byte[]? Data)[Math.Max(1, workers * 4)];
            for (var start = 0; start < tiles.Count; start += batch.Length)
            {
                var count = Math.Min(batch.Length, tiles.Count - start);
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
                    i => batch[i] = Encode(pyramid.CheckTile(tiles[start + i].Level, tiles[start + i].Column, tiles[start + i].Row), webp));
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (level, column, row) = tiles[start + i];
                    var (id, data) = batch[i];
                    batch[i] = default;
                    if (id is null) continue; // fully transparent: nothing to show
                    if (stored.Add(id))
                    {
                        imageId.Value = id; imageData.Value = data;
                        image.ExecuteNonQuery();
                    }
                    var (_, rows) = Grid(pyramid, level);
                    z.Value = maxZoom - level;
                    x.Value = column;
                    y.Value = rows - 1 - row;
                    mapId.Value = id;
                    map.ExecuteNonQuery();
                    written++;
                }
                progress?.Invoke((start + count) / (double)tiles.Count);
            }

            transaction.Commit();
        }

        connection.Close();
        return written;
    }

    /// <summary>
    /// The tile's stored bytes and their content id, or (null, null) for a fully transparent tile.
    /// WebP is decoded back and must give the same straight RGBA as the PNG.
    /// </summary>
    internal static (string? Id, byte[]? Data) Encode(string pngPath, bool webp)
    {
        var png = File.ReadAllBytes(pngPath);
        using var pixels = DecodeStraight(png);
        var span = pixels.GetPixelSpan();
        var visible = false;
        for (var i = 3; i < span.Length; i += 4) if (span[i] != 0) { visible = true; break; }
        if (!visible) return (null, null);
        byte[] data;
        if (webp)
        {
            using (var pixmap = pixels.PeekPixels())
            using (var encoded = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 0)))
                data = encoded?.ToArray() ?? throw new WebpNotExactException();
            using var back = DecodeStraight(data);
            if (back.Width != pixels.Width || back.Height != pixels.Height || !back.GetPixelSpan().SequenceEqual(span))
                throw new WebpNotExactException();
        }
        else data = png;
        return (Convert.ToHexStringLower(SHA256.HashData(data)), data);
    }

    private static SKBitmap DecodeStraight(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded))
            ?? throw new InvalidDataException("Участок просмотра не декодируется.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Участок просмотра не декодируется.");
        }
        return bitmap;
    }

    internal sealed class WebpNotExactException() : Exception("WebP без потерь не повторил пиксели участка.");

    private static (int Columns, int Rows) Grid(PngTilePyramid pyramid, int level)
    {
        var (width, height) = pyramid.LevelSize(level);
        return ((width + pyramid.TileEdge - 1) / pyramid.TileEdge, (height + pyramid.TileEdge - 1) / pyramid.TileEdge);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
