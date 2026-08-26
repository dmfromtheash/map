using System.Text.Json;
using Map.DiagnosticAssets;

// Writes the project's own diagnostic control images and their manifest. The outputs are tracked
// test fixtures, never part of the application publish output or of any shipped asset pack.
var outputDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "fixtures", "assets"));

Directory.CreateDirectory(outputDirectory);
var assets = DiagnosticAssetGenerator.Generate();
foreach (var asset in assets)
{
    File.WriteAllBytes(Path.Combine(outputDirectory, asset.FileName), asset.Bytes);
}

var manifest = DiagnosticAssetGenerator.BuildManifest(assets);
File.WriteAllBytes(
    Path.Combine(outputDirectory, "manifest.json"),
    JsonSerializer.SerializeToUtf8Bytes(manifest, DiagnosticAssetGenerator.JsonOptions));

Console.WriteLine($"Wrote {assets.Count} diagnostic fixtures and manifest.json to {outputDirectory}");
