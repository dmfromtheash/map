using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

/// <summary>
/// R20b/D5: the whole hex world on a 1024 × 1024 board, every generator step in order. Each step is timed twice
/// from the same input: the bare generator (<see cref="HexWorldGenerator"/>) and the R20 envelope
/// (<see cref="ControlledHexGeneration"/>: protection, reasons, merge). Kernel/envelope alternate per pair, the
/// chain continues from the envelope result, the report keeps every run and the medians. Headless, no I/O in the timer.
/// <c>MAP_HEX1024_PAIRS</c> (default 5).
/// </summary>
internal static class HexWorld1024Probe
{
    public sealed class Report
    {
        public int Pairs { get; set; }
        public Dictionary<string, double[]> KernelMs { get; set; } = [];
        public Dictionary<string, double[]> EnvelopeMs { get; set; } = [];
        public Dictionary<string, double> KernelMedianMs { get; set; } = [];
        public Dictionary<string, double> EnvelopeMedianMs { get; set; } = [];
        public double[] WorldEnvelopeMs { get; set; } = [];
        public double WorldEnvelopeMedianMs { get; set; }
        public string ResultHash { get; set; } = "";
        public int Cells { get; set; }
        public int Paths { get; set; }
    }

    public static Report Run()
    {
        var pairs = int.TryParse(Environment.GetEnvironmentVariable("MAP_HEX1024_PAIRS"), out var p) && p > 0 ? p : 5;
        var report = new Report { Pairs = pairs };
        var settings = new HexGeneratorSettings { Seed = 9, RiverModel = HexGeneratorSettings.FlowRivers };
        var steps = Enum.GetValues<HexGenerationStep>();
        var empty = new HexLayer(LayerId.New(), "1024", true, false, 1, HexBoard.Create(HexOrientation.Flat, 16, 1024, 1024));
        var kernel = steps.ToDictionary(s => s.ToString(), _ => new List<double>());
        var envelope = steps.ToDictionary(s => s.ToString(), _ => new List<double>());
        var world = new List<double>();
        HexLayer? final = null;
        for (var pair = 0; pair < pairs; pair++)
        {
            var layer = empty; var worldWatch = 0.0;
            foreach (var step in steps)
            {
                var recipe = new HexGenerationRecipe { Settings = settings, Steps = [step] };
                HexLayer? next = null;
                foreach (var useEnvelope in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var watch = Stopwatch.StartNew();
                    var result = useEnvelope ? ControlledHexGeneration.Run(layer, recipe)
                        : HexWorldGenerator.Run(layer, step, settings);
                    watch.Stop();
                    (useEnvelope ? envelope : kernel)[step.ToString()].Add(watch.Elapsed.TotalMilliseconds);
                    if (useEnvelope) { next = result; worldWatch += watch.Elapsed.TotalMilliseconds; }
                }
                layer = next!;
                Console.WriteLine($"pair {pair} {step}: kernel {kernel[step.ToString()][^1]:0} ms, envelope {envelope[step.ToString()][^1]:0} ms");
            }
            world.Add(worldWatch);
            final = layer;
        }
        static double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
        report.KernelMs = kernel.ToDictionary(k => k.Key, k => k.Value.ToArray());
        report.EnvelopeMs = envelope.ToDictionary(k => k.Key, k => k.Value.ToArray());
        report.KernelMedianMs = kernel.ToDictionary(k => k.Key, k => Math.Round(Median(k.Value), 1));
        report.EnvelopeMedianMs = envelope.ToDictionary(k => k.Key, k => Math.Round(Median(k.Value), 1));
        report.WorldEnvelopeMs = [.. world]; report.WorldEnvelopeMedianMs = Math.Round(Median(world), 1);
        report.Cells = final!.Board.CellCount; report.Paths = final.Paths.Count;
        report.ResultHash = Hash(final);
        return report;
    }

    /// <summary>The same world must come out before and after an optimisation: every cell, reason and path.</summary>
    private static string Hash(HexLayer layer)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string s) => sha.AppendData(System.Text.Encoding.UTF8.GetBytes(s + "\n"));
        foreach (var (h, cell) in layer.Board.Cells.OrderBy(c => c.Key.Q).ThenBy(c => c.Key.R)) Add($"{h.Q},{h.R}:{cell}");
        // Path ids are new GUIDs on every run: the hash takes what the path is, not its id.
        foreach (var line in layer.Paths.Select(path => $"{path.Kind}:{path.Style}:{string.Join(';', path.Hexes)}:{string.Join(';', path.Vertices)}:{path.GenerationReason}")
            .Order(StringComparer.Ordinal)) Add(line);
        foreach (var region in layer.Regions.OrderBy(r => r.Id.ToString(), StringComparer.Ordinal)) Add(region.ToString());
        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }
}
