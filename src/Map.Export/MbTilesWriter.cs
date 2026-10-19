using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Map.Export;

/// <summary>
/// Writes a <see cref="PngTilePyramid"/> as one MBTiles file (DEC-051): an SQLite database with
/// the standard <c>metadata</c> and <c>tiles</c> tables, TMS row order, zoom 0 = the coarsest
/// level and the highest zoom = every source pixel. The map is a plain picture, not a
/// geographic one, so no bounds are claimed; the tile edge is recorded as <c>tile_size</c>.
/// </summary>
public static class MbTilesWriter
{
    public const string Extension = ".mbtiles";

    /// <summary>Writes every tile of the pyramid into <paramref name="path"/> and returns the tile count.</summary>
    public static int Write(PngTilePyramid pyramid, string path, string name,
        CancellationToken cancellationToken = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path)) File.Delete(path);
        var total = 0;
        for (var level = 0; level < pyramid.Levels; level++)
        {
            var (columns, rows) = Grid(pyramid, level);
            total += columns * rows;
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
            + "CREATE TABLE tiles (zoom_level integer, tile_column integer, tile_row integer, tile_data blob);"
            + "CREATE UNIQUE INDEX tile_index ON tiles (zoom_level, tile_column, tile_row);");

        var maxZoom = pyramid.Levels - 1;
        var written = 0;
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
                    ("format", "png"),
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

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES ($z, $x, $y, $data)";
            var z = insert.Parameters.Add("$z", SqliteType.Integer);
            var x = insert.Parameters.Add("$x", SqliteType.Integer);
            var y = insert.Parameters.Add("$y", SqliteType.Integer);
            var data = insert.Parameters.Add("$data", SqliteType.Blob);
            for (var level = 0; level < pyramid.Levels; level++)
            {
                var (columns, rows) = Grid(pyramid, level);
                for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    z.Value = maxZoom - level;
                    x.Value = column;
                    y.Value = rows - 1 - row;
                    data.Value = File.ReadAllBytes(pyramid.CheckTile(level, column, row));
                    insert.ExecuteNonQuery();
                    written++;
                    progress?.Invoke(total == 0 ? 1 : written / (double)total);
                }
            }

            transaction.Commit();
        }

        connection.Close();
        return written;
    }

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
