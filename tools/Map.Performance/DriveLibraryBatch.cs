using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed class DriveLibraryReport
{
    public bool GatePassed { get; set; }
    public int Batch { get; set; }
    public int Files { get; set; }
    public List<string> Images { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public List<DriveLibrarySetCheck> Sets { get; } = [];
}

internal sealed class DriveLibrarySetCheck
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Catalog { get; init; }
    public int Variants { get; set; }
    public bool Found { get; set; }
    public int Readable { get; set; }
    public int Thumbnails { get; set; }
    public int Drawn { get; set; }
    public string Sizes { get; set; } = "";
    public bool Passed => Found && Readable == Variants && Thumbnails == Variants && Drawn == Variants;
}

/// <summary>
/// The owner's test references on the external drive, one batch at a time (owner 2026-11-12: «каждые 100 ассетов
/// проверяй на нашей программе»). The next ≈100 upscaled files are imported into the drive's own catalogs with the
/// program's import, then checked the way Map uses them: a fresh library attaches the drive through the marker
/// (connection), every variant must read back verified from the drive with its preview, and the program's renderer
/// draws each set far (25 %), at 100 % and close (400 %) into one sheet. Sets already checked are never redone.
/// Library folder: MAP_DRIVE_LIBRARY_BUILD (default H:\MapLibrary). Batch size: --object-count.
/// </summary>
internal static class DriveLibraryBatch
{
    private sealed record PlanSet(string Key, string Catalog, string Name, string One, string Use,
        List<string> Collection, List<string> Tags, List<string> Files, bool Ready, List<int> Factor);

