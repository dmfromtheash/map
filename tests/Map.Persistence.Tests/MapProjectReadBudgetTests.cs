using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Tests.Support;
using System.Text;
using System.IO.Compression;

namespace Map.Persistence.Tests;

public sealed class MapProjectReadBudgetTests
{
    [Fact]
    public void Oversized_compressed_container_is_refused_before_ZipArchive_materialization()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("oversized-container" + MapProjectFormat.Extension);
        using (var stream = File.Create(path))
        {
            stream.SetLength(MapProjectFormat.MaxContainerFileBytes + 1);
        }

        Assert.Throws<MapProjectResourceLimitException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void Oversized_entry_name_is_refused_by_central_directory_admission()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("oversized-name" + MapProjectFormat.Extension);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry(new string('a', MapProjectFormat.MaxEntryNameBytes + 1));
        }

        Assert.Throws<MapProjectResourceLimitException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void Editor_asset_preflight_rejects_an_aggregate_before_payload_allocation()
    {
        var document = ProjectFixtures.Document();

        Assert.True(MapProjectStore.CanAdmitEmbeddedBytes(
            document, MapProjectFormat.MaxAssetPayloadBytes, additionalAssets: 1, out _));
        Assert.False(MapProjectStore.CanAdmitEmbeddedBytes(
            document, MapProjectFormat.MaxAssetPayloadBytes + 1, additionalAssets: 1, out var reason));
        Assert.Contains("лимит", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_refuses_an_excessive_asset_entry_count_before_creating_a_temp()
    {
        using var workspace = new TempWorkspace();
        var assets = Enumerable.Range(0, 8_191).Select(index => new MapAssetReference(
            index.ToString("x64"), $"asset-{index}", "image/png", "png",
            ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "test", "",
            embeddedContent: [1], fallbackThumbnailPng: [2])).ToList();
        var document = ProjectFixtures.Document().WithAssets(assets);
        var target = workspace.File("too-many-assets" + MapProjectFormat.Extension);

        var failure = Assert.Throws<MapProjectResourceLimitException>(() => MapProjectStore.Save(
            document, target, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));

        Assert.Contains("файлов контейнера", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp-*"));
    }

    [Fact]
    public void Asset_payloads_have_a_retained_memory_budget_below_the_container_total()
    {
        var budget = new MapProjectReadBudget();
        for (var index = 0; index < 4; index++)
        {
            budget.Add($"assets/{index:D2}.png", 64L * 1024 * 1024, 60L * 1024 * 1024);
        }

        var failure = Assert.Throws<MapProjectResourceLimitException>(() =>
            budget.Add("assets/04.png", 1, 1));

        Assert.Contains("asset payload", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extreme_compression_ratio_is_rejected_from_metadata_only()
    {
        var budget = new MapProjectReadBudget();

        var failure = Assert.Throws<MapProjectResourceLimitException>(() =>
            budget.Add("scene.json", 8L * 1024 * 1024, 4 * 1024));

        Assert.Contains("compression ratio", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Entry_just_below_the_old_threshold_cannot_bypass_the_ratio_budget()
    {
        var budget = new MapProjectReadBudget();

        var failure = Assert.Throws<MapProjectResourceLimitException>(() =>
            budget.Add("assets/bomb.png", (4L * 1024 * 1024) - 1, 1));

        Assert.Contains("compression ratio", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Highly_compressible_fixed_size_tile_fits_the_additive_allowance()
    {
        var budget = new MapProjectReadBudget();

        budget.Add("rasters/layer/0_0.bin", 256L * 256 * 4, 256);

        Assert.Equal(256L * 256 * 4, budget.TilePayloadBytes);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Negative_declared_lengths_are_corruption(long uncompressed, long compressed)
    {
        var budget = new MapProjectReadBudget();

        Assert.Throws<MapProjectCorruptedException>(() =>
            budget.Add("scene.json", uncompressed, compressed));
    }

    [Theory]
    [InlineData("preview.png", MapProjectFormat.MaxPreviewBytes)]
    [InlineData("rasters/layer/0_0.bin", MapProjectFormat.MaxTileEntryBytes)]
    [InlineData("assets/tree.png", MapProjectFormat.MaxAssetEntryBytes)]
    [InlineData("scene.json", MapProjectFormat.MaxJsonEntryBytes)]
    public void Every_entry_class_accepts_its_exact_limit_and_rejects_one_more(
        string name, long limit)
    {
        var accepted = new MapProjectReadBudget();
        accepted.Add(name, limit, limit);

        var rejected = new MapProjectReadBudget();
        Assert.Throws<MapProjectResourceLimitException>(() =>
            rejected.Add(name, limit + 1, limit + 1));
    }

    [Fact]
    public void Compression_allowance_is_inclusive_but_one_more_byte_is_rejected()
    {
        const long compressed = 1_024;
        var allowance = (long)(compressed * MapProjectFormat.MaxCompressionRatio)
            + MapProjectFormat.CompressionRatioSlackBytes;
        var accepted = new MapProjectReadBudget();
        accepted.Add("scene.json", allowance, compressed);

        var rejected = new MapProjectReadBudget();
        Assert.Throws<MapProjectResourceLimitException>(() =>
            rejected.Add("scene.json", allowance + 1, compressed));
    }

    [Fact]
    public void Json_and_total_aggregates_accept_the_boundary_and_reject_the_next_byte()
    {
        var json = new MapProjectReadBudget();
        json.Add("first.json", MapProjectFormat.MaxJsonEntryBytes, MapProjectFormat.MaxJsonEntryBytes);
        json.Add("second.json", MapProjectFormat.MaxJsonEntryBytes, MapProjectFormat.MaxJsonEntryBytes);
        Assert.Equal(MapProjectFormat.MaxJsonPayloadBytes, json.JsonPayloadBytes);
        Assert.Throws<MapProjectResourceLimitException>(() => json.Add("third.json", 1, 1));

        var total = new MapProjectReadBudget();
        for (var index = 0; index < 16; index++)
        {
            total.Add($"other-{index}.bin", MapProjectFormat.MaxJsonEntryBytes,
                MapProjectFormat.MaxJsonEntryBytes);
        }

        Assert.Equal(MapProjectFormat.MaxTotalUncompressedBytes, total.TotalUncompressedBytes);
        Assert.Throws<MapProjectResourceLimitException>(() => total.Add("overflow.bin", 1, 1));
    }

    [Fact]
    public void Tile_aggregate_accepts_the_boundary_and_rejects_the_next_tile_byte()
    {
        var budget = new MapProjectReadBudget();
        var entries = MapProjectFormat.MaxTilePayloadBytes / MapProjectFormat.MaxTileEntryBytes;
        for (var index = 0; index < entries; index++)
        {
            budget.Add($"rasters/layer/{index}_0.bin", MapProjectFormat.MaxTileEntryBytes,
                MapProjectFormat.MaxTileEntryBytes);
        }

        Assert.Equal(MapProjectFormat.MaxTilePayloadBytes, budget.TilePayloadBytes);
        Assert.Throws<MapProjectResourceLimitException>(() =>
            budget.Add("rasters/layer/overflow.bin", 1, 1));
    }

    [Fact]
    public void Normal_project_entry_mix_fits_the_budget()
    {
        var budget = new MapProjectReadBudget();
        budget.Add("manifest.json", 8 * 1024, 2 * 1024);
        budget.Add("scene.json", 2 * 1024 * 1024, 200 * 1024);
        budget.Add("asset-manifest.json", 64 * 1024, 8 * 1024);
        budget.Add("preview.png", 2 * 1024 * 1024, 2 * 1024 * 1024);
        budget.Add("assets/tree.png", 32 * 1024 * 1024, 31 * 1024 * 1024);
        budget.Add("rasters/layer/0_0.bin", 256 * 256 * 4, 64 * 1024);

        Assert.InRange(budget.TotalUncompressedBytes, 36L * 1024 * 1024, 40L * 1024 * 1024);
    }

    [Fact]
    public void Semantic_json_budget_accepts_the_documented_large_map_profile()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"objects\":[" +
            string.Join(',', Enumerable.Repeat("{}", 50_000)) +
            "]}");

        MapProjectJsonBudget.Validate(json, MapProjectFormat.SceneEntry);
    }

    [Fact]
    public void Semantic_json_budget_rejects_tiny_token_object_amplification_before_deserialization()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"objects\":[" +
            string.Join(',', Enumerable.Repeat("{}", MapProjectJsonBudget.MaxMaterializedObjects + 1)) +
            "]}");

        var failure = Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectJsonBudget.Validate(json, MapProjectFormat.SceneEntry));

        Assert.Contains("semantic JSON budget", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("objects", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Semantic_json_budget_aggregates_composition_items_with_layer_objects()
    {
        var first = MapProjectJsonBudget.MaxMaterializedObjects / 2;
        var json = Encoding.UTF8.GetBytes(
            "{\"objects\":[" + string.Join(',', Enumerable.Repeat("{}", first)) +
            "],\"items\":[" +
            string.Join(',', Enumerable.Repeat("{}", MapProjectJsonBudget.MaxMaterializedObjects - first + 1)) +
            "]}");

        Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectJsonBudget.Validate(json, MapProjectFormat.SceneEntry));
    }

    [Fact]
    public void Semantic_json_budget_rejects_an_individually_excessive_string()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"markdown\":\"" + new string('x', MapProjectJsonBudget.MaxStringUtf8Bytes + 1) + "\"}");

        Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectJsonBudget.Validate(json, MapProjectFormat.SceneEntry));
    }
}
