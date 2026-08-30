using System.Globalization;

namespace Map.Benchmarks;

internal sealed record BenchmarkOptions(
    string? OutputPath,
    string ScratchPath,
    int Seed,
    int WarmupIterations,
    int MeasuredSamples,
    IReadOnlySet<string> Profiles)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string? output = null;
        var scratch = Path.Combine(Path.GetTempPath(), $"map-stage11-benchmark-{Guid.NewGuid():N}");
        var seed = 110011;
        var warmup = 1;
        var samples = 5;
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "all" };

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
                case "--output":
                    output = Value("--output");
                    break;
                case "--scratch":
                    scratch = Value("--scratch");
                    break;
                case "--seed":
                    seed = int.Parse(Value("--seed"), CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    warmup = int.Parse(Value("--warmup"), CultureInfo.InvariantCulture);
                    break;
                case "--samples":
                    samples = int.Parse(Value("--samples"), CultureInfo.InvariantCulture);
                    break;
                case "--profiles":
                    profiles = Value("--profiles")
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option: {args[index]}");
            }
        }

        if (warmup != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(warmup), "Stage 11 uses exactly one warmup iteration.");
        }
        if (samples < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Stage 11 requires at least five measured samples.");
        }
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one profile must be selected.");
        }

        return new BenchmarkOptions(
            output,
            Path.GetFullPath(scratch),
            seed,
            warmup,
            samples,
            profiles);
    }

    public bool Includes(string profile) => Profiles.Contains("all") || Profiles.Contains(profile);
}

internal sealed record MachineFingerprint(
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string Processor,
    int ProcessorCount,
    long TotalAvailableMemoryBytes,
    string Framework,
    bool ServerGc,
    long StopwatchFrequency,
    string Renderer,
    string SkiaAssemblyVersion,
    string GitCommit,
    bool GitDirty);

internal sealed record SampleMeasurement(
    double ElapsedMs,
    long AllocatedBytes,
    long ManagedHeapBytes,
    long WorkingSetBytes,
    long NativeEstimateBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal sealed record MetricSummary(
    string Unit,
    IReadOnlyList<double> Raw,
    double Median,
    double P95,
    double Min,
    double Max,
    double? Budget,
    string? BudgetRule,
    bool? Passed)
{
    public static MetricSummary From(
        IEnumerable<double> values,
        string unit,
        double? budget = null,
        string? budgetRule = null,
        Func<IReadOnlyList<double>, bool>? gate = null)
    {
        var ordered = values.Select(value => Math.Round(value, 3)).Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("A metric requires at least one sample.", nameof(values));
        }
        return new MetricSummary(
            unit,
            ordered,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            ordered[0],
            ordered[^1],
            budget,
            budgetRule,
            gate?.Invoke(ordered));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }
}

internal sealed record ProfileResult(
    string Name,
    string Boundary,
    IReadOnlyDictionary<string, object?> Dimensions,
    IReadOnlyDictionary<string, MetricSummary> Metrics,
    IReadOnlyDictionary<string, object?> Memory,
    IReadOnlyDictionary<string, object?> Evidence,
    bool GatePassed,
    string Gate);

internal sealed record Stage11BenchmarkReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    int Seed,
    int WarmupIterations,
    int MeasuredSamples,
    string ColdWarmContract,
    MachineFingerprint Machine,
    IReadOnlyList<ProfileResult> Profiles,
    bool GatePassed,
    IReadOnlyList<string> Limitations);