    private sealed class State
    {
        public List<string> Imported { get; set; } = [];
        public List<string> Checked { get; set; } = [];
        public int Batches { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private const string Paper = "#FFEFE6D2";

    public static DriveLibraryReport Run(PerformanceOptions options)
    {
        var library = Environment.GetEnvironmentVariable("MAP_DRIVE_LIBRARY_BUILD") is { Length: > 0 } configured ? configured : @"H:\MapLibrary";
        var plan = JsonSerializer.Deserialize<List<PlanSet>>(File.ReadAllBytes(Path.Combine(library, "plan", "sets.json")), Json)!;
        var statePath = Path.Combine(library, "checks", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = File.Exists(statePath) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(statePath), Json)! : new State();
        var done = state.Checked.ToHashSet(StringComparer.Ordinal);

        // The next sets whose every file is upscaled and verified; unclassified names wait for their Russian names.
        var batch = new List<PlanSet>();
        foreach (var set in plan)
        {
            if (done.Contains(set.Key) || !set.Ready || set.Tags.Contains("Не разобрано")) continue;
            batch.Add(set);
            if (batch.Sum(s => s.Files.Count) >= options.ObjectCount) break;
        }

        var report = new DriveLibraryReport { Batch = state.Batches + 1, Files = batch.Sum(s => s.Files.Count) };
        if (batch.Count == 0) { report.Notes["result"] = "nothing ready"; report.GatePassed = true; return report; }

        // 1. Import with the program's own catalog code into one catalog per pack on the drive.
        var upscaled = Path.Combine(library, "upscaled");
        var stamp = DateTimeOffset.UtcNow;
        foreach (var group in batch.GroupBy(s => s.Catalog))
        {
            var catalog = new AssetCatalog(Path.Combine(library, group.Key));
            catalog.ImportSets([.. group.Select(set => new AssetSetImport
            {
                StableKey = set.Key, Name = set.Name, CollectionPath = set.Collection, Tags = set.Tags,
                Paths = [.. set.Files.Select(file => Path.Combine(upscaled, Path.ChangeExtension(file, ".webp")))],
                Labels = [.. set.Files.Select((_, i) => set.Files.Count == 1 ? set.One : $"{set.One} {i + 1}")],
                Provenance = new AssetProvenance
                {
                    Source = "owner-refs (test), upscaled x2/x4 Real-ESRGAN", Author = "",
                    LicenseStatus = AssetLicenseStatus.Unknown, CommercialUseAllowed = false,
                },
            })], stamp);
            catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        }

        var catalogs = Directory.GetDirectories(library).Where(dir => File.Exists(Path.Combine(dir, "catalog.json")))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        // Written next to the marker and moved over it: a running Map never reads half a marker.
        var marker = Path.Combine(library, AssetDriveLibraries.MarkerFileName);
        File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(new
        {
            format = AssetDriveLibraries.Format, version = 1, id = "owner-refs-t7", name = "Тестовые ассеты (диск T7)", catalogs,
        }, Json));
        File.Move(marker + ".tmp", marker, overwrite: true);
        state.Imported.AddRange(batch.Select(s => s.Key).Where(k => !state.Imported.Contains(k)));

        // 2. Connection: a fresh, empty library attaches the drive exactly as the program does.
        var ownRoot = Path.Combine(Path.GetTempPath(), "map-drive-check", Guid.NewGuid().ToString("N"));
        var own = new AssetCatalog(ownRoot);
        try
        {
            // The marker is read exactly as the program's drive scan reads it.
            var found = AssetDriveLibraries.TryRead(library) ?? throw new InvalidOperationException("Метка библиотеки на диске не читается.");
            own.SetMounts(AssetDriveLibraries.CatalogRoots([found]));
            report.Notes["attached catalogs"] = own.Mounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            report.Notes["cards on the drive"] = own.Sets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            report.Notes["test cards found by tag"] = own.Query(new AssetQuery { Tag = "Тестовое" }).TotalSets.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var document = new MapDocument(DocumentId.New(), "Проверка ассетов", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(4000, 4000, "px", Paper), [new BaseColorLayer(LayerId.New(), "Бумага", true, false, 1, Paper)], []);
            using var images = new CatalogStampImageSource(own, () => document, () => null);
            var rows = new List<SKImage>();
            foreach (var set in batch)
            {
                var check = new DriveLibrarySetCheck { Key = set.Key, Name = set.Name, Catalog = set.Catalog };
                report.Sets.Add(check);
                var card = own.FindSet(AssetIdentity.SetIdForKey(set.Key));
                check.Found = card is not null;
                if (card is null) continue;
                var records = card.VariantIds.Select(own.RecordForVariant).OfType<AssetRecord>().ToList();
                check.Variants = card.VariantIds.Count;
                check.Readable = records.Count(r => own.ReadVerified(r).Status == AssetResolutionStatus.Available);
                check.Thumbnails = records.Count(r => own.TryThumbnailPath(r, out var t) && File.Exists(t));
                check.Sizes = string.Join(", ", records.Take(3).Select(r => $"{r.Width}×{r.Height}"));
                rows.Add(RenderRow(set, records, own, images, check));
            }

            report.Images.Add(SaveSheet(rows, Path.Combine(library, "checks", $"batch-{report.Batch:D4}.jpg")));
            foreach (var row in rows) row.Dispose();
        }
        finally
        {
            own.SetMounts([]);
            own.FlushMetadataAsync().GetAwaiter().GetResult();
            try { Directory.Delete(ownRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        report.GatePassed = report.Sets.All(s => s.Passed);
        if (report.GatePassed)
        {
            state.Checked.AddRange(batch.Select(s => s.Key));
            state.Batches++;
        }

        File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(state, Json));
        File.WriteAllBytes(Path.Combine(library, "checks", $"batch-{report.Batch:D4}.json"), JsonSerializer.SerializeToUtf8Bytes(report, Json));
        return report;
    }

    /// <summary>One sheet row: the drive preview, the variants at 25 % and 100 %, and the cover at 400 %.</summary>
    private static SKImage RenderRow(PlanSet set, List<AssetRecord> records, AssetCatalog catalog, CatalogStampImageSource images, DriveLibrarySetCheck check)
    {
        const int Thumb = 160, Strip = 980, Close = 300, Height = 330, Label = 30;
        var factor = set.Factor.Count > 0 ? set.Factor.Max() : 2;
        var shown = records.Take(5).ToList();
        // Each variant at the size it had in its pack (the upscale only adds pixels), fitted into a 180-unit cell.
        var objects = new List<SceneObject>();
        var x = 10.0;
        foreach (var record in shown)
        {
            var w = record.Width / (double)factor; var h = record.Height / (double)factor;
            var fit = Math.Min(1, 180 / Math.Max(w, h));
            w *= fit; h *= fit;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, set.Name, x + (180 - w) / 2, 10 + (180 - h) / 2, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha256) });
            x += 190;
        }

        var document = new MapDocument(DocumentId.New(), "Ряд", DateTimeOffset.UnixEpoch, new CanvasDefinition(1000, 200, "px", Paper),
            [new BaseColorLayer(LayerId.New(), "Бумага", true, false, 1, Paper), new ObjectLayer(LayerId.New(), "Ассеты", true, false, 1, objects)], []);

