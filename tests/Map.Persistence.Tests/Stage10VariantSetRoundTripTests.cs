using System.IO.Compression;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// Set membership is catalog state, never project state. A map that placed several variants of
/// one logical set must reopen, render and export from its own bytes with no local library at
/// all, and the project schema must not have grown a set field to make that work.
/// </summary>
public sealed class Stage10VariantSetRoundTripTests
{
    [Fact]
    public void A_scatter_of_twelve_variants_reopens_with_the_same_hashes_without_any_catalog()
    {
        using var workspace = new TempWorkspace();
        var document = ScatterDocument(out var plan, out var references);
        var path = workspace.File("variants.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        var stamps = reopened.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.Equal(plan.Count, stamps.Count);
        for (var index = 0; index < plan.Count; index++)
        {
            Assert.Equal(plan[index].Sha256, stamps[index].Stamp!.AssetSha256);
            Assert.Equal(plan[index].X, stamps[index].X, 6);
            Assert.Equal(plan[index].Y, stamps[index].Y, 6);
            Assert.Equal(plan[index].Width, stamps[index].Width, 6);
            Assert.Equal(plan[index].Height, stamps[index].Height, 6);
            Assert.Equal(plan[index].RotationDegrees, stamps[index].RotationDegrees, 6);
            Assert.Equal(plan[index].FlipHorizontal, stamps[index].Stamp!.FlipHorizontal);
            Assert.Equal(plan[index].FlipVertical, stamps[index].Stamp!.FlipVertical);
        }

        // Every distinct variant travelled inside the container, so no catalog is consulted.
        Assert.Equal(references.Count, reopened.Assets.Count);
        foreach (var reference in references)
        {
            var stored = reopened.Assets.Single(asset => asset.Sha256 == reference.Sha256);
            Assert.Equal(ProjectAssetStorage.Embedded, stored.Storage);
            Assert.Equal(
                reference.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(stored.EmbeddedContent!)));
        }
    }

    [Fact]
    public void The_project_schema_gained_no_set_or_variant_field_for_this_slice()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("schema.mapproj");
        MapProjectStore.Save(
            ScatterDocument(out _, out _), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using var archive = ZipFile.OpenRead(path);
        var scene = ReadEntry(archive, "scene.json");
        var manifest = ReadEntry(archive, "manifest.json");

        foreach (var forbidden in new[] { "setId", "variantId", "assetSet", "assetVariant" })
        {
            Assert.DoesNotContain(forbidden, scene, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, manifest, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("assetSha256", scene, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Undo_of_the_gesture_before_saving_leaves_no_orphan_asset_in_the_container()
    {
        using var workspace = new TempWorkspace();
        var document = ScatterDocument(out _, out var references);
        var layerId = document.Layers.OfType<ObjectLayer>().Single().Id;
        var session = new EditingSession(new MapDocument(
            document.Id, document.Title, DateTimeOffset.UnixEpoch, document.Canvas,
            [document.Layers[0], new ObjectLayer(layerId, "objects", true, false, 1, [])]));

        var stamps = document.Layers.OfType<ObjectLayer>().Single().Objects;
        session.Execute(new PlaceAssetObjectsCommand(layerId, references, stamps));
        session.Undo();

        var path = workspace.File("undone.mapproj");
        MapProjectStore.Save(
            session.Document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Empty(reopened.Assets);
        Assert.Empty(reopened.Layers.OfType<ObjectLayer>().Single().Objects);
        using var archive = ZipFile.OpenRead(path);
        Assert.DoesNotContain(
            archive.Entries, entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A real Area gesture over twelve variants, materialised as a saved document.</summary>
    private static MapDocument ScatterDocument(
        out IReadOnlyList<StampPlacementItem> plan, out IReadOnlyList<MapAssetReference> references)
    {
        var contents = Enumerable.Range(0, 12).Select(Content).ToList();
        var sources = contents
            .Select(item => new StampVariantSource(item.Sha256, 512, 384))
            .ToList();
        plan = StampPlacement.PlanArea(
            new StampPlacementRequest
            {
                CenterX = 1_024,
                CenterY = 1_024,
                AreaRadius = 300,
                Density = 50,
                Seed = 20_260_826,
                RandomizeVariant = true,
                RandomAngle = true,
                RandomFlip = true,
                BaseWidth = 96,
            },
            sources);

        var used = plan.Select(item => item.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        references =
        [
            .. contents.Where(item => used.Contains(item.Sha256)).Select(item => new MapAssetReference(
                item.Sha256, item.Name, "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "diagnostic fixture", "CC0-1.0",
                embeddedContent: item.Bytes, fallbackThumbnailPng: ProjectFixtures.PreviewPng())),
        ];

        var objects = plan.Select((item, index) => new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, $"variant {index}",
            item.X, item.Y, item.Width, item.Height, item.RotationDegrees,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(item.Sha256)
            {
                FlipHorizontal = item.FlipHorizontal,
                FlipVertical = item.FlipVertical,
            },
        }).ToList();

        return new MapDocument(
            DocumentId.New(), "variant scatter", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2_048, 2_048, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects),
            ],
            references);
    }

    private static (string Sha256, string Name, byte[] Bytes) Content(int index)
    {
        var bytes = ProjectFixtures.PreviewPng().Concat(Enumerable.Repeat((byte)index, 32)).ToArray();
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), $"variant-{index:D2}", bytes);
    }
}
