using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// R10 «Красота» (schema v30): an area with beauty switches and a painted density brush, a layer
/// with «ближние поверх дальних» and the tint and ground of its objects save and reopen exactly;
/// a map without them keeps its older schema; an older file that claims them is refused.
/// </summary>
public sealed class PlacementAreaBeautyPersistenceTests
{
    [Fact]
    public void Beauty_density_brush_and_layer_depth_sort_reopen_exactly_in_v30()
    {
        using var workspace = new TempWorkspace();
        var (session, layerId, saved, bytes) = Build(beauty: true);
        var path = workspace.File("beauty.mapproj");
        MapProjectStore.Save(session.Document, path, ProjectFixtures.Metadata(), bytes);
        Assert.Equal(30, Schema(path));

        var reopened = MapProjectStore.Open(path).Document;
        var layer = (ObjectLayer)reopened.FindLayer(layerId)!;
        var actual = layer.PlacementAreas.Single();
        actual.ValidateMembers();
        Assert.Equal(saved.Beauty, actual.Beauty);
        Assert.Equal(saved.DensityMask, actual.DensityMask);
        Assert.Equal(saved.DensityMask!.CopyValues(), actual.DensityMask!.CopyValues());
        Assert.True(layer.Effects.DepthSort);
        var before = ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.ToDictionary(o => o.Id);
        Assert.Equal(before.Count, layer.Objects.Count);
        foreach (var item in layer.Objects) Assert.Equal(before[item.Id], item);
        Assert.Contains(layer.Objects, o => o.Stamp is { Tint: not null, GroundShadow.Mass: > 0 });
    }

    [Fact]
    public void A_map_without_beauty_keeps_its_older_schema_and_an_older_file_cannot_claim_it()
    {
        using var workspace = new TempWorkspace();
        var (session, _, _, bytes) = Build(beauty: false);
        var path = workspace.File("plain.mapproj");
        MapProjectStore.Save(session.Document, path, ProjectFixtures.Metadata(), bytes);
        Assert.True(Schema(path) < 30);
        MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single(l => !l.PlacementAreas.IsEmpty).PlacementAreas.Single().ValidateMembers();

        // A v30 file rewritten to claim v25 is refused rather than silently read.
        var (beautiful, _, _, beautyBytes) = Build(beauty: true);
        var forged = workspace.File("forged.mapproj");
        MapProjectStore.Save(beautiful.Document, forged, ProjectFixtures.Metadata(), beautyBytes);
        using (var archive = ZipFile.Open(forged, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            var replaced = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(replaced.Open());
            writer.Write(text.Replace("\"schemaVersion\":30", "\"schemaVersion\":25", StringComparison.Ordinal));
        }
        Assert.ThrowsAny<Exception>(() => MapProjectStore.Open(forged));
    }

    private static int Schema(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        return manifest.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static (EditingSession Session, LayerId Layer, PlacementArea Saved, byte[] Bytes) Build(bool beauty)
    {
        var bytes = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "own fixture", "test", bytes);
        var session = new EditingSession(ProjectFixtures.Document("Beauty"));
        var layer = session.Document.Layers.OfType<ObjectLayer>().First();
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea
        {
            Density = 80, Seed = 18, LayoutVersion = 4, Pattern = PlacementAreaPattern.Groups,
            Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 700, 20), new(Guid.NewGuid(), 700, 500), new(Guid.NewGuid(), 20, 500)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 32 },
            Beauty = beauty ? PlacementAreaBeauty.ForNewArea(32) : null,
        });
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "area", [asset]));
        Assert.Null(session.LastResourceError);
        if (beauty)
        {
            var current = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas.Single();
            var work = PlacementAreaDensityBrush.Begin(current);
            PlacementAreaDensityBrush.Dab(work.Left, work.Top, work.CellSize, work.Width, work.Height, work.Values, 300, 250, 120, .8,
                PlacementAreaDensityBrushMode.Thinner);
            var mask = PlacementAreaDensityBrush.Finish(work.Left, work.Top, work.CellSize, work.Width, work.Height, work.Values);
            session.Execute(new PlacementAreaCommand(layer.Id, area.Id, current with { DensityMask = mask }, "brush"));
            Assert.Null(session.LastResourceError);
            var updated = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
            session.Execute(UpdateLayerCommand.Replace(updated, updated with { Effects = updated.Effects with { DepthSort = true } }, "depth"));
        }
        var saved = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas.Single();
        return (session, layer.Id, saved, bytes);
    }
}