        SKBitmap Render(WorldBounds view, double scale)
        {
            var width = (int)Math.Round((view.Right - view.Left) * scale);
            var height = (int)Math.Round((view.Bottom - view.Top) * scale);
            var bitmap = new SKBitmap(Math.Max(1, width), Math.Max(1, height));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColor.Parse(Paper));
            canvas.Scale((float)scale);
            canvas.Translate((float)-view.Left, (float)-view.Top);
            SceneRenderer.Render(canvas, document, new RenderOptions { IsExport = false, Stamps = images, VisibleWorld = view, ViewScale = scale });
            return bitmap;
        }

        using var far = Render(new WorldBounds(0, 0, 1000, 200), 0.25);
        using var normal = Render(new WorldBounds(0, 0, 1000, 200), 1.0);
        var cover = objects[0];
        var cx = cover.X + cover.Width / 2; var cy = cover.Y + cover.Height / 2;
        using var close = Render(new WorldBounds(cx - 37.5, cy - 37.5, cx + 37.5, cy + 37.5), 4.0);

        // Drawn = the stamp changed the paper inside its own box at 100 %.
        var paper = SKColor.Parse(Paper);
        foreach (var o in objects)
        {
            var changed = 0; var total = 0;
            for (var py = (int)o.Y; py < (int)(o.Y + o.Height); py += 3)
                for (var px = (int)o.X; px < (int)(o.X + o.Width); px += 3)
                {
                    if (px < 0 || py < 0 || px >= normal.Width || py >= normal.Height) continue;
                    total++;
                    var c = normal.GetPixel(px, py);
                    if (Math.Abs(c.Red - paper.Red) + Math.Abs(c.Green - paper.Green) + Math.Abs(c.Blue - paper.Blue) > 24) changed++;
                }
            if (total > 0 && changed > total / 200) check.Drawn++;
        }
        check.Drawn += records.Count - shown.Count; // variants not on the sheet were checked for reading and preview only

        var info = new SKImageInfo(Thumb + Strip + Close + 40, Height + Label);
        using var surface = SKSurface.Create(info);
        var c2 = surface.Canvas;
        c2.Clear(new SKColor(46, 40, 34));
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 17);
        using var text = new SKPaint { Color = check.Passed ? new SKColor(236, 226, 200) : new SKColor(255, 120, 100), IsAntialias = true };
        c2.DrawText($"{(check.Passed ? "✓" : "✗")} {set.Name}  ·  {set.Catalog}  ·  вариантов {check.Variants}  ·  ×{factor}  ·  {check.Sizes}", 8, 21, font, text);
        if (catalog.TryThumbnailPath(records[0], out var thumbPath) && thumbPath is not null && File.Exists(thumbPath))
        {
            using var thumb = SKBitmap.Decode(thumbPath);
            c2.DrawRect(SKRect.Create(8, Label + 4, Thumb, Thumb), new SKPaint { Color = SKColor.Parse(Paper) });
            c2.DrawBitmap(thumb, SKRect.Create(8, Label + 4, Thumb, Thumb), new SKPaint());
        }
        c2.DrawBitmap(far, 8, Label + Thumb + 12);
        c2.DrawBitmap(normal, SKRect.Create(Thumb + 16, Label + 4, Strip, Strip * 0.2f));
        c2.DrawBitmap(close, SKRect.Create(Thumb + Strip + 24, Label + 4, Close, Close));
        using var small = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13);
        using var dim = new SKPaint { Color = new SKColor(190, 175, 150), IsAntialias = true };
        c2.DrawText("превью · вдали 25 %", 8, Label + Thumb + 12 + far.Height + 16, small, dim);
        c2.DrawText("100 %", Thumb + 16, Label + 4 + Strip * 0.2f + 16, small, dim);
        c2.DrawText("400 %", Thumb + Strip + 24, Label + Close + 20, small, dim);
        return surface.Snapshot();
    }

    private static string SaveSheet(List<SKImage> rows, string path)
    {
        var width = rows.Max(r => r.Width); var height = rows.Sum(r => r.Height + 6);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(30, 26, 22));
        var y = 0;
        foreach (var row in rows) { surface.Canvas.DrawImage(row, 0, y); y += row.Height + 6; }
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        using (var output = File.Create(path)) data.SaveTo(output);
        return path;
    }
}
