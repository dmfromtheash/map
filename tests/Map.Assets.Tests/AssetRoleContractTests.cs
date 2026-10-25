using System.Text.Json.Nodes;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class AssetRoleContractTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-r11-assets-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];
    private AssetCatalog Open()
    {
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        _catalogs.Add(catalog);
        return catalog;
    }

    private AssetSet Import(AssetCatalog catalog)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "test.png");
        using var surface = SKSurface.Create(new SKImageInfo(16, 20))!;
        surface.Canvas.Clear(SKColors.Teal);
        using var image = surface.Snapshot();
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, bytes.ToArray());
        var record = catalog.Import(path, new() { Pack = "Свой набор", Tags = ["мой"],
            Provenance = new() { Source = "owner file", Author = "owner", LicenseId = "owner licence" } }, DateTimeOffset.UnixEpoch);
        return catalog.FindSetForContent(record.Sha256)!;
    }

    [Fact]
    public async Task V3_contract_roundtrips_and_does_not_duplicate_origin_or_variant_geometry()
    {
        var catalog = Open(); var set = Import(catalog);
        catalog.SetVariantPresentation(set.CoverVariantId, "Ель", .4, .9, .18);
        catalog.SetPlacementDefaults(set.Id, new() { Role = AssetRole.Spruce, AllowedRotations = [-15, 0, 15],
            CanFlip = false, DepthSort = true, HexEdges = ["land", "land", "road", "land", "land", "road"] });
        await catalog.FlushMetadataAsync();
        var reopened = Open();
        Assert.Equal(3, reopened.SchemaVersion);
        var passport = reopened.FindSet(set.Id)!.Placement!;
        Assert.Equal(AssetRole.Spruce, passport.Role);
        Assert.Equal(new double[] { -15, 0, 15 }, passport.AllowedRotations);
        Assert.False(passport.CanFlip); Assert.True(passport.DepthSort); Assert.Equal(6, passport.HexEdges.Count);
        Assert.Equal(catalog.FindSet(set.Id)!.Placement, passport);
        Assert.Equal(catalog.FindSet(set.Id)!.Placement!.GetHashCode(), passport.GetHashCode());
        var variant = reopened.FindVariant(set.CoverVariantId)!;
        Assert.Equal(.4, variant.AnchorX); Assert.Equal(.9, variant.AnchorY); Assert.Equal(.18, variant.DefaultScale);
        Assert.Equal("owner file", reopened.Records[0].Provenance.Source);
        Assert.Equal("owner licence", reopened.Records[0].Provenance.LicenseId);
        Assert.Contains("\"role\": \"spruce\"", File.ReadAllText(Path.Combine(catalog.Root, "catalog.json")));
    }

    [Fact]
    public async Task V2_migration_is_backed_up_idempotent_and_preserves_owner_metadata()
    {
        var catalog = Open(); var set = Import(catalog);
        catalog.SetVariantPresentation(set.CoverVariantId, "Мой вариант", .2, .8, .3);
        catalog.SetPlacementDefaults(set.Id, new() { CanFlip = false, CollisionFraction = .4 });
        catalog.SetSetFavorite(set.Id, true);
        await catalog.FlushMetadataAsync();
        var path = Path.Combine(catalog.Root, "catalog.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["schemaVersion"] = 2;
        var placement = json["sets"]![0]!["placement"]!.AsObject();
        foreach (var key in new[] { "role", "allowedRotations", "depthSort", "hexEdges" }) placement.Remove(key);
        var old = json.ToJsonString(); File.WriteAllText(path, old);
        var migrated = Open();
        Assert.Equal(AssetLibraryStatus.Migrated, migrated.Status); Assert.Equal(3, migrated.SchemaVersion);
        Assert.Equal(old, File.ReadAllText(Assert.Single(Directory.GetFiles(catalog.Root, "catalog.v2.*.json"))));
        var next = migrated.FindSet(set.Id)!;
        Assert.True(next.Favorite); Assert.Equal(set.Tags, next.Tags); Assert.False(next.Placement!.CanFlip);
        Assert.Null(next.Placement.Role); Assert.Equal(.4, next.Placement.CollisionFraction);
        Assert.Equal(catalog.FindVariant(set.CoverVariantId), migrated.FindVariant(set.CoverVariantId));
        Assert.Equal(AssetLibraryStatus.Ready, Open().Status);
        Assert.Single(Directory.GetFiles(catalog.Root, "catalog.v2.*.json"));
    }

    [Fact]
    public void Built_in_role_migration_recognizes_content_and_respects_an_explicitly_cleared_role()
    {
        var catalog = Open(); var set = Import(catalog); var hash = catalog.FindVariant(set.CoverVariantId)!.Sha256;
        var manifest = new RegionalArtManifest("test", "test", [new("trees", "Trees", [])],
            [new("tree", "trees", "Tree", "unused.png", hash, 46)]);
        var document = new AssetLibraryDocument { SchemaVersion = 2, Records = [.. catalog.Records],
            Variants = [.. catalog.Variants], Sets = [set], Collections = [.. catalog.Collections] };
        RegionalArtPack.AssignDefaultRoles(document, manifest);
        Assert.Equal(AssetRole.Tree, document.Sets[0].Placement!.Role);
        Assert.True(document.Sets[0].Placement!.DepthSort);
        document.Sets[0] = document.Sets[0] with { Placement = document.Sets[0].Placement! with { Role = AssetRole.None } };
        RegionalArtPack.AssignDefaultRoles(document, manifest);
        Assert.Equal(AssetRole.None, document.Sets[0].Placement!.Role);
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("edges")]
    [InlineData("role")]
    public void Invalid_contract_is_rejected_before_mutating_the_catalog(string field)
    {
        var catalog = Open(); var set = Import(catalog);
        var invalid = field switch
        {
            "rotation" => new AssetPlacementDefaults { AllowedRotations = [181] },
            "edges" => new AssetPlacementDefaults { HexEdges = ["one"] },
            _ => new AssetPlacementDefaults { Role = (AssetRole)999 },
        };
        Assert.Throws<ArgumentException>(() => catalog.SetPlacementDefaults(set.Id, invalid));
        Assert.Null(catalog.FindSet(set.Id)!.Placement);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var catalog in _catalogs) await catalog.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
