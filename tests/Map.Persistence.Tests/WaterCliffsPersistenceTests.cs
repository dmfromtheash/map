using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R12 (schema v32): the cliff light and mask and the river node features survive save and reopen.</summary>
public sealed class WaterCliffsPersistenceTests
{
    private static readonly byte[] Full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();

    private static int Schema(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        return json.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static (MapDocument Document, LandLayer Land, ObjectLayer Objects) Fixture()
    {
        var document = ProjectFixtures.Document("R12");
        var land = new LandLayer(LayerId.New(), "Плато", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), Full), new LandEdgeStyle()) { Level = 1 };
        var objects = document.Layers.OfType<ObjectLayer>().First();
        return (document with { Layers = [document.Layers[0], land, .. document.Layers.Skip(1)] }, land, objects);
    }

    [Fact]
    public void Cliff_light_mask_and_node_features_round_trip_in_v32()
    {
        using var workspace = new TempWorkspace();
        var (document, land, objects) = Fixture();
        var half = Full.Select((_, i) => i % TileMap.TileSize < 128 ? (byte)255 : (byte)0).ToArray();
        var masked = land with
        {
            EdgeStyle = land.EdgeStyle with { Cliff = land.EdgeStyle.Cliff with { LightAngle = 120 } },
            CliffWallTiles = TileMap.Empty(1).With(new TileKey(0, 0), half),
            CliffBeachTiles = TileMap.Empty(1).With(new TileKey(1, 0), Full),
        };
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 40), new(400, 60)], 8)] };
        river = RiverEditor.SetNodeFeature(river, river.Channels[0].Id, 1, RiverNodeFeature.Rapids);
        document = document.WithLayer(land.Id, _ => masked).WithLayer(objects.Id, _ => objects with { Rivers = [river] });
        var path = workspace.File("r12.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(32, Schema(path));
        var opened = MapProjectStore.Open(path).Document;

        var actual = (LandLayer)opened.FindLayer(land.Id)!;
        Assert.Equal(120, actual.EdgeStyle.Cliff.LightAngle);
        Assert.Equal(half, actual.CliffWallTiles.TryGet(new TileKey(0, 0)));
        Assert.Equal(Full, actual.CliffBeachTiles.TryGet(new TileKey(1, 0)));
        Assert.Equal(1, actual.CliffWallTiles.TileCount);
        Assert.Equal(RiverNodeFeature.Rapids, ((ObjectLayer)opened.FindLayer(objects.Id)!).Rivers.Single().Channels[0].Nodes[1].Feature);
    }

    [Fact]
    public void The_planet_flag_round_trips_in_v42()
    {
        using var workspace = new TempWorkspace();
        var document = new MapDocument(DocumentId.New(), "Планета", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 1024, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA")]) { Planet = new MapPlanet { Enabled = true, SeamlessMeridian = false } };
        var path = workspace.File("planet.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(42, Schema(path));
        Assert.Equal(new MapPlanet { Enabled = true, SeamlessMeridian = false }, MapProjectStore.Open(path).Document.Planet);
    }

    [Fact]
    public void The_pointcrawl_round_trips_in_v41()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var crawl = new MapPointcrawl
        {
            Nodes = [new PointcrawlNode(a, "Деревня", 10, 20) { Visited = true, Note = "Таверна" }, new PointcrawlNode(b, "Руины", 200, 120) { EntityId = Guid.NewGuid() }],
            Edges = [new PointcrawlEdge(Guid.NewGuid(), a, b) { Hours = 6.5, Danger = 2, Hidden = true, OneWay = true, Name = "Старый тракт" }],
            FogByVisit = false,
        };
        var path = workspace.File("crawl.mapproj");
        MapProjectStore.Save(document with { Pointcrawl = crawl }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(41, Schema(path));
        var read = MapProjectStore.Open(path).Document.Pointcrawl;
        Assert.Equal(crawl.Nodes.ToArray(), read.Nodes.ToArray());
        Assert.Equal(crawl.Edges.ToArray(), read.Edges.ToArray());
        Assert.False(read.FogByVisit);
    }

    [Fact]
    public void Eras_border_keys_political_roles_and_the_timeline_round_trip_in_v40()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var state = VectorGeometry.CreateFreeShape(ObjectId.New(), "Королевство", [new(10, 10), new(300, 20), new(280, 200), new(20, 180)])
            with { Era = new Era(812, null), MorphKey = "kingdom", Political = PoliticalRole.State };
        var road = RoadEditor.NewRoad([new(0, 10), new(300, 10)], 16, RoadStyle.Country(), "Тракт") with { Era = new Era(null, 1040) };
        var timeline = new MapTimeline { Enabled = true, Year = 900, First = 800, Last = 1100, PoliticalMode = true };
        var timed = document.WithLayers([.. document.Layers, new ObjectLayer(LayerId.New(), "Политика", true, false, 1, [state]) { Roads = [road] }]) with { Timeline = timeline };
        var path = workspace.File("timeline.mapproj");
        MapProjectStore.Save(timed, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(40, Schema(path));
        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(timeline, opened.Timeline);
        var layer = opened.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Политика");
        var shape = layer.Objects.Single();
        Assert.Equal(new Era(812, null), shape.Era);
        Assert.Equal("kingdom", shape.MorphKey);
        Assert.Equal(PoliticalRole.State, shape.Political);
        Assert.Equal(new Era(null, 1040), layer.Roads.Single().Era);
    }

    [Fact]
    public void Transitions_round_trip_in_v39_and_the_atlas_keeps_world_levels()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var other = Guid.NewGuid();
        var stairs = new MapTransition { Name = "Лестница в склеп", Kind = TransitionKind.Stairs, From = new(null, 40, 50, 0), To = new(other, 300, 200), Direction = TransitionDirection.Forward, CostMinutes = 5 };
        var path = workspace.File("transitions.mapproj");
        MapProjectStore.Save(document with { Transitions = [stairs] }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(39, Schema(path));
        var read = Assert.Single(MapProjectStore.Open(path).Document.Transitions);
        Assert.Equal(stairs, read);

        var atlas = new MapAtlas(Guid.NewGuid(), [new AtlasMap(document.Id.Value, "a.mapproj", "Мир"), new AtlasMap(other, "b.mapproj", "Склеп") { Level = WorldLevel.Underdark }], []);
        var manifest = workspace.File("world.mapatlas");
        AtlasStore.Save(atlas, manifest);
        var levels = AtlasStore.Read(manifest).Maps.ToDictionary(m => m.Id, m => m.Level);
        Assert.Equal(WorldLevel.Surface, levels[document.Id.Value]);
        Assert.Equal(WorldLevel.Underdark, levels[other]);
    }

    [Fact]
    public void The_fog_of_war_round_trips_in_v38()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var stroke = Map.Core.Editing.MapFogEditor.Begin(new MapFog { Enabled = true });
        Map.Core.Editing.MapFogEditor.Dab(stroke, 300, 300, 90, true, document.Canvas.Width, document.Canvas.Height);
        var fog = Map.Core.Editing.MapFogEditor.Finish(stroke);
        fog = fog with { Revealed = fog.Revealed.SetItem(MapFog.Key(3, 2), []) };
        var path = workspace.File("fog.mapproj");
        MapProjectStore.Save(document with { Fog = fog }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(38, Schema(path));
        var opened = MapProjectStore.Open(path).Document.Fog;
        Assert.True(opened.Enabled);
        Assert.Equal(fog.Revealed.Count, opened.Revealed.Count);
        foreach (var (key, rows) in fog.Revealed) Assert.Equal(rows.ToArray(), opened.Revealed[key].ToArray());
        Assert.True(opened.Sees(300, 300));
        Assert.False(opened.Sees(450, 300));
    }

    [Fact]
    public void Region_labels_importance_and_label_settings_round_trip_in_v37()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var shape = VectorGeometry.CreateFreeShape(ObjectId.New(), "Долина", [new(10, 10), new(300, 20), new(280, 200), new(20, 180)]);
        var region = new TextRegionBinding { Target = TextRegionTarget.Shape, TargetId = shape.Id.Value, EntityId = Guid.NewGuid(), OffsetX = 12.5, OffsetY = -40 };
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Долина", 50, 60, 200, 40, 12, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Долина", Importance = 5, Region = region } };
        var plain = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Хутор", 400, 60, 100, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Хутор", Importance = 1 } };
        var settings = new LabelSettings { ByScale = true, ResolveOverlaps = true, ClearUnderLabels = true, MinScales = [1, .5, .25, .1, 0] };
        var labelled = document.WithLayers([.. document.Layers, new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, [shape, label, plain])]) with { Labels = settings };
        var path = workspace.File("labels.mapproj");
        MapProjectStore.Save(labelled, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(37, Schema(path));
        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(settings.MinScales.ToArray(), opened.Labels.MinScales.ToArray());
        Assert.True(opened.Labels.ByScale && opened.Labels.ResolveOverlaps && opened.Labels.ClearUnderLabels);
        var texts = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Text is not null).ToDictionary(o => o.Id);
        Assert.Equal(region, texts[label.Id].Text!.Region);
        Assert.Equal(5, texts[label.Id].Text!.Importance);
        Assert.Equal(1, texts[plain.Id].Text!.Importance);
        Assert.Null(texts[plain.Id].Text!.Region);
    }

    [Fact]
    public void Cards_and_note_settings_round_trip_in_v36()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = Fixture();
        var map = Guid.NewGuid();
        var card = new MapEntity
        {
            Name = "Ильград", Kind = EntityKind.Place, Markdown = "Столица у [[Сельва]].", PlayerVisible = true,
            Bindings = [new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, 10, 20), new EntityBinding(map, EntityTargetKind.Hex, Guid.Empty, 3, -2)],
        };
        var note = new MapNote(NoteId.New(), "Вход", "Пещера", NoteAnchorKind.World, 5, 5) { LinkedMap = map, Category = NoteCategory.Secret, PlayerVisible = true };
        var path = workspace.File("wiki.mapproj");
        MapProjectStore.Save(document.WithNotes([note]) with { Entities = [card] }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(36, Schema(path));
        var opened = MapProjectStore.Open(path).Document;
        var read = Assert.Single(opened.Entities);
        Assert.Equal(card.Id, read.Id);
        Assert.Equal(card.Markdown, read.Markdown);
        Assert.Equal(card.Bindings.ToArray(), read.Bindings.ToArray());
        Assert.True(read.PlayerVisible);
        var readNote = Assert.Single(opened.Notes);
        Assert.Equal(map, readNote.LinkedMap);
        Assert.Equal(NoteCategory.Secret, readNote.Category);
        Assert.True(readNote.PlayerVisible);
    }

    [Fact]
    public void Intentional_marks_round_trip_in_v35()
    {
        using var workspace = new TempWorkspace();
        var (document, _, objects) = Fixture();
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 40)], 8)], GeographyIntentional = true };
        var road = RoadEditor.NewRoad([new(10, 100), new(300, 120)], 3) with { GeographyIntentional = true };
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Город", 10, 10, 100, 30, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Город" }, GeographyIntentional = true };
        var layer = objects with { Rivers = [river], Roads = [road], Objects = [label] };
        var path = workspace.File("intent.mapproj");
        MapProjectStore.Save(document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? layer : l)]), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(35, Schema(path));
        var opened = (ObjectLayer)MapProjectStore.Open(path).Document.FindLayer(objects.Id)!;
        Assert.True(opened.Rivers.Single().GeographyIntentional);
        Assert.True(opened.Roads.Single().GeographyIntentional);
        Assert.True(opened.Objects.Single().GeographyIntentional);
    }

    [Fact]
    public void Relief_and_its_look_round_trip_in_v34()
    {
        using var workspace = new TempWorkspace();
        var (document, land, _) = Fixture();
        var hill = Full.Select((_, i) => (byte)(i % 200)).ToArray();
        var raised = land with
        {
            ReliefTiles = TileMap.Empty(1).With(new TileKey(0, 0), hill),
            EdgeStyle = land.EdgeStyle with { ReliefShading = .8, ReliefContours = .2, ReliefContourStep = 12, ReliefSteepInk = .5 },
        };
        var path = workspace.File("relief.mapproj");
        MapProjectStore.Save(document.WithLayer(land.Id, _ => raised), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(34, Schema(path));
        var opened = (LandLayer)MapProjectStore.Open(path).Document.FindLayer(land.Id)!;
        Assert.Equal(hill, opened.ReliefTiles.TryGet(new TileKey(0, 0)));
        Assert.Equal(raised.EdgeStyle, opened.EdgeStyle);
    }

    [Fact]
    public void A_map_without_R12_constructs_keeps_its_schema_and_writes_no_feature()
    {
        using var workspace = new TempWorkspace();
        var (document, land, objects) = Fixture();
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 40)], 8)] };
        document = document.WithLayer(objects.Id, _ => objects with { Rivers = [river] });
        var path = workspace.File("old.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(Schema(path) < 32);
        using var zip = ZipFile.OpenRead(path);
        var scene = new StreamReader(zip.GetEntry("scene.json")!.Open()).ReadToEnd();
        Assert.DoesNotContain("\"Feature\"", scene, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lightAngle", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("cliffWallTiles", scene, StringComparison.Ordinal);
        Assert.Equal(LandCliffStyle.DefaultLightAngle,
            ((LandLayer)MapProjectStore.Open(path).Document.FindLayer(land.Id)!).EdgeStyle.Cliff.LightAngle);
    }

    [Fact]
    public void An_older_schema_carrying_a_node_feature_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var (document, _, objects) = Fixture();
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 40), new(300, 80)], 8)] };
        river = RiverEditor.SetNodeFeature(river, river.Channels[0].Id, 1, RiverNodeFeature.Waterfall);
        document = document.WithLayer(objects.Id, _ => objects with { Rivers = [river] });
        var path = workspace.File("forged.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        // Forge the manifest down to v31: the feature is still in the scene.
        var forged = workspace.File("forged-v31.mapproj");
        using (var source = ZipFile.OpenRead(path))
        using (var target = ZipFile.Open(forged, ZipArchiveMode.Create))
            foreach (var entry in source.Entries)
            {
                var bytes = new MemoryStream(); using (var input = entry.Open()) input.CopyTo(bytes);
                var data = bytes.ToArray();
                if (entry.FullName == "manifest.json")
                {
                    var root = JsonNode.Parse(data)!; root["schemaVersion"] = 31;
                    data = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
                }
                using var output = target.CreateEntry(entry.FullName).Open(); output.Write(data);
            }
        var error = Assert.ThrowsAny<Exception>(() => MapProjectStore.Open(forged));
        Assert.Contains("v32", error.ToString(), StringComparison.Ordinal);
    }
}
