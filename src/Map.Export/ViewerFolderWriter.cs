using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Map.Export;

/// <summary>
/// Writes a <see cref="PngTilePyramid"/> as a folder anyone can open by double-clicking
/// «Открыть карту.html»: the tiles of every level as lossless WebP (checked against their PNG
/// pixels, PNG when WebP would differ) under <c>tiles/level/column_row</c>, level 0 = every source
/// pixel, and Map's own offline viewer page. Nothing is loaded from the internet; fully transparent
/// tiles are left out and the page draws nothing there (owner, 2026-10-30: the whole largest map
/// opened instantly, without quality loss).
/// </summary>
public static class ViewerFolderWriter
{
    public const string PageName = "Открыть карту.html";

    /// <summary>Writes the folder at <paramref name="folder"/> (must not exist) and returns the tile count.</summary>
    public static int Write(PngTilePyramid pyramid, string folder, string title,
        CancellationToken cancellationToken = default, Action<double>? progress = null, int parallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        try
        {
            return Write(pyramid, folder, title, webp: true, cancellationToken, progress, parallelism);
        }
        catch (MbTilesWriter.WebpNotExactException)
        {
            Directory.Delete(folder, recursive: true);
            return Write(pyramid, folder, title, webp: false, cancellationToken, progress, parallelism);
        }
    }

    private static int Write(PngTilePyramid pyramid, string folder, string title, bool webp,
        CancellationToken cancellationToken, Action<double>? progress, int parallelism)
    {
        Directory.CreateDirectory(folder);
        var tiles = new List<(int Level, int Column, int Row)>();
        for (var level = 0; level < pyramid.Levels; level++)
        {
            var (width, height) = pyramid.LevelSize(level);
            Directory.CreateDirectory(Path.Combine(folder, "tiles", level.ToString(CultureInfo.InvariantCulture)));
            for (var row = 0; row * pyramid.TileEdge < height; row++)
            for (var column = 0; column * pyramid.TileEdge < width; column++)
                tiles.Add((level, column, row));
        }

        var extension = webp ? "webp" : "png";
        var written = 0;
        var workers = parallelism > 0 ? parallelism : Environment.ProcessorCount;
        var done = 0;
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, tile =>
        {
            var (id, data) = MbTilesWriter.Encode(pyramid.CheckTile(tile.Level, tile.Column, tile.Row), webp);
            if (id is not null)
            {
                File.WriteAllBytes(Path.Combine(folder, "tiles", tile.Level.ToString(CultureInfo.InvariantCulture),
                    $"{tile.Column}_{tile.Row}.{extension}"), data!);
                Interlocked.Increment(ref written);
            }
            var count = Interlocked.Increment(ref done);
            if (count % 64 == 0) progress?.Invoke(count / (double)tiles.Count);
        });

        var settings = JsonSerializer.Serialize(new
        {
            title = string.IsNullOrWhiteSpace(title) ? "Карта" : title,
            width = pyramid.Width,
            height = pyramid.Height,
            tile = pyramid.TileEdge,
            levels = pyramid.Levels,
            format = extension,
        });
        File.WriteAllText(Path.Combine(folder, PageName), Page().Replace("/*MAP_SETTINGS*/{}", settings, StringComparison.Ordinal));
        progress?.Invoke(1);
        return written;
    }

    private static string Page()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Map.Export.ViewerPage.html")
            ?? throw new InvalidOperationException("Страница просмотра не найдена в сборке.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
