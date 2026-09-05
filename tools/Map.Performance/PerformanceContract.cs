using System.Globalization;

namespace Map.Performance;

internal sealed record PerformanceOptions(
    string Profile,
    int ObjectCount,
    int Seed,
    int WarmupIterations,
    int MeasuredIterations,
    string OutputPath)
{
    public static PerformanceOptions Parse(string[] args)
    {
        var profile = "stage13a";
        var objectCount = 50_000;
        var seed = 13_013;
        var warmup = 1;
        var iterations = 5;
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            string Value(string name)
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException($"{name} requires a value.");
                }

                return args[index];
            }

            switch (args[index])
            {
                case "--profile":
                    profile = Value("--profile");
                    break;
                case "--object-count":
                    objectCount = int.Parse(Value("--object-count"), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = int.Parse(Value("--seed"), CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    warmup = int.Parse(Value("--warmup"), CultureInfo.InvariantCulture);
                    break;
                case "--iterations":
                    iterations = int.Parse(Value("--iterations"), CultureInfo.InvariantCulture);
                    break;
                case "--output":
                    output = Value("--output");
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        var normalizedProfile = profile.Trim().ToLowerInvariant();
        var isLargestMapStress = normalizedProfile is
            "native-largest-map-stress" or "native-largest-map-assets-only";
        var minimumObjectCount = isLargestMapStress ? 4 : 1;
        var maximumObjectCount = isLargestMapStress
            ? int.MaxValue
            : 100_000;
        if (objectCount < minimumObjectCount || objectCount > maximumObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(objectCount),
                $"The {normalizedProfile} profile accepts "
                + $"{minimumObjectCount:N0}..{maximumObjectCount:N0} logical objects.");
        }
        if (warmup < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(warmup), "At least one warmup is required.");
        }
        if (iterations < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations),
                "Stage 13 requires at least five measured iterations.");
        }

        output ??= Path.Combine(
            FindRepositoryRoot(Environment.CurrentDirectory),
            "tmp",
            "performance",
            $"stage13-{profile}-{objectCount}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

        return new PerformanceOptions(
            normalizedProfile,
            objectCount,
            seed,
            warmup,
            iterations,
            Path.GetFullPath(output));
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Map.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Map.slnx was not found above the current directory.");
    }
}

internal sealed record FixtureManifest(
    string Name,
    int Seed,
    int ObjectCount,
    int RectangleCount,
    int GroupCount,
    int GroupedObjectCount,
    int CanvasWidth,
    int CanvasHeight,
    string Hash)
{
    public IReadOnlyDictionary<string, int> KindCounts { get; init; } =
        new Dictionary<string, int>();

    public int ExpensiveEffectCount { get; init; }

    public int MaterializedMaskTileCount { get; init; }
}

internal sealed record MachineFingerprint(
    string Root,
    string Branch,
    string Head,
    bool Dirty,
    string Runtime,
    string Os,
    string Architecture,
    string Processor,
    int ProcessorCount,
    string RendererBackend);

internal sealed record Sample(
    double ElapsedMs,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    long WorkingSetBefore,
    long WorkingSetAfter);

internal sealed record MetricSummary(
    double P50,
    double P95,
    double Max,
    long AllocationP50,
    long AllocationP95,
    int Gen0,
    int Gen1,
    int Gen2,
    IReadOnlyList<Sample> Samples)
{
    public static MetricSummary From(IReadOnlyList<Sample> samples)
    {
        var byTime = samples.OrderBy(sample => sample.ElapsedMs).ToArray();
        var byAllocation = samples.OrderBy(sample => sample.AllocatedBytes).ToArray();
        return new MetricSummary(
            Percentile(byTime, 0.50).ElapsedMs,
            Percentile(byTime, 0.95).ElapsedMs,
            byTime[^1].ElapsedMs,
            Percentile(byAllocation, 0.50).AllocatedBytes,
            Percentile(byAllocation, 0.95).AllocatedBytes,
            samples.Sum(sample => sample.Gen0),
            samples.Sum(sample => sample.Gen1),
            samples.Sum(sample => sample.Gen2),
            samples);
    }

    private static Sample Percentile(IReadOnlyList<Sample> ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }
}

internal sealed record ProfileResult(
    string Name,
    string Gate,
    bool Passed,
    MetricSummary Timing,
    IReadOnlyDictionary<string, object?> Counters);

internal sealed record PerformanceReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    PerformanceOptions Options,
    MachineFingerprint Machine,
    FixtureManifest Fixture,
    IReadOnlyList<ProfileResult> Profiles,
    bool GatePassed,
    IReadOnlyList<string> Limitations);
