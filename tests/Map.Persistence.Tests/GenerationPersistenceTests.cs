using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class GenerationPersistenceTests
{
    [Fact]
    public void Area_recipe_protected_slots_and_object_reason_round_trip()
    {
        using var workspace = new TempWorkspace();
        var png = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Area", DateTimeOffset.UnixEpoch, new(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer],
            [new MapAssetReference(sha, "Smoke", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "Test", "Test", png)]);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea { LayoutVersion = 4, Density = 70, Seed = 5,
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 500, 10), new(Guid.NewGuid(), 500, 500), new(Guid.NewGuid(), 10, 500)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 40 } });
        document = new PlacementAreaCommand(layer.Id, area.Id, area, "Create").Apply(document);
        layer = (ObjectLayer)document.FindLayer(layer.Id)!; area = layer.PlacementAreas.Single();
        var slot = area.Members.First().Slot;
        area = area with { GenerationProtection = [slot],
            GenerationProtectionMask = PlacementAreaDensityMask.Create(-128, -128, 8, 4, 4, new byte[16]) };
        document = document.WithLayer(layer.Id, _ => layer with { PlacementAreas = [area] });
        document = ControlledAreaGeneration.Create(document, layer.Id, area.Id, default).Apply(document);
        var path = workspace.File("area.mapproj"); MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), png);
        var actual = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single();
        var saved = document.Layers.OfType<ObjectLayer>().Single().PlacementAreas.Single();
        Assert.Equal(saved.GenerationRecipe, actual.PlacementAreas.Single().GenerationRecipe);
        Assert.Contains(slot, actual.PlacementAreas.Single().GenerationProtection!);
        Assert.Equal(saved.GenerationProtectionMask, actual.PlacementAreas.Single().GenerationProtectionMask);
        Assert.Equal(document.Layers.OfType<ObjectLayer>().Single().Objects.Select(o => (o.Id, o.GenerationReason)), actual.Objects.Select(o => (o.Id, o.GenerationReason)));
    }

    [Fact]
    public void Recipe_protection_and_reasons_round_trip_in_v31()
    {
        using var workspace = new TempWorkspace();
        var document = HexMapFactory.Create("R20", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 24, 24, "#FFFFFFFF");
        var layer = document.Layers.OfType<HexLayer>().Single(); var h = layer.Board.Layout.FromOffset(new(2, 2));
        layer = layer with { GenerationProtection = [h], Board = layer.Board.With([new(h, HexCell.Of("starter/grass"))]) };
        var recipe = new HexGenerationRecipe { Settings = new() { Seed = 19, SettlementsPerThousand = 60 }, Steps = [.. Enum.GetValues<HexGenerationStep>()] };
        layer = ControlledHexGeneration.Run(layer, recipe);
        document = document.WithLayer(layer.Id, _ => layer);
        var path = workspace.File("generation.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path)) using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(31, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var actual = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(recipe.Settings, actual.GenerationRecipe!.Settings); Assert.True(recipe.Steps.SequenceEqual(actual.GenerationRecipe.Steps));
        Assert.True(actual.GenerationProtection.SetEquals(layer.GenerationProtection));
        Assert.Equal(layer.Board.Cells.OrderBy(p => p.Key.R).ThenBy(p => p.Key.Q), actual.Board.Cells.OrderBy(p => p.Key.R).ThenBy(p => p.Key.Q));
        Assert.Equal(layer.Paths.Select(p => (p.Id, p.GenerationReason)), actual.Paths.Select(p => (p.Id, p.GenerationReason)));
        Assert.All(layer.Paths.Zip(actual.Paths), p => Assert.True(p.First.Hexes.SequenceEqual(p.Second.Hexes)));
    }

    [Fact]
    public void V30_opens_unchanged_and_acquires_v31_only_when_generation_is_used()
    {
        using var workspace = new TempWorkspace();
        var baseLayer = new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF");
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Effects = new() { DepthSort = true } };
        var document = new MapDocument(DocumentId.New(), "v30", DateTimeOffset.UnixEpoch, new(512, 512, "px", "#FFFFFFFF"), [baseLayer, objects]);
        var path = workspace.File("v30.mapproj"); MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var bytes = File.ReadAllBytes(path); var timestamp = File.GetLastWriteTimeUtc(path);
        using (var zip = ZipFile.OpenRead(path)) using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(30, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var actual = MapProjectStore.Open(path).Document;
        Assert.True(actual.Layers.OfType<ObjectLayer>().Single().Effects.DepthSort);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var copy = workspace.File("reopened.mapproj"); MapProjectStore.Save(actual, copy, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var reopened = ZipFile.OpenRead(copy); using var manifest = JsonDocument.Parse(reopened.GetEntry("manifest.json")!.Open());
        Assert.Equal(30, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
