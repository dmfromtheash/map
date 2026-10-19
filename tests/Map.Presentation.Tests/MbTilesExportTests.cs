using Map.Core.Documents;
using Map.Export;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class MbTilesExportTests
{
    [Fact]
    public void MbTiles_requires_png()
    {
        var exporter = new ImageExportEngine();
        Assert.Throws<ExportValidationException>(() => exporter.Plan(Document(300, 200), new ExportRequest(
            ExportImageFormat.Jpeg, ExportAreaMode.WholeCanvas, null, 300, 200, ExportOutputMode.MbTiles, false, false)));
    }

    [Fact]
    public async Task Small_map_becomes_one_tile_at_zoom_zero_with_metadata()
    {
        using var temp = new TempFolder();
        var exporter = new ImageExportEngine();
        var document = Document(300, 200);
        var plan = exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 300, 200, ExportOutputMode.MbTiles, false, false));
        var destination = Path.Combine(temp.Path, "small.mbtiles");
        var result = await exporter.ExportAsync(document, plan, destination, stamps: null);
        Assert.Equal(ExportOutputMode.MbTiles, result.OutputMode);
        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(destination));
        Assert.Empty(Directory.GetDirectories(temp.Path));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        Assert.Equal("png", Scalar(connection, "SELECT value FROM metadata WHERE name = 'format'"));
        Assert.Equal("0", Scalar(connection, "SELECT value FROM metadata WHERE name = 'maxzoom'"));
        Assert.Equal("300", Scalar(connection, "SELECT value FROM metadata WHERE name = 'map_width'"));
        Assert.Equal("1", Scalar(connection, "SELECT COUNT(*) FROM tiles"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level = 0 AND tile_column = 0 AND tile_row = 0";
        var bytes = (byte[])command.ExecuteScalar()!;
        using var tile = SKBitmap.Decode(bytes);
        Assert.Equal((300, 200), (tile.Width, tile.Height));
        Assert.Equal(SKColor.Parse("#FF182430"), tile.GetPixel(10, 10));
    }

    [Fact]
    public async Task Large_map_gets_a_pyramid_in_tms_row_order()
    {
        using var temp = new TempFolder();
        var exporter = new ImageExportEngine();
        var document = Document(2100, 1100);
        var plan = exporter.Plan(document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 2100, 1100, ExportOutputMode.MbTiles, false, false));
        var destination = Path.Combine(temp.Path, "big.mbtiles");
        var result = await exporter.ExportAsync(document, plan, destination, stamps: null);
        // 2100×1100 with 1024 tiles: level 0 → 3×2, level 1 (1050×550) → 2×1, level 2 (525×275) → 1×1.
        Assert.Equal(9, result.FileCount);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        Assert.Equal("2", Scalar(connection, "SELECT value FROM metadata WHERE name = 'maxzoom'"));
        Assert.Equal("6", Scalar(connection, "SELECT COUNT(*) FROM tiles WHERE zoom_level = 2"));
        Assert.Equal("2", Scalar(connection, "SELECT COUNT(*) FROM tiles WHERE zoom_level = 1"));
        Assert.Equal("1", Scalar(connection, "SELECT COUNT(*) FROM tiles WHERE zoom_level = 0"));
        // TMS: the bottom row of the picture is tile_row 0; at full zoom it is the shorter row.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level = 2 AND tile_column = 0 AND tile_row = 0";
        using var bottom = SKBitmap.Decode((byte[])command.ExecuteScalar()!);
        Assert.Equal((1024, 76), (bottom.Width, bottom.Height));
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static MapDocument Document(int width, int height, string background = "#FF182430") => new(
        DocumentId.New(),
        "mbtiles map",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(width, height, "px", background),
        [
            new BaseColorLayer(LayerId.New(), "base", true, false, 1, background),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, []),
        ]);

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-mbtiles-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
