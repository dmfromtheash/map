using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Presentation.Services;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    private static bool PhaseEvidenceEnabled => Environment.GetEnvironmentVariable("MAP_PERF_PHASE_EVIDENCE") == "1";

    // PrintWindow and PNG encoding stop the UI thread. Replay the visual route after
    // timing routes, in the same native window, and never include it in FPS statistics.
    private async Task CapturePhaseEvidence(ZoomScenarioReport scenario, double fit)
    {
        var folder = Environment.GetEnvironmentVariable("MAP_PERF_SHOTS") ?? Path.GetDirectoryName(Options.OutputPath)!;
        Directory.CreateDirectory(folder);
        var canvas = _view.Canvas;
        var layer = _editor.Session.Document.Layers.OfType<ObjectLayer>().Last();
        var cells = new int[1024];
        foreach (var obj in layer.Objects)
        {
            var x = Math.Clamp((int)((obj.X + obj.Width / 2) * 32 / scenario.CanvasWidth), 0, 31);
            var y = Math.Clamp((int)((obj.Y + obj.Height / 2) * 32 / scenario.CanvasHeight), 0, 31);
            cells[y * 32 + x]++;
        }
        var regions = new[] { ("dense", Array.IndexOf(cells, cells.Max())), ("sparse", Array.IndexOf(cells, cells.Min())) };
        var rows = new List<Dictionary<string, object?>>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        _report.Environment["phase-evidence"] = "Separate visual replay AFTER timed routes, same window; 5 scales x 2 histogram regions x 6 phases; lossless PNG zlib=0; exact-file SHA256 deduplication. Timing metrics exclude this replay.";
        _report.Environment["phase-evidence-start-qpc"] = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture);
        foreach (var (region, cell) in regions)
        foreach (var (label, scale) in new[] { ("fit", fit), ("25", .25), ("100", 1d), ("400", 4d), ("1600", 16d) })
        {
            var anchor = ((cell % 32 + .5) * scenario.CanvasWidth / 32, (cell / 32 + .5) * scenario.CanvasHeight / 32);
            await Step(() => SetScale(anchor, scale));
            var clock = Stopwatch.StartNew();
            Capture("before");
            for (var i = 0; i < 60; i++)
            {
                canvas.PanByScreenDeltaForSmoke(i < 30 ? -3 : 3, 0);
                if (i == 29) Capture("during");
                await Task.Delay(16);
            }
            await Step(() => { clock.Restart(); canvas.PanByScreenDeltaForSmoke(1, 0); });
            Capture("immediate");
            await Until(5000); Capture("after5s");
            await Until(30000); Capture("after30s");
            var wait = Stopwatch.StartNew();
            while (!Ready() && wait.Elapsed.TotalSeconds < 180) await Task.Delay(25);
            var ready = Ready();
            Capture(ready ? "settled" : "settled-timeout");
            _report.Checks[$"phase/{region}/{label}/settled"] = ready;
            Console.WriteLine($"phase {region}/{label}: ready={ready}, after input={clock.Elapsed.TotalSeconds:0.0}s");

            async Task Until(double milliseconds)
            {
                var remaining = milliseconds - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            }
            bool Ready()
            {
                var stats = canvas.LastRenderStats;
                if (canvas.LastFrameFastTerrain || canvas.LastTerrainRefinementPending
                    || stats.DetailTilesPending != 0 || stats.UnresolvedImages != 0) return false;
                if (_editor.StampImages is not CatalogStampImageSource images) return true;
                if (images.ImageRequestsPending) return false;
                return images.TryReadPerformanceQueues(out var queues)
                    && queues.Metadata == 0 && queues.Builds == 0 && queues.Pixels == 0 && queues.LatePixels == 0;
            }
            void Capture(string phase)
            {
                var diagnostic = new ZoomRouteReport { Name = "phase-snapshot" };
                CaptureRefinementState(diagnostic);
                var name = $"phase-{region}-{label}-{phase}.png";
                var path = Path.Combine(folder, name);
                var row = new Dictionary<string, object?>
                {
                    ["requestedFile"] = name, ["region"] = region, ["zoom"] = label, ["phase"] = phase,
                    ["scale"] = _editor.Session.Viewport.Scale, ["anchorX"] = anchor.Item1, ["anchorY"] = anchor.Item2,
                    ["sinceLastInputMs"] = phase is "before" or "during" ? null : clock.Elapsed.TotalMilliseconds,
                    ["qpc"] = Stopwatch.GetTimestamp(), ["revision"] = canvas.CompletedSceneRevision,
                    ["tier"] = canvas.LastLodTier.ToString(), ["fast"] = canvas.LastFrameFastTerrain,
                    ["workMs"] = Map.App.Controls.MapCanvasControl.LastFrameWorkMilliseconds,
                    ["counters"] = diagnostic.RefinementState, ["cache"] = diagnostic.ImageCacheAfterRefinement,
                    ["visualReview"] = "NOT CHECKED: inspect PNG before accepting",
                };
                CaptureBlurWindow(path);
                using (var stream = File.OpenRead(path))
                {
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    row["sha256"] = hash;
                    row["file"] = hashes.TryGetValue(hash, out var previous) ? previous : name;
                    row["duplicate"] = previous is not null;
                    if (previous is null) { hashes.Add(hash, name); _report.Frames.Add(path); }
                }
                if ((bool)row["duplicate"]!) File.Delete(path); // exact owned duplicate, original remains in index
                row["revisionAfterCapture"] = canvas.CompletedSceneRevision;
                rows.Add(row);
                File.WriteAllText(Path.Combine(folder, "phases.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                var index = new StringBuilder("# Живые фазы — отдельный визуальный повтор\n\nНастоящее окно/холст/GPU. После измеряемых маршрутов; эти снимки не входят в FPS. PNG без потерь, zlib=0. Дубликаты совпадают по SHA256 всего файла. Счётчики до захвата; изменение revision видно в JSON. Снимки требуют просмотра глазами.\n\n|Файл|Участок / масштаб / фаза|Detail / unresolved / pixels|Tier|Работа, мс|Дубликат|\n|---|---|---|---|---|---|\n");
                foreach (var item in rows)
                {
                    var c = (Dictionary<string, long>)item["counters"]!;
                    index.AppendLine($"|[{item["file"]}]({item["file"]})|{item["region"]}/{item["zoom"]}/{item["phase"]}|{c.GetValueOrDefault("detailTilesPending")}/{c.GetValueOrDefault("unresolvedImages")}/{c.GetValueOrDefault("pixelQueue")}|{item["tier"]}|{item["workMs"]}|{item["duplicate"]}|");
                }
                File.WriteAllText(Path.Combine(folder, "index.md"), index.ToString());
            }
        }
        _report.Environment["phase-evidence-end-qpc"] = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture);
        _report.Checks["phase/60-indexed-captures"] = rows.Count == 60;
    }
}
