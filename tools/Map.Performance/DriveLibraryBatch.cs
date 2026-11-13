using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed class DriveLibraryReport
{
    public string Catalog { get; set; } = "";
    public bool DryRun { get; set; }
    public bool GatePassed { get; set; }
    public int Batch { get; set; }
    public int Files { get; set; }
    public List<string> Images { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public List<DriveLibrarySetCheck> Sets { get; } = [];
    public List<string> Errors { get; } = [];
    public IReadOnlyList<string> Failures => [.. Errors, .. Sets.Where(s => !DryRun && !s.Passed)
        .Select(s => $"{s.Key}: {string.Join("; ", s.Errors)}; found={s.Found}, read={s.Readable}/{s.Variants}, thumbnails={s.Thumbnails}, drawn={s.Drawn}")];
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
    public List<string> Inputs { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Passed => Errors.Count == 0 && Found && Variants > 0 && Readable == Variants && Thumbnails == Variants && Drawn == Variants;
}

/// <summary>
/// The owner's test references on the external drive, one batch at a time (owner 2026-11-12: «каждые 100 ассетов
/// проверяй на нашей программе»). The next ≈100 upscaled files are imported into the drive's own catalogs with the
/// program's import, then checked the way Map uses them: a fresh library attaches the drive through the marker
/// (connection), every variant must read back verified from the drive with its preview, and the program's renderer
/// draws each set far (25 %), at 100 % and close (400 %) into one sheet. Sets already checked are never redone.
/// Library folder: MAP_DRIVE_LIBRARY_BUILD (default H:\MapLibrary). Batch size per catalog: --object-count (100).
/// --catalogs limits the catalogs; --dry-run only lists the next batches without writing anything.
/// </summary>
internal static class DriveLibraryBatch
{
    private sealed record PlanSet(string Key, string Catalog, string Name, string One, string Use,
        List<string> Collection, List<string> Tags, List<string> Files, bool Ready, List<int> Factor,
        string? Detail = null, bool Named = false, string? Source = null);

    /// <summary>The owner's reference folder: textures are imported from their originals (already 2048-4096 px).</summary>
    private const string RefsRoot = @"C:\Users\Leshe\Pictures\refs";
    private const string Refs2Root = @"C:\Users\Leshe\Pictures";

    private sealed class State
    {
        public List<string> Imported { get; set; } = [];
        public List<string> Checked { get; set; } = [];
        /// <summary>Checked sets that already carry the names reworked from the pictures.</summary>
        public List<string> Renamed { get; set; } = [];
        public int Batches { get; set; }
    }

    private static AssetSetImport ToImport(PlanSet set, string library, HashSet<string> trims, bool overwriteNames) => new()
    {
        StableKey = set.Key, Name = set.Name, Detail = set.Detail, OverwriteNames = overwriteNames,
        CollectionPath = set.Collection, Tags = set.Tags,
        Paths = [.. set.Files.Select(file => InputPath(set, file, library, trims))],
        Labels = [.. set.Files.Select((_, i) => set.Files.Count == 1 ? set.One : $"{set.One} {i + 1}")],
        Provenance = new AssetProvenance
        {
            Source = "owner-refs (test), upscaled x2/x4 Real-ESRGAN", Author = "",
            LicenseStatus = AssetLicenseStatus.Unknown, CommercialUseAllowed = false,
        },
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    // The page of a new map (DEC-078), not the old light paper.
    private const string Paper = CurrentLook.PageColor;

    public static IReadOnlyList<DriveLibraryReport> Run(PerformanceOptions options)
    {
        var library = Environment.GetEnvironmentVariable("MAP_DRIVE_LIBRARY_BUILD") is { Length: > 0 } configured ? configured : @"H:\MapLibrary";
        library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library));
        var plan = JsonSerializer.Deserialize<List<PlanSet>>(File.ReadAllBytes(Path.Combine(library, "plan", "sets.json")), Json)
            ?? throw new InvalidDataException("План наборов пуст.");
        var catalogs = plan.Select(s => s.Catalog).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var catalog in catalogs.Concat(options.Catalogs)) ValidateCatalog(catalog);
        var selected = options.Catalogs.Count > 0 ? options.Catalogs : catalogs;
        var exclusions = ReadOptionalFiles(Path.Combine(library, "plan", "exclusions.json"), cropBoxes: false);
        var trims = ReadOptionalFiles(Path.Combine(library, "plan", "trims.json"), cropBoxes: true);
        var reports = new List<DriveLibraryReport>();
        foreach (var catalog in selected)
        {
            var report = new DriveLibraryReport { Catalog = catalog, DryRun = options.DryRun };
            reports.Add(report);
            try
            {
                if (!catalogs.Contains(catalog, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"В плане нет каталога «{catalog}».");
                var catalogPlan = plan.Where(s => string.Equals(s.Catalog, catalog, StringComparison.OrdinalIgnoreCase)).ToList();
                var checks = Path.Combine(library, "checks");
                // Different catalogs never share a state/batch lock. A second writer of this catalog fails clearly.
                if (!options.DryRun) Directory.CreateDirectory(checks);
                using var lease = options.DryRun ? null : new FileStream(Path.Combine(checks, $"state-{catalog}.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                RunCatalog(options, library, catalogPlan, catalogs, exclusions, trims, report);
            }
            catch (Exception error) when (BatchError(error))
            {
                report.Errors.Add(error.Message);
                report.GatePassed = false;
            }
        }
        return reports;
    }

    private static void RunCatalog(PerformanceOptions options, string library, List<PlanSet> plan, List<string> catalogs,
        HashSet<string> exclusions, HashSet<string> trims, DriveLibraryReport report)
    {
        var statePath = Path.Combine(library, "checks", $"state-{report.Catalog}.json");
        var state = ReadState(statePath, Path.Combine(library, "checks", "state.json"), plan);
        var done = state.Checked.ToHashSet(StringComparer.Ordinal);
        var eligible = plan.Select(set => set with { Files = [.. set.Files.Where(file => !exclusions.Contains(NormalizeRel(file)))] })
            .Where(set => set.Files.Count > 0).ToList();
        report.Notes["excluded files"] = (plan.Sum(s => s.Files.Count) - eligible.Sum(s => s.Files.Count)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Sets checked before their pictures were looked at get the reworked names once (no new files).
        var renamed = state.Renamed.ToHashSet(StringComparer.Ordinal);
        var rename = eligible.Where(s => s.Named && done.Contains(s.Key) && !renamed.Contains(s.Key)).ToList();

        // The next sets whose every file is upscaled and verified and whose pictures were looked at.
        var batch = new List<PlanSet>();
        var fileCount = 0;
        foreach (var set in eligible)
        {
            if (done.Contains(set.Key) || !set.Ready || !set.Named || set.Tags.Contains("Не разобрано")) continue;
            batch.Add(set);
            fileCount += set.Files.Count;
            if (fileCount >= options.ObjectCount) break;
        }

        report.Batch = state.Batches + 1;
        report.Files = fileCount;
        report.Notes["earlier sets to rename"] = rename.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var set in batch)
        {
            var check = new DriveLibrarySetCheck { Key = set.Key, Name = set.Name, Catalog = report.Catalog };
            report.Sets.Add(check);
            try { check.Inputs.AddRange(set.Files.Select(file => InputPath(set, file, library, trims))); }
            catch (Exception error) when (BatchError(error)) { check.Errors.Add(error.Message); }
        }
        if (options.DryRun)
        {
            report.GatePassed = report.Sets.All(s => s.Errors.Count == 0);
            report.Errors.AddRange(report.Sets.SelectMany(s => s.Errors.Select(e => $"{s.Key}: {e}")));
            return;
        }

        // The first catalog state is a one-time, filtered copy of the legacy keys. The global file stays read-only
        // so that other catalogs can migrate independently. New catalog-local batch numbering starts at one.
        WriteJson(statePath, state);
        var output = Path.Combine(library, "checks", report.Catalog);
        Directory.CreateDirectory(output);
        if (rename.Count > 0)
        {
            try
            {
                var catalog = new AssetCatalog(Path.Combine(library, report.Catalog));
                catalog.ImportSets([.. rename.Select(set => ToImport(set, library, trims, overwriteNames: true))], DateTimeOffset.UtcNow);
                catalog.FlushMetadataAsync().GetAwaiter().GetResult();
                state.Renamed.AddRange(rename.Select(s => s.Key));
            }
            catch (Exception error) when (BatchError(error)) { report.Errors.Add($"Переименование: {error.Message}"); }
        }
        if (batch.Count == 0)
        {
            WriteJson(statePath, state);
            report.Notes["result"] = "nothing ready";
            report.GatePassed = report.Errors.Count == 0;
            if (!report.GatePassed) WriteJson(Path.Combine(output, $"batch-{report.Batch:D4}.json"), report);
            return;
        }

        var rows = new List<SKImage>();
        try
        {
            // 1. One fast bulk import on the happy path; split a failed transaction to identify the bad sets.
            var catalog = new AssetCatalog(Path.Combine(library, report.Catalog));
            ImportBatch(batch.Where(s => report.Sets.Single(c => c.Key == s.Key).Errors.Count == 0).ToList(), catalog,
                library, trims, state, report);
            EnsureMarker(library, catalogs);

            // 2. Connection: attach only this catalog, never open another builder's catalog for a check.
            var ownRoot = Path.Combine(output, $".connection-{Guid.NewGuid():N}");
            var own = new AssetCatalog(ownRoot);
            try
            {
                // The marker is read exactly as the program's drive scan reads it.
                var found = AssetDriveLibraries.TryRead(library) ?? throw new InvalidOperationException("Метка библиотеки на диске не читается.");
                var catalogRoot = Path.GetFullPath(Path.Combine(library, report.Catalog));
                own.SetMounts([.. AssetDriveLibraries.CatalogRoots([found]).Where(root => string.Equals(root, catalogRoot, StringComparison.OrdinalIgnoreCase))]);
                report.Notes["attached catalogs"] = own.Mounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                report.Notes["cards on the drive"] = own.Sets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                report.Notes["test cards found by tag"] = own.Query(new AssetQuery { Tag = "Тестовое" }).TotalSets.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var document = new MapDocument(DocumentId.New(), "Проверка ассетов", DateTimeOffset.UnixEpoch,
                    new CanvasDefinition(4000, 4000, "px", Paper), [new BaseColorLayer(LayerId.New(), "Бумага", true, false, 1, Paper)], []);
                using var images = new CatalogStampImageSource(own, () => document, () => null);
                foreach (var set in batch)
                {
                    var check = report.Sets.Single(c => c.Key == set.Key);
                    if (check.Errors.Count > 0) continue;
                    try
                    {
                        var card = own.FindSet(AssetIdentity.SetIdForKey(set.Key));
                        check.Found = card is not null;
                        if (card is null) continue;
                        var records = card.VariantIds.Select(own.RecordForVariant).OfType<AssetRecord>().ToList();
                        check.Variants = card.VariantIds.Count;
                        check.Readable = records.Count(r => own.ReadVerified(r).Status == AssetResolutionStatus.Available);
                        check.Thumbnails = records.Count(r => own.TryThumbnailPath(r, out var t) && File.Exists(t));
                        check.Sizes = string.Join(", ", records.Take(3).Select(r => $"{r.Width}×{r.Height}"));
                        if (records.Count == 0) { check.Errors.Add("Нет читаемых вариантов."); continue; }
                        // Keep five variants on the sheet, but actually render all the remaining variants too.
                        foreach (var chunk in records.Skip(5).Chunk(5))
                        {
                            using var extra = RenderRow(set, [.. chunk], own, images, check);
                        }
                        rows.Add(RenderRow(set, [.. records.Take(5)], own, images, check));
                    }
                    catch (Exception error) when (BatchError(error)) { check.Errors.Add(error.Message); }
                }

                if (rows.Count > 0) report.Images.Add(SaveSheet(rows, Path.Combine(output, $"batch-{report.Batch:D4}.jpg")));
            }
            finally
            {
                try { own.SetMounts([]); own.FlushMetadataAsync().GetAwaiter().GetResult(); }
                finally { if (Directory.Exists(ownRoot)) Directory.Delete(ownRoot, recursive: true); }
            }
        }
        catch (Exception error) when (BatchError(error)) { report.Errors.Add(error.Message); }
        finally
        {
            foreach (var row in rows) row.Dispose();
        }

        report.GatePassed = report.Errors.Count == 0 && report.Sets.All(s => s.Passed);
        if (report.Errors.Count == 0) state.Checked.AddRange(report.Sets.Where(s => s.Passed).Select(s => s.Key));
        // Failed attempts also keep their reports; retrying a failed set must not overwrite the evidence.
        state.Batches = report.Batch;
        WriteJson(Path.Combine(output, $"batch-{report.Batch:D4}.json"), report);
        WriteJson(statePath, state);
    }

    private static void ImportBatch(List<PlanSet> batch, AssetCatalog catalog, string library, HashSet<string> trims,
        State state, DriveLibraryReport report)
    {
        if (batch.Count == 0) return;
        try
        {
            catalog.ImportSets([.. batch.Select(set => ToImport(set, library, trims, overwriteNames: false))], DateTimeOffset.UtcNow);
            catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) when (BatchError(error))
        {
            if (batch.Count == 1) report.Sets.Single(s => s.Key == batch[0].Key).Errors.Add($"Импорт: {error.Message}");
            else
            {
                var middle = batch.Count / 2;
                ImportBatch(batch.GetRange(0, middle), catalog, library, trims, state, report);
                ImportBatch(batch.GetRange(middle, batch.Count - middle), catalog, library, trims, state, report);
            }
            return;
        }
        state.Imported.AddRange(batch.Select(s => s.Key).Where(key => !state.Imported.Contains(key)));
        state.Renamed.AddRange(batch.Select(s => s.Key).Where(key => !state.Renamed.Contains(key)));
    }

    private static State ReadState(string path, string legacyPath, List<PlanSet> plan)
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<State>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException($"Не читается состояние: {path}");
        if (!File.Exists(legacyPath)) return new State();
        var legacy = JsonSerializer.Deserialize<State>(File.ReadAllBytes(legacyPath), Json)
            ?? throw new InvalidDataException("Не читается старое состояние.");
        var keys = plan.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        return new State
        {
            Imported = [.. legacy.Imported.Where(keys.Contains).Distinct(StringComparer.Ordinal)],
            Checked = [.. legacy.Checked.Where(keys.Contains).Distinct(StringComparer.Ordinal)],
            Renamed = [.. legacy.Renamed.Where(keys.Contains).Distinct(StringComparer.Ordinal)],
        };
    }

    private static string NormalizeRel(string file)
    {
        var rel = file.Replace('\\', '/').Trim();
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        return rel;
    }

    private static string InputPath(PlanSet set, string file, string library, HashSet<string> trims)
    {
        var rel = file.Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.Split('/').Any(part => part is ".." or "") || rel.Contains(':'))
            throw new InvalidDataException($"Путь должен быть относительным: {file}");
        rel = NormalizeRel(rel).Replace('/', Path.DirectorySeparatorChar);
        if (trims.Contains(NormalizeRel(file)))
        {
            var trimmed = Path.Combine(library, "trimmed", Path.ChangeExtension(rel, ".webp"));
            if (File.Exists(trimmed)) return trimmed;
            // Accept producers which append .webp to the original relative name as well.
            trimmed = Path.Combine(library, "trimmed", rel + ".webp");
            if (File.Exists(trimmed)) return trimmed;
        }
        if (set.Source == "refs") return Path.Combine(RefsRoot, rel);
        // refs2 rels already include "refs2/". Only textures read originals; shop previews require x4 output.
        if (set.Source == "refs2" && set.Catalog == "textures-2") return Path.Combine(Refs2Root, rel);
        return Path.Combine(library, "upscaled", Path.ChangeExtension(rel, ".webp"));
    }

    private static HashSet<string> ReadOptionalFiles(string path, bool cropBoxes)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return files;
        using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        });
        if (!TryProperty(json.RootElement, "files", out var entries) || entries.ValueKind != JsonValueKind.Array) return files;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryProperty(entry, "rel", out var rel) || rel.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rel.GetString())) continue;
            if (cropBoxes)
            {
                if (!TryProperty(entry, "box", out var box) || box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) continue;
                var coords = box.EnumerateArray().ToArray();
                if (coords.Any(c => c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out _))) continue;
                if (coords[0].GetInt32() < 0 || coords[1].GetInt32() < 0 || coords[2].GetInt32() <= coords[0].GetInt32()
                    || coords[3].GetInt32() <= coords[1].GetInt32()) continue;
            }
            files.Add(NormalizeRel(rel.GetString()!));
        }
        return files;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static void ValidateCatalog(string catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog) || catalog is "." or ".." || catalog.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || catalog != catalog.Trim() || catalog.EndsWith('.'))
            throw new ArgumentException($"Недопустимое имя каталога: «{catalog}».");
    }

    private sealed record DriveMarker(string Format, int Version, string Id, string Name, List<string> Catalogs);

    private static void EnsureMarker(string library, List<string> catalogs)
    {
        // A short OS mutex protects the only shared file. List ALL planned catalogs on the first write,
        // including those not yet created: drive discovery ignores absent catalog.json files until they exist.
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(library.ToUpperInvariant())));
        using var mutex = new Mutex(false, $"Map.DriveLibrary.Marker.{identity}");
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("Занята метка библиотеки."); }
        catch (AbandonedMutexException) { } // the previous writer died; the atomic marker is still readable
        try
        {
            var path = Path.Combine(library, AssetDriveLibraries.MarkerFileName);
            var marker = File.Exists(path) ? JsonSerializer.Deserialize<DriveMarker>(File.ReadAllBytes(path), Json) : null;
            if (marker is not null && (marker.Format != AssetDriveLibraries.Format || marker.Version != 1
                || string.IsNullOrWhiteSpace(marker.Id) || marker.Catalogs is null))
                throw new InvalidDataException("Неверная метка библиотеки.");
            var names = (marker?.Catalogs ?? []).Concat(catalogs)
                .Concat(Directory.GetDirectories(library).Where(dir => File.Exists(Path.Combine(dir, "catalog.json"))).Select(dir => Path.GetFileName(dir)!))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (marker is not null && marker.Catalogs.SequenceEqual(names)) return;
            WriteJson(path, new
            {
                format = AssetDriveLibraries.Format, version = 1, id = marker?.Id ?? "owner-refs-t7",
                name = marker?.Name ?? "Тестовые ассеты (диск T7)", catalogs = names,
            });
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool BatchError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or JsonException or InvalidDataException;

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

        var info = new SKImageInfo(Thumb + Strip + Close + 40, Height + Label);
        using var surface = SKSurface.Create(info);
        var c2 = surface.Canvas;
        c2.Clear(new SKColor(46, 40, 34));
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 17);
        using var text = new SKPaint { Color = check.Passed ? new SKColor(236, 226, 200) : new SKColor(255, 120, 100), IsAntialias = true };
        c2.DrawText($"{(check.Passed ? "✓" : "✗")} {set.Name}  —  {set.Detail ?? "без подробности"}  ·  {set.Catalog}  ·  вариантов {check.Variants}  ·  ×{factor}", 8, 21, font, text);
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
