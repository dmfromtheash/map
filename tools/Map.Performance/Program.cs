using System.Text.Json;
using Map.Performance;

var options = PerformanceOptions.Parse(args);
object report;
bool gatePassed;
if (options.Profile == "native-stage17")
{
    var nativeReport = NativeStage17Smoke.Run(options); report = nativeReport; gatePassed = nativeReport.GatePassed;
}
else if (options.Profile is "native-placement-workflow" or "native-placement-baseline" or "native-placement-ux")
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
