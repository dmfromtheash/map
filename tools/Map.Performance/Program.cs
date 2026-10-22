using System.Text.Json;
using Map.Performance;

var options = PerformanceOptions.Parse(args);
object report;
bool gatePassed;
if (options.Profile == "regional-terrain-probe")
{
    report = RegionalTerrainProbe.Run(); gatePassed = true;
}
else if (options.Profile == "regional-art-probe")
{
    report = RegionalArtProbe.Run(options); gatePassed = true;
}
else if (options.Profile is "native-atlas" or "native-regional-art" or "native-regional" or "native-regional-reopen" or "native-regional-surfaces" or "native-regional-finish" or "native-area-explore" or "native-asset-opacity" or "native-editor-defaults" or "native-map-categories" or "native-hex-category" or "native-hex-workshop" or "native-hex-panel-shots" or "native-player-package" or "native-export-wins" or "native-library-wins" or "native-poster-export")
{
    var smoke = NativeRegionalSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile is "native-mountain-foundation" or "native-stamp-family")
{
    var smoke = NativeMountainFoundationSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
}
else if (options.Profile == "install-mountain-foundation")
{
    var install = MountainFoundationPack.Install(); report = install; gatePassed = install.GatePassed;
}
else if (options.Profile == "native-stress-fill")
{
    var smoke = NativeStressFillSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-stress-fill: routes={smoke.Routes.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile == "native-paint-perf")
{
    var smoke = NativePaintPerfSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-paint-perf: routes={smoke.Routes.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile == "native-zoom-assets")
{
    var smoke = NativeZoomAssetsSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-zoom-assets: scenarios={smoke.Scenarios.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile is "native-asset-catalog" or "native-empty-artwork")
{
    var smoke = NativeAssetCatalogSmoke.Run(options);
    report = smoke; gatePassed = smoke.GatePassed;
}
else if (options.Profile == "native-render-audit")
{
    var audit = NativeRenderAuditSmoke.Run(options); report = audit; gatePassed = audit.GatePassed;
    Console.WriteLine($"native-render-audit: checks={audit.Checks.Count(c => c.Value)}/{audit.Checks.Count}, gate={(audit.GatePassed ? "PASS" : "FAIL")}");
    foreach (var note in audit.Notes) Console.WriteLine($"  {note.Key} = {note.Value}");
    if (audit.Failure is not null) Console.WriteLine(audit.Failure);
}
else if (options.Profile == "native-giant-export")
{
    var giant = NativeGiantExport.Run(options); report = giant; gatePassed = giant.GatePassed;
}
else if (options.Profile == "native-area-rules")
{
    var smoke = NativeAreaRulesSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-area-rules: checks={smoke.Checks.Count(c => c.Value)}/{smoke.Checks.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    foreach (var check in smoke.Checks.Where(c => !c.Value)) Console.WriteLine($"  FAILED {check.Key}");
    foreach (var note in smoke.Notes) Console.WriteLine($"  {note.Key} = {note.Value}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile == "native-decorations")
{
    var smoke = NativeDecorationsSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-decorations: checks={smoke.Checks.Count(c => c.Value)}/{smoke.Checks.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    foreach (var check in smoke.Checks.Where(c => !c.Value)) Console.WriteLine($"  FAILED {check.Key}");
    foreach (var note in smoke.Notes) Console.WriteLine($"  {note.Key} = {note.Value}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile == "native-text-on-path")
{
    var smoke = NativeTextOnPathSmoke.Run(options); report = smoke; gatePassed = smoke.GatePassed;
    Console.WriteLine($"native-text-on-path: checks={smoke.Checks.Count(c => c.Value)}/{smoke.Checks.Count}, gate={(smoke.GatePassed ? "PASS" : "FAIL")}");
    foreach (var check in smoke.Checks.Where(c => !c.Value)) Console.WriteLine($"  FAILED {check.Key}");
    if (smoke.Failure is not null) Console.WriteLine(smoke.Failure);
}
else if (options.Profile is "native-stage20" or "native-crossings")
{
    var nativeReport = NativeStage20Smoke.Run(options); report = nativeReport; gatePassed = nativeReport.GatePassed;
}
else if (options.Profile is "native-stage19" or "native-land-materials" or "native-soft-coast" or "native-history-memory" or "native-panel-shots" or "native-standard-panel-shots" or "native-ui-shots" or "native-color-presets" or "native-vtt-export" or "native-grid-types" or "native-versions" or "native-room" or "native-land-cliffs" or "native-owner-art")
{
    var nativeReport = NativeStage19Smoke.Run(options); report = nativeReport; gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage18")
{
    var nativeReport = NativeStage18Smoke.Run(options); report = nativeReport; gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage17")
{
    var nativeReport = NativeStage17Smoke.Run(options); report = nativeReport; gatePassed = nativeReport.GatePassed;
}
else if (options.Profile is "native-placement-workflow" or "native-placement-baseline" or "native-placement-ux" or "native-placement-overlap")
{
    var placementReport = NativePlacementWorkflowSmoke.Run(options);
    report = placementReport;
    gatePassed = placementReport.GatePassed;
}
else if (options.Profile == "native-brush-stress")
{
    var stressReport = NativeBrushStressSmoke.Run(options);
    report = stressReport;
    gatePassed = stressReport.GatePassed;
}
else if (options.Profile is "native-largest-map-stress" or "native-largest-map-assets-only")
{
    var stressReport = NativeLargestMapStressSmoke.Run(options);
    report = stressReport;
    gatePassed = stressReport.GatePassed;
}
else if (options.Profile == "native-editor-integrity")
{
    var nativeReport = NativeEditorIntegritySmoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-object-workflow")
{
    var nativeReport = NativeObjectWorkflowSmoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage16")
{
    var nativeReport = NativeStage16Smoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage15")
{
    var nativeReport = NativeStage15Smoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage14")
{
    var nativeReport = NativeStage14Smoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile == "native-stage12d")
{
    var nativeReport = NativeStage12DSmoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else if (options.Profile is "native-stage13a" or "native-stage13b" or "native-stage13c" or "native-stage13d")
{
    var nativeReport = NativeStage13ASmoke.Run(options);
    report = nativeReport;
    gatePassed = nativeReport.GatePassed;
}
else
{
    var performanceReport = new Stage13PerformanceRunner(options).Run();
    report = performanceReport;
    gatePassed = performanceReport.GatePassed;
}

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
});

var outputDirectory = Path.GetDirectoryName(options.OutputPath);
if (!string.IsNullOrWhiteSpace(outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
}
File.WriteAllText(options.OutputPath, json + Environment.NewLine);

Console.WriteLine($"Performance report: {options.OutputPath}");
if (report is PerformanceReport performance)
{
    foreach (var profile in performance.Profiles)
    {
        Console.WriteLine(
            $"{profile.Name}: P50={profile.Timing.P50:F3} ms, " +
            $"P95={profile.Timing.P95:F3} ms, max={profile.Timing.Max:F3} ms, " +
            $"alloc P95={profile.Timing.AllocationP95:N0} B, " +
            $"gate={(profile.Passed ? "PASS" : "FAIL")}");
    }
}
else if (report is NativeStage13AReport native)
{
    Console.WriteLine(
        $"{options.Profile}: realized max={native.ObjectTree.MaxRealizedRows}, " +
        $"drag P95={native.Drag.InputToRenderP95:F3} ms, " +
        $"overview P95={(native.Lod?.OverviewP95.ToString("F3") ?? "n/a")} ms, " +
        $"tree rebuilds during pointer updates={native.Drag.PointerMoveTreeRebuilds}, " +
        $"gate={(native.GatePassed ? "PASS" : "FAIL")}");
}
else if (report is NativeStage14Report stage14)
{
    Console.WriteLine(
        $"native-stage14: ghost={stage14.GhostWasVisible}, "
        + $"pointer tree rebuilds={stage14.PointerMoveObjectTreeRebuilds}, "
        + $"primary reopen={stage14.PrimaryAutosaveReopenedLatestRevision}, "
        + $"gate={(stage14.GatePassed ? "PASS" : "FAIL")}");
}
else if (report is NativeStage15Report stage15)
{
    Console.WriteLine(
        $"native-stage15: compact={stage15.CompactCatalogReady}, "
        + $"ghost={stage15.GhostVisibleAndTransient}, "
        + $"save/export={stage15.SaveReopenAndExactExportMatched}, "
        + $"gate={(stage15.GatePassed ? "PASS" : "FAIL")}");
}
else if (report is NativeEditorIntegrityReport integrity)
{
    foreach (var pair in integrity.Scenarios)
    {
        Console.WriteLine($"  {(pair.Value ? "PASS" : "FAIL")}  {pair.Key}");
    }

    Console.WriteLine($"native-editor-integrity: gate={(integrity.GatePassed ? "PASS" : "FAIL")}");
}
else if (report is NativeStage16Report stage16)
{
    Console.WriteLine(
        $"native-stage16: dashboard={stage16.DashboardProjectFlowWorked}, "
        + $"assets={stage16.RasterAndSafeSvgImported}, "
        + $"edit={stage16.ConnectedEditingFlowWorked}, "
        + $"save/export={stage16.SaveReopenAndExportWorked}, "
        + $"responsive={stage16.ResponsiveEditorSurfacesWorked}, "
        + $"gate={(stage16.GatePassed ? "PASS" : "FAIL")}");
}
else if (report is NativeLargestMapStressReport largest)
{
    Console.WriteLine(
        $"native-largest-map-stress: objects={largest.PlacedObjects:N0}/{largest.TargetObjects:N0}, "
        + $"land tiles={largest.LandTiles:N0}, input-to-render P95={largest.OverviewP95:F3} ms, "
        + $"scene P95={largest.SceneRenderP95:F3} ms, "
        + $"private peak={largest.PeakPrivateBytes / 1024d / 1024d:F1} MiB, "
        + $"save={(largest.SaveSucceeded ? "PASS" : "REFUSED")}, "
        + $"gate={(largest.GatePassed ? "PASS" : "FAIL")}");
}

return gatePassed ? 0 : 2;
