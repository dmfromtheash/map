using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R10/R11: kept-empty places and objects survive legacy v19 and current v30 beauty roundtrips.</summary>
public sealed class PlacementAreaRulesPersistenceTests
{
    [Fact]
    public void Ecological_slots_and_beauty_reopen_in_v30_without_catalog_metadata()
    {
        using var workspace = new TempWorkspace();
        var bytes = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "fixture", "test", bytes);
        var session = new EditingSession(ProjectFixtures.Document("Ecology"));
        var layer = session.Document.Layers.OfType<ObjectLayer>().First();
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea
        {
            LayoutVersion = 4, Density = 80, Seed = 18,
            Nodes = [new(Guid.NewGuid(), 50, 50), new(Guid.NewGuid(), 600, 50), new(Guid.NewGuid(), 600, 400), new(Guid.NewGuid(), 50, 400)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 32 },
            Beauty = PlacementAreaBeauty.ForNewArea(32), DepthSort = true,
        });
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "ecology", [asset], forestEcology: true));
        Assert.Null(session.LastResourceError);
        var saved = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas.Single();
        Assert.NotEmpty(saved.RuleBlocked); Assert.True(saved.MemberCount > 0);
        var path = workspace.File("ecology.mapproj");
        MapProjectStore.Save(session.Document, path, ProjectFixtures.Metadata(), bytes);
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
            Assert.Equal(MapProjectFormat.AreaBeautySchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var reopened = MapProjectStore.Open(path).Document;
        var actual = ((ObjectLayer)reopened.FindLayer(layer.Id)!).PlacementAreas.Single();
        actual.ValidateMembers();
        Assert.Equal(saved.Beauty, actual.Beauty); Assert.True(actual.DepthSort);
        Assert.Equal(saved.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal), actual.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal));
        Assert.Equal(saved.MemberCount, actual.MemberCount);
        Assert.Equal(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects, ((ObjectLayer)reopened.FindLayer(layer.Id)!).Objects);
    }

    [Fact]
    public void An_area_with_rules_reopens_exactly_in_v19()
    {
        using var workspace = new TempWorkspace();
        var bytes = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "own fixture", "test", bytes);
        var session = new EditingSession(ProjectFixtures.Document("Rules"));
        var layer = session.Document.Layers.OfType<ObjectLayer>().First();
        var road = RoadEditor.NewRoad([new(0, 220), new(700, 220)], 16, RoadStyle.Country(), "Дорога");
        session.Execute(UpdateLayerCommand.Replace(layer, layer with { Roads = [road] }, "road"));
        var area = new PlacementArea
        {
            Density = 5, Seed = 18, LayoutVersion = 1,
            Nodes = [new(Guid.NewGuid(), 50, 50), new(Guid.NewGuid(), 600, 50), new(Guid.NewGuid(), 600, 400), new(Guid.NewGuid(), 50, 400)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 32 },
            Rules = new() { AvoidRoads = true, RoadMargin = 12 },
        };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "area", [asset]));
        Assert.Null(session.LastResourceError);
        var saved = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas.Single();
        Assert.NotEmpty(saved.RuleBlocked);

        var path = workspace.File("rules.mapproj");
        MapProjectStore.Save(session.Document, path, ProjectFixtures.Metadata(), bytes);
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
            Assert.Equal(19, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var reopened = MapProjectStore.Open(path).Document;
        var actual = ((ObjectLayer)reopened.FindLayer(layer.Id)!).PlacementAreas.Single();
        actual.ValidateMembers();
        Assert.Equal(saved.Rules, actual.Rules);
        Assert.Equal(saved.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal), actual.RuleBlocked.OrderBy(s => s.Cell).ThenBy(s => s.Ordinal));
        Assert.Equal(saved.MemberCount, actual.MemberCount);
    }
}
