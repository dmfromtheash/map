using System.Text.Json;
using Map.Performance;

var options = PerformanceOptions.Parse(args);
object report;
bool gatePassed;
if (options.Profile is "native-stage13a" or "native-stage13b" or "native-stage13c" or "native-stage13d")
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

Console.WriteLine($"Stage 13 report: {options.OutputPath}");
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

return gatePassed ? 0 : 2;
