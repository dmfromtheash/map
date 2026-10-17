using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Export;

/// <summary>What a Universal VTT export wrote: the file, the grid it used and how many walls and doors.</summary>
public sealed record VttExportResult(string Path, int PixelsPerGrid, int Walls, int Portals, long Bytes);

/// <summary>
/// Universal VTT (*.dd2vtt): one JSON file a virtual tabletop opens as a map. It carries the
/// exported picture as base64 PNG, the grid (<c>resolution</c>), the walls (<c>line_of_sight</c>)
/// from strokes and roads marked «стена блокирует свет», and the doors (<c>portals</c>) cut into
/// them. Coordinates are in grid cells from the exported area's top-left corner. Lights are left
/// empty: Map has no light sources yet.
/// </summary>
public static class UniversalVttExporter
{
    public const string Extension = "dd2vtt";

    /// <summary>The format revision of the community specification this file follows.</summary>
    public const double FormatVersion = 0.3;

    /// <summary>
    /// Renders the plan to PNG with the regular export engine (the same pixels as a PNG export)
    /// and writes the dd2vtt next to it atomically: nothing replaces <paramref name="destinationPath"/>
    /// until the whole file is written.
    /// </summary>
    public static async Task<VttExportResult> ExportAsync(
        IImageExporter engine, MapDocument snapshot, ExportPlan plan, string destinationPath,
        IStampImageSource? stamps, double gridCellWorld,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!double.IsFinite(gridCellWorld) || gridCellWorld <= 0)
            throw new ExportValidationException("Размер клетки для виртуального стола должен быть больше нуля.");
        if (plan.Format != ExportImageFormat.Png || plan.OutputMode != ExportOutputMode.SingleImage)
            throw new ExportValidationException("Для виртуального стола нужна одна картинка PNG.");

        var folder = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        var image = Path.Combine(folder, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".png");
        var temporary = Path.Combine(folder, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await engine.ExportAsync(snapshot, plan, image, stamps, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var png = await File.ReadAllBytesAsync(image, cancellationToken);
            var (walls, portals) = Write(snapshot, plan, gridCellWorld, png, temporary, out var pixelsPerGrid);
            File.Move(temporary, destinationPath, overwrite: true);
            return new VttExportResult(destinationPath, pixelsPerGrid, walls, portals, new FileInfo(destinationPath).Length);
        }
        finally
        {
            TryDelete(image);
            TryDelete(temporary);
        }
    }

    /// <summary>Grid cells per world unit and the pixels one cell takes in the exported picture.</summary>
    public static int PixelsPerGrid(ExportPlan plan, double gridCellWorld) =>
        Math.Max(1, (int)Math.Round(gridCellWorld * plan.OutputWidth / (double)plan.SourceRegion.Width));

    private static (int Walls, int Portals) Write(
        MapDocument snapshot, ExportPlan plan, double cell, byte[] png, string path, out int pixelsPerGrid)
    {
        var region = plan.SourceRegion;
        pixelsPerGrid = PixelsPerGrid(plan, cell);
        var collected = VttGeometry.Collect(snapshot);
        var reach = new WorldBounds(region.X - cell, region.Y - cell, region.X + region.Width + cell, region.Y + region.Height + cell);
        var walls = collected.Walls.Where(wall => wall.Any(p => Inside(reach, p))).ToList();
        var doors = collected.Portals.Where(door => Inside(reach, door.Centre)).ToList();

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        void Point(string? name, VectorPoint p)
        {
            if (name is null) json.WriteStartObject(); else json.WriteStartObject(name);
            json.WriteNumber("x", Math.Round((p.X - region.X) / cell, 4));
            json.WriteNumber("y", Math.Round((p.Y - region.Y) / cell, 4));
            json.WriteEndObject();
        }

        json.WriteStartObject();
        json.WriteNumber("format", FormatVersion);
        json.WriteStartObject("resolution");
        json.WriteStartObject("map_origin"); json.WriteNumber("x", 0); json.WriteNumber("y", 0); json.WriteEndObject();
        json.WriteStartObject("map_size");
        json.WriteNumber("x", Math.Round(plan.OutputWidth / (double)pixelsPerGrid, 4));
        json.WriteNumber("y", Math.Round(plan.OutputHeight / (double)pixelsPerGrid, 4));
        json.WriteEndObject();
        json.WriteNumber("pixels_per_grid", pixelsPerGrid);
        json.WriteEndObject();

        json.WriteStartArray("line_of_sight");
        foreach (var wall in walls)
        {
            json.WriteStartArray();
            foreach (var p in wall) Point(null, p);
            json.WriteEndArray();
        }
        json.WriteEndArray();
        json.WriteStartArray("objects_line_of_sight");
        json.WriteEndArray();

        json.WriteStartArray("portals");
        foreach (var door in doors)
        {
            json.WriteStartObject();
            Point("position", door.Centre);
            json.WriteStartArray("bounds");
            Point(null, door.Start);
            Point(null, door.End);
            json.WriteEndArray();
            json.WriteNumber("rotation", Math.Round(Math.Atan2(door.End.Y - door.Start.Y, door.End.X - door.Start.X), 6));
            json.WriteBoolean("closed", true);
            json.WriteBoolean("freestanding", false);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartObject("environment");
        json.WriteBoolean("baked_lighting", false);
        json.WriteString("ambient_light", "ffffffff");
        json.WriteEndObject();
        json.WriteStartArray("lights");
        json.WriteEndArray();
        json.WriteBase64String("image", png);
        json.WriteEndObject();
        json.Flush();
        stream.Flush(flushToDisk: true);
        return (walls.Count, doors.Count);
    }

    private static bool Inside(WorldBounds bounds, VectorPoint p) =>
        p.X >= bounds.Left && p.X <= bounds.Right && p.Y >= bounds.Top && p.Y <= bounds.Bottom;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
