using System.Security.Cryptography;
using System.Text.Json;

namespace Map.Assets.Tests;

/// <summary>
/// The test assets are the owner's own six mountains (`content/Owner.Mountains.2026-10-18/source`), as 512 px copies
/// in `tests/fixtures/assets`. Smokes open real windows the owner sees, so they show his art; the coloured diagnostic
/// stamps that stood here before were removed at his word (2026-10-30). Nothing here may leak into the app output.
/// </summary>
public sealed class OwnerMountainFixtureTests
{
    private const string SetId = "owner-mountains-set";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Combine(RepositoryRoot, "tests", "fixtures", "assets");

    [Fact]
    public void The_tracked_fixture_set_matches_the_manifest_exactly()
    {
        var declared = ReadManifest().Assets
            .Select(asset => asset.FileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var tracked = Directory.GetFiles(FixtureRoot, "*.png", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, tracked);
    }

    [Fact]
    public void The_manifest_lists_the_six_owner_mountains_in_order()
    {
        var manifest = ReadManifest();

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $"owner-mountain-{index:D2}.png"),
            manifest.Assets.Select(asset => asset.FileName));
        Assert.All(manifest.Assets, asset => Assert.Equal("Владелец", asset.Author));
        Assert.DoesNotContain(manifest.Assets, asset => asset.FileName.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));

        var set = manifest.Sets.Single(item => item.Id == SetId);
        Assert.Equal(manifest.Assets.Select(asset => asset.Id), set.VariantIds);
        Assert.Equal(set.VariantIds[0], set.CoverVariantId);
    }

    [Fact]
    public void Every_fixture_comes_from_an_owner_original_that_is_in_the_repository()
    {
        foreach (var asset in ReadManifest().Assets)
        {
            Assert.StartsWith("content/Owner.Mountains.2026-10-18/source/", asset.Source, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(RepositoryRoot, asset.Source)), $"Missing owner original: {asset.Source}");
        }
    }

    [Fact]
    public void Every_fixture_is_a_real_rgba_png_of_the_declared_size()
    {
        foreach (var asset in ReadManifest().Assets)
        {
            var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, asset.FileName));
            Assert.Equal(PngSignature, bytes[..8]);

            // IHDR is the first chunk: length, type, width, height, bit depth, colour type.
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal(asset.Width, ReadBigEndian(bytes, 16));
            Assert.Equal(asset.Height, ReadBigEndian(bytes, 20));
            Assert.Equal(512, asset.Width);
            Assert.Equal(8, bytes[24]);
            Assert.Equal(6, bytes[25]);
        }
    }

    [Fact]
    public void Every_fixture_really_carries_transparent_and_opaque_pixels()
    {
        foreach (var asset in ReadManifest().Assets)
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
    public void The_manifest_hashes_are_distinct_and_match_the_tracked_files()
    {
        var assets = ReadManifest().Assets;
        Assert.Equal(assets.Count, assets.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count());
        foreach (var declared in assets)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixtureRoot, declared.FileName))));
            Assert.Equal(declared.Sha256, actual);
        }
    }

    [Fact]
    public void The_fixtures_are_not_carried_into_the_application_output()
    {
        var fixtureNames = ReadManifest().Assets
            .Select(asset => asset.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // No production project may pull the fixture directory into its own content.
        foreach (var project in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj",
            SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("fixtures", File.ReadAllText(project), StringComparison.OrdinalIgnoreCase);
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
    public void The_six_mountains_import_as_one_six_variant_set()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-fixture-import", Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new AssetCatalog(root);
            var variants = ReadManifest().Assets.Select(asset => Path.Combine(FixtureRoot, asset.FileName)).ToList();

            var set = catalog.ImportSet(
                variants, "Мои горы", new AssetImportOptions { Pack = "Горы владельца" }, DateTimeOffset.UnixEpoch);

            Assert.Equal(6, set.VariantIds.Count);
            Assert.Equal(6, catalog.Records.Count);
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

    private static FixtureManifest ReadManifest()
    {
        var path = Path.Combine(FixtureRoot, "manifest.json");
        Assert.True(File.Exists(path), $"Fixture manifest is missing: {path}");
        return JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllBytes(path), ManifestJsonOptions)!;
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

    private sealed class FixtureManifest
    {
        public int SchemaVersion { get; init; }
        public List<FixtureEntry> Assets { get; init; } = [];
        public List<FixtureSet> Sets { get; init; } = [];
    }

    private sealed class FixtureEntry
    {
        public string Id { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public string Author { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    private sealed class FixtureSet
    {
        public string Id { get; init; } = string.Empty;
        public List<string> VariantIds { get; init; } = [];
        public string CoverVariantId { get; init; } = string.Empty;
    }
}
