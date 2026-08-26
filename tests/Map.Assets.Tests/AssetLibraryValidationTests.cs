using System.Text;
using System.Text.Json;

namespace Map.Assets.Tests;

/// <summary>
/// The schema v2 consistency matrix. A file that parses as JSON but contradicts itself is a
/// damaged index, not a library: it must be reported, left byte-for-byte alone, and it must never
/// reach the code that walks the collection graph. Every case here writes a hand-made index,
/// because these states cannot be produced through the catalog's own writing path.
/// </summary>
public sealed class AssetLibraryValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-validation", Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Two_collections_that_share_an_identifier_are_a_damaged_index()
    {
        var document = Healthy();
        document.Collections.Add(new AssetCollection { Id = "col-a", Name = "Дубль", Order = 1 });

        AssertDamaged(document, "коллекц");
    }

    [Fact]
    public void A_collection_whose_parent_does_not_exist_is_a_damaged_index()
    {
        var document = Healthy();
        document.Collections.Add(new AssetCollection { Id = "col-b", Name = "Сирота", ParentId = "col-ghost" });

        AssertDamaged(document, "родител");
    }

    [Fact]
    public void A_collection_that_is_its_own_parent_is_a_damaged_index()
    {
        var document = Healthy();
        document.Collections.Add(new AssetCollection { Id = "col-b", Name = "Сама себе", ParentId = "col-b" });

        AssertDamaged(document, "цикл");
    }

    /// <summary>
    /// The case a recursive tree walk cannot survive: three nodes pointing round in a ring. The
    /// index must be refused before anything tries to build the collection tree from it.
    /// </summary>
    [Fact]
    public void A_cycle_across_three_collections_is_refused_instead_of_overflowing_the_stack()
    {
        var document = Healthy();
        document.Collections.Add(new AssetCollection { Id = "col-x", Name = "X", ParentId = "col-z" });
        document.Collections.Add(new AssetCollection { Id = "col-y", Name = "Y", ParentId = "col-x" });
        document.Collections.Add(new AssetCollection { Id = "col-z", Name = "Z", ParentId = "col-y" });

        var catalog = AssertDamaged(document, "цикл");

        // Reached only because the load refused the document: an empty tree, not a stack overflow.
        Assert.Empty(catalog.CollectionTree());
    }

    [Fact]
    public void A_variant_that_names_a_content_record_nobody_stores_is_a_damaged_index()
    {
        var document = Healthy();
        document.Variants.Add(new AssetVariant { Id = "var-ghost", Sha256 = Sha(9), Label = "Призрак" });
        document.Sets[0] = document.Sets[0] with { VariantIds = [.. document.Sets[0].VariantIds, "var-ghost"] };

        AssertDamaged(document, "содержим");
    }

    [Fact]
    public void A_set_that_names_a_collection_nobody_stores_is_a_damaged_index()
    {
        var document = Healthy();
        document.Sets[0] = document.Sets[0] with { CollectionId = "col-ghost" };

        AssertDamaged(document, "коллекц");
    }

    [Fact]
    public void A_set_that_lists_the_same_variant_twice_is_a_damaged_index()
    {
        var document = Healthy();
        document.Sets[0] = document.Sets[0] with { VariantIds = ["var-0", "var-1", "var-0"] };

        AssertDamaged(document, "повтор");
    }

    [Fact]
    public void A_variant_claimed_by_two_sets_at_once_is_a_damaged_index()
    {
        var document = Healthy();
        document.Sets[1] = document.Sets[1] with { VariantIds = ["var-0"], CoverVariantId = "var-0" };

        AssertDamaged(document, "сразу в два набора");
    }

    [Fact]
    public void A_variant_without_an_identifier_or_a_label_is_a_damaged_index()
    {
        var document = Healthy();
        document.Variants[2] = document.Variants[2] with { Label = "  " };

        AssertDamaged(document, "вариант без идентификатора");
    }

    [Fact]
    public void A_collection_without_a_name_is_a_damaged_index()
    {
        var document = Healthy();
        document.Collections[0] = document.Collections[0] with { Name = "" };

        AssertDamaged(document, "коллекция без идентификатора");
    }

    [Fact]
    public void A_set_without_a_name_is_a_damaged_index()
    {
        var document = Healthy();
        document.Sets[0] = document.Sets[0] with { Name = "   " };

        AssertDamaged(document, "набор без идентификатора");
    }

    [Fact]
    public void A_current_variant_recorded_for_a_set_nobody_stores_is_a_damaged_index()
    {
        var document = Healthy();
        document.CurrentVariantBySet["set-ghost"] = "var-0";

        AssertDamaged(document, "отсутствующего набора");
    }

    [Fact]
    public void A_recent_entry_without_a_set_identifier_is_a_damaged_index()
    {
        var document = Healthy();
        document.Recent.Add(new AssetRecentEntry
        {
            SetId = "   ",
            VariantId = "var-0",
            UsedUtc = DateTimeOffset.UnixEpoch,
        });

        AssertDamaged(document, "недавн");
    }

    /// <summary>
    /// A set whose tag list is null cannot be filtered or grouped, and JSON is free to write it
    /// that way. It has to be refused rather than crash the first query that touches the card.
    /// </summary>
    [Fact]
    public void A_set_whose_tag_list_is_null_is_a_damaged_index()
    {
        var path = Index();
        var text = JsonSerializer.Serialize(Healthy(), Options)
            .Replace("\"tags\": []", "\"tags\": null", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bytes);

        var catalog = new AssetCatalog(_root);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.Contains("тег", catalog.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_recent_entry_pointing_at_a_set_nobody_stores_is_a_damaged_index()
    {
        var document = Healthy();
        document.Recent.Add(new AssetRecentEntry
        {
            SetId = "set-ghost",
            VariantId = "var-0",
            UsedUtc = DateTimeOffset.UnixEpoch,
        });

        AssertDamaged(document, "недавн");
    }

    [Fact]
    public void A_recent_entry_whose_variant_belongs_to_another_set_is_a_damaged_index()
    {
        var document = Healthy();
        document.Recent.Add(new AssetRecentEntry
        {
            SetId = document.Sets[0].Id,
            VariantId = "var-2",
            UsedUtc = DateTimeOffset.UnixEpoch,
        });

        AssertDamaged(document, "недавн");
    }

    [Fact]
    public void A_current_variant_that_does_not_belong_to_its_set_is_a_damaged_index()
    {
        var document = Healthy();
        document.CurrentVariantBySet[document.Sets[0].Id] = "var-2";

        AssertDamaged(document, "текущ");
    }

    /// <summary>
    /// JSON can set a list to null outright. The deserialized document then has no collection at
    /// all where the code expects one, so the file has to be refused before anything reads it.
    /// </summary>
    [Fact]
    public void An_index_whose_lists_are_null_is_a_damaged_index_rather_than_a_crash()
    {
        var path = Index();
        var bytes = Encoding.UTF8.GetBytes(
            """
            { "schemaVersion": 2, "records": null, "collections": null, "variants": null,
              "sets": null, "recent": null, "currentVariantBySet": null }
            """);
        File.WriteAllBytes(path, bytes);

        var catalog = new AssetCatalog(_root);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.Contains("обязательных списков", catalog.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(catalog.Sets);
        Assert.Empty(catalog.CollectionTree());
    }

    [Fact]
    public void The_consistent_index_this_matrix_is_built_from_really_does_load()
    {
        // Without this the whole matrix could pass by rejecting everything, including a good file.
        var path = Index();
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Healthy(), Options));

        var catalog = new AssetCatalog(_root);

        Assert.Equal(AssetLibraryStatus.Ready, catalog.Status);
        Assert.Equal(2, catalog.Sets.Count);
        Assert.Single(catalog.CollectionTree());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Writes the index, opens it and proves it was refused without being touched.</summary>
    private AssetCatalog AssertDamaged(AssetLibraryDocument document, string expectedFragment)
    {
        var path = Index();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        File.WriteAllBytes(path, bytes);

        var catalog = new AssetCatalog(_root);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.Contains(expectedFragment, catalog.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(catalog.Sets);

        // A damaged index is read-only, and the user's only copy survives unchanged.
        Assert.Throws<InvalidOperationException>(() => catalog.RenameSet("set-a", "Новое имя"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        return catalog;
    }

    /// <summary>
    /// Two consistent cards in one collection: a two-variant set and a singleton. Every test
    /// breaks exactly one rule on top of this, so the reason for each refusal is unambiguous.
    /// </summary>
    private static AssetLibraryDocument Healthy()
    {
        var document = new AssetLibraryDocument
        {
            Collections = [new AssetCollection { Id = "col-a", Name = "Коллекция", Order = 0 }],
        };

        for (var index = 0; index < 3; index++)
        {
            document.Records.Add(new AssetRecord
            {
                Sha256 = Sha(index),
                DisplayName = $"card-{index}",
                OriginalFileName = $"card-{index}.png",
                MediaType = "image/png",
                Extension = "png",
                Width = 512,
                Height = 512,
                HasAlpha = true,
                ByteLength = 1_024,
                ImportedUtc = DateTimeOffset.UnixEpoch,
                Provenance = new AssetProvenance(),
                ThumbnailRelativePath = $"thumbnails/{Sha(index)}.png",
            });
            document.Variants.Add(new AssetVariant
            {
                Id = $"var-{index}",
                Sha256 = Sha(index),
                Label = $"card-{index}",
            });
        }

        document.Sets.Add(new AssetSet
        {
            Id = "set-a",
            Name = "Набор",
            VariantIds = ["var-0", "var-1"],
            CoverVariantId = "var-0",
            CollectionId = "col-a",
            ImportedUtc = DateTimeOffset.UnixEpoch,
        });
        document.Sets.Add(new AssetSet
        {
            Id = "set-b",
            Name = "Одиночка",
            VariantIds = ["var-2"],
            CoverVariantId = "var-2",
            ImportedUtc = DateTimeOffset.UnixEpoch,
        });
        return document;
    }

    private static string Sha(int index) => index.ToString("x64");

    private string Index()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, "catalog.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
