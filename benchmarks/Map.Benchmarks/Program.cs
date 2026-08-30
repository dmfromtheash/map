using System.Text.Json;
using Map.Benchmarks;

var options = BenchmarkOptions.Parse(args);
var runner = new Stage11BenchmarkRunner(options);
var report = runner.Run();
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};
var json = JsonSerializer.Serialize(report, jsonOptions);
if (!string.IsNullOrWhiteSpace(options.OutputPath))
{
    var outputFull = Path.GetFullPath(options.OutputPath);
    var outputDirectory = Path.GetDirectoryName(outputFull);
    if (!string.IsNullOrWhiteSpace(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }
    File.WriteAllText(outputFull, json + Environment.NewLine);
}

Console.WriteLine(json);
return report.GatePassed ? 0 : 2;
