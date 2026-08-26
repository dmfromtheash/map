using System.Security.Cryptography;
using System.Text.Json;
using Map.DiagnosticAssets;

namespace Map.Assets.Tests;

/// <summary>
/// The two logical diagnostic fixtures. They are deliberately technical control images produced
/// by our own generator: no downloaded material, nothing resembling anyone else's artwork, and
/// nothing that may leak into a shipped starter pack.
/// </summary>
public sealed class DiagnosticFixtureTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Radii sampled outward from the brush-tip centre, along the marker-free axis.</summary>
    private static readonly int[] BrushRampOffsets = [20, 40, 60, 80, 100, 120];

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Combine(RepositoryRoot, "tests", "fixtures", "assets");

    [Fact]
    public void The_generator_reproduces_byte_identical_outputs_on_the_same_skiasharp_version()
    {
        var first = DiagnosticAssetGenerator.Generate();
        var second = DiagnosticAssetGenerator.Generate();

        Assert.Equal(13, first.Count);
        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].FileName, second[index].FileName);
            Assert.Equal(first[index].Bytes, second[index].Bytes);
        }
    }

    [Fact]
    public void The_tracked_outputs_match_what_the_generator_produces_today()
    {
        foreach (var asset in DiagnosticAssetGenerator.Generate())
        {
            var path = Path.Combine(FixtureRoot, asset.FileName);
            Assert.True(File.Exists(path), $"Tracked fixture is missing: {path}");
            Assert.Equal(asset.Bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Every_fixture_is_a_real_rgba_png_of_the_declared_size()
    {
        foreach (var asset in DiagnosticAssetGenerator.Generate())
        {
            var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, asset.FileName));
            Assert.Equal(PngSignature, bytes[..8]);

            // IHDR is the first chunk: length, type, width, height, bit depth, colour type.
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            var width = ReadBigEndian(bytes, 16);
            var height = ReadBigEndian(bytes, 20);
            var expected = asset.FileName.StartsWith("diagnostic-brush-tip", StringComparison.Ordinal) ? 256 : 512;
            Assert.Equal(expected, width);
            Assert.Equal(expected, height);
            Assert.Equal(8, bytes[24]);
            Assert.Equal(6, bytes[25]);
        }
    }

    [Fact]
    public void Every_fixture_really_carries_transparent_and_opaque_pixels()
    {
        foreach (var asset in DiagnosticAssetGenerator.Generate())
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(FixtureRoot, asset.FileName));
            Assert.NotNull(bitmap);
            var alphas = new HashSet<byte>();
            for (var y = 0; y < bitmap.Height; y += 4)
            {
                for (var x = 0; x < bitmap.Width; x += 4)
                {
                    alphas.Add(bitmap.GetPixel(x, y).Alpha);
                }
            }

            Assert.Contains((byte)0, alphas);
            Assert.Contains((byte)255, alphas);
            Assert.True(alphas.Count >= 3, $"{asset.FileName} has only {alphas.Count} alpha levels.");
        }
    }

    [Fact]
    public void The_brush_tip_has_a_hard_core_a_falling_alpha_ramp_and_an_empty_inner_sector()
    {
        // The tip is white on transparent, so it is invisible against a white background: its
        // structure lives entirely in the alpha channel and has to be measured, not eyeballed.
        using var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(FixtureRoot, "diagnostic-brush-tip.png"));
        Assert.NotNull(bitmap);
        const int centre = 128;

        Assert.Equal(255, bitmap.GetPixel(centre, centre).Alpha);
        Assert.Equal(0, bitmap.GetPixel(2, 254).Alpha);

        // Sample straight down from the centre, away from the directional marker.
        var ramp = BrushRampOffsets
            .Select(offset => (int)bitmap.GetPixel(centre, centre + offset).Alpha)
            .ToArray();
        Assert.Equal(255, ramp[0]);
        for (var index = 1; index < ramp.Length; index++)
        {
            Assert.True(
                ramp[index] <= ramp[index - 1],
                $"Alpha rose outward: {string.Join(", ", ramp)}");
        }

        Assert.True(ramp[^1] < 64, $"The outer edge is not soft: {string.Join(", ", ramp)}");
        Assert.True(ramp.Distinct().Count() >= 4, $"Only {ramp.Distinct().Count()} ring levels.");

        // The cleared wedge spans 200..246 degrees; its middle must be fully transparent.
        var angle = 223 * Math.PI / 180;
        var wedgeX = (int)Math.Round(centre + (40 * Math.Cos(angle)));
        var wedgeY = (int)Math.Round(centre + (40 * Math.Sin(angle)));
        Assert.Equal(0, bitmap.GetPixel(wedgeX, wedgeY).Alpha);

        // The coloured directional marker is opaque and is not white, so flips read correctly.
        var marker = bitmap.GetPixel(centre + 4, 60);
        Assert.Equal(255, marker.Alpha);
        Assert.True(marker.Red > marker.Blue + 80, $"Marker is not the expected warm colour: {marker}");
    }

    [Fact]
    public void The_manifest_declares_thirteen_distinct_hashes_with_a_stable_order_and_cover()
    {
        var manifest = ReadManifest();

        Assert.Equal(13, manifest.Assets.Count);
        Assert.Equal(13, manifest.Assets.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.All(manifest.Assets, item => Assert.Equal(64, item.Sha256.Length));

        var stampSet = manifest.Sets.Single(set => set.Id == DiagnosticAssetGenerator.StampSetId);
        Assert.Equal(12, stampSet.VariantIds.Count);
        Assert.Equal(stampSet.VariantIds[0], stampSet.CoverVariantId);
        Assert.Equal(
            [.. Enumerable.Range(1, 12).Select(index => $"diagnostic-stamp-{index:D2}")],
            stampSet.VariantIds);

        var brush = manifest.Sets.Single(set => set.Id == DiagnosticAssetGenerator.BrushSetId);
        Assert.Equal(["diagnostic-brush-tip"], brush.VariantIds);
    }

    [Fact]
    public void The_manifest_hashes_are_the_hashes_of_the_tracked_files()
    {
        foreach (var declared in ReadManifest().Assets)
        {
            var path = Path.Combine(FixtureRoot, declared.FileName);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(declared.Sha256, actual);
            Assert.Equal(declared.Width, declared.FileName.Contains("brush", StringComparison.Ordinal) ? 256 : 512);
        }
    }

    [Fact]
    public void Every_fixture_records_its_own_generator_author_and_licence()
    {
        foreach (var asset in ReadManifest().Assets)
        {
            Assert.Equal("Map project", asset.Author);
            Assert.Equal("CC0-1.0", asset.License);
            Assert.Equal("tools/Map.DiagnosticAssets", asset.Generator);
            Assert.True(asset.HasAlpha);
        }
    }

    [Fact]
    public void The_diagnostic_fixtures_are_not_carried_into_the_application_publish_output()
    {
        var fixtureNames = DiagnosticAssetGenerator.Generate()
            .Select(asset => asset.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // No production project may pull the fixture directory into its own content.
        foreach (var project in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj",
            SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("fixtures", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DiagnosticAssets", text, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing with those names may already sit in a built application output.
        var appOutput = Path.Combine(RepositoryRoot, "src", "Map.App", "bin");
        if (Directory.Exists(appOutput))
        {
            foreach (var file in Directory.GetFiles(appOutput, "*.png", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(Path.GetFileName(file), fixtureNames);
            }
        }
    }

    [Fact]
    public void The_stamp_fixture_imports_as_one_twelve_variant_set_and_the_brush_tip_as_a_singleton()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-fixture-import", Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new AssetCatalog(root);
            var variants = Enumerable.Range(1, 12)
                .Select(index => Path.Combine(FixtureRoot, $"diagnostic-stamp-{index:D2}.png"))
                .ToList();

            var set = catalog.ImportSet(
                variants, "Диагностический набор", new AssetImportOptions { Pack = "Диагностика" },
                DateTimeOffset.UnixEpoch);
            var tip = catalog.Import(
                Path.Combine(FixtureRoot, "diagnostic-brush-tip.png"),
                new AssetImportOptions { Pack = "Диагностика" }, DateTimeOffset.UnixEpoch);

            Assert.Equal(12, set.VariantIds.Count);
            Assert.Equal(13, catalog.Records.Count);
            Assert.Single(catalog.FindSetForContent(tip.Sha256)!.VariantIds);
            Assert.All(set.VariantIds, id => Assert.Equal(512, catalog.RecordForVariant(id)!.Width));
            Assert.All(catalog.Records, record => Assert.True(record.HasAlpha));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static DiagnosticAssetManifest ReadManifest()
    {
        var path = Path.Combine(FixtureRoot, "manifest.json");
        Assert.True(File.Exists(path), $"Fixture manifest is missing: {path}");
        return JsonSerializer.Deserialize<DiagnosticAssetManifest>(
            File.ReadAllBytes(path), DiagnosticAssetGenerator.JsonOptions)!;
    }

    private static int ReadBigEndian(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Map.slnx was not found above the test output directory.");
    }
}
