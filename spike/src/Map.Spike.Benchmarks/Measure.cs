using System.Diagnostics;

namespace Map.Spike.Benchmarks;

/// <summary>
/// Timing helper. Reports median and P95 rather than a mean, because a canvas is judged on its
/// slow frames: an average hides exactly the stutter a user notices.
/// </summary>
public sealed class Measure(string name, int warmupIterations)
{
    private readonly List<double> _samplesMs = [];

    public string Name { get; } = name;
    public int WarmupIterations { get; } = warmupIterations;

    public int SampleCount => _samplesMs.Count;

    public static Measure Run(string name, int warmup, int iterations, Action<int> body)
    {
        var measure = new Measure(name, warmup);

        for (var i = 0; i < warmup; i++)
        {
            body(i);
        }

        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            body(i);
            measure._samplesMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return measure;
    }

    public void Add(double milliseconds) => _samplesMs.Add(milliseconds);

    public double Min => Percentile(0);
    public double Median => Percentile(0.50);
    public double P95 => Percentile(0.95);
    public double Max => Percentile(1);

    public double Percentile(double q)
    {
        if (_samplesMs.Count == 0)
        {
            return double.NaN;
        }

        var sorted = _samplesMs.OrderBy(x => x).ToArray();
        var index = (int)Math.Round(q * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public string Format(string unit = "ms") =>
        $"{Name}: n={SampleCount} warmup={WarmupIterations} " +
        $"min={Min:F3}{unit} median={Median:F3}{unit} p95={P95:F3}{unit} max={Max:F3}{unit}";
}
