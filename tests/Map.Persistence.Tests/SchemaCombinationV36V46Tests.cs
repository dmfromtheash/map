using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R42c: schema selection, combinations and durable versions across v36-v46.</summary>
public sealed class SchemaCombinationV36V46Tests
{
    private static readonly CheckpointId VersionId = new(Id(100));
    private static readonly Guid OtherMap = Id(101);
    private static readonly Guid EntityId = Id(102);
    private static readonly ObjectId RegionId = new(Id(103));
    private static readonly ObjectId LabelId = new(Id(104));
    private static readonly ObjectId MinorLabelId = new(Id(105));
    private static readonly LayerId SectorId = new(Id(106));

    public static IEnumerable<object[]> Schemas => Enumerable.Range(36, 11).Select(v => new object[] { v });

    public static IEnumerable<object[]> GuardCases()
    {
        // Exercise each guard operand alone as well as the combined feature. In particular,
        // region/importance and object eras must not be masked by map-wide settings.
        var cases = Enumerable.Range(36, 11).Select(v => (Schema: v, Part: "all")).Concat(
        [
            (36, "entities"), (36, "linked-map"), (36, "note-category"), (36, "player-visible"),
            (37, "region"), (37, "importance"),
            (40, "object-era"), (40, "morph-key"), (40, "political-role"), (40, "road-era"),
            (44, "river"), (44, "road"),
        ]);
        foreach (var (schema, part) in cases)
        foreach (var checkpoint in new[] { false, true })
            yield return [schema, part, checkpoint];
    }

    [Fact]
    public void All_v36_v46_features_together_round_trip_field_by_field()
    {
        using var workspace = new TempWorkspace();
        var expected = Enumerable.Range(36, 11).Aggregate(Plain(), (d, schema) => AddFeature(d, schema));
        var path = workspace.File("all-features.mapproj");

        Save(expected, path);
        Assert.Equal(46, Schema(path));
        AssertMapEqual(expected, MapProjectStore.Open(path).Document);
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Feature_only_in_named_checkpoint_selects_schema_and_restores_exactly(int schema)
    {
        using var workspace = new TempWorkspace();
        var baseline = Plain();
        var historical = AddFeature(baseline, schema);
        var version = Version(historical);
        var path = workspace.File($"only-version-v{schema}.mapproj");

        Save(baseline.WithCheckpoints([version]), path);
        Assert.Equal(schema, Schema(path));
        Assert.Equal(schema, Schema(path, VersionId));
        var reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(baseline, reopened);
        AssertNoNewFeatures(reopened);
        AssertVersionEqual(historical, version, reopened);

        var restored = RestoreCheckpointCommand.Create(reopened, VersionId).Apply(reopened);
        AssertMapEqual(historical, restored);
        AssertVersionEqual(historical, version, restored);
        Save(restored, path);
        var savedRestore = MapProjectStore.Open(path).Document;
        AssertMapEqual(historical, savedRestore);
        AssertVersionEqual(historical, version, savedRestore);
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Removing_feature_from_head_keeps_saved_version_through_resave_and_restore(int schema)
    {
        using var workspace = new TempWorkspace();
        var historical = AddFeature(Plain(), schema);
        var version = Version(historical);
        var path = workspace.File($"removed-v{schema}.mapproj");
        Save(historical.WithCheckpoints([version]), path);
        var saved = MapProjectStore.Open(path).Document;

        var withoutFeature = RemoveFeature(saved, schema);
        AssertNoNewFeatures(withoutFeature);
        Save(withoutFeature, path);
        Assert.Equal(schema, Schema(path));
        var reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(withoutFeature, reopened);
        AssertNoNewFeatures(reopened);
        AssertVersionEqual(historical, version, reopened);

        // Saving the still feature-free head must retain data needed only by the version.
        Save(reopened, path);
        Assert.Equal(schema, Schema(path));
        reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(withoutFeature, reopened);
        AssertNoNewFeatures(reopened);
        AssertVersionEqual(historical, version, reopened);
        var restored = RestoreCheckpointCommand.Create(reopened, VersionId).Apply(reopened);
        AssertMapEqual(historical, restored);
        Save(restored, path);
        reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(historical, reopened);
        AssertVersionEqual(historical, version, reopened);
    }

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void Schema_one_below_content_is_rejected_without_changing_file(int schema, string part, bool checkpoint)
    {
        using var workspace = new TempWorkspace();
        var content = GuardFeature(schema, part);
        var document = checkpoint ? Plain().WithCheckpoints([Version(content)]) : content;
        var path = workspace.File($"guard-v{schema}-{part}-{checkpoint}.mapproj");
        Save(document, path);
        Assert.Equal(schema, Schema(path));
        var valid = MapProjectStore.Open(path).Document;
        AssertMapEqual(content, checkpoint ? valid.Checkpoints.Single().Snapshot.Materialize(valid.Id) : valid);

        // Same manifest edit used by the existing WaterCliffs tests, with snapshot hashes
        // repaired so rejection must come from the feature guard, not a checksum mismatch.
        ProjectJsonTestMutations.RewriteSchemaVersion(path, schema - 1, checkpoint ? VersionId : null);
        Assert.Equal(schema - 1, Schema(path, checkpoint ? VersionId : null));
        var before = File.ReadAllBytes(path);
        var error = Record.Exception(() => MapProjectStore.Open(path));

        Assert.Equal(before, File.ReadAllBytes(path));
        var corruption = Assert.IsType<MapProjectCorruptedException>(error);
        Assert.Contains($"v{schema}", corruption.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V44_from_parent_keeps_origin_and_geometry_in_head_and_named_version_restore()
    {
        using var workspace = new TempWorkspace();
        var parent = Plain() with { Id = new DocumentId(OtherMap) };
        var transform = new AtlasTransform(200, 200, 400, 300, 1600, 1200);
        var child = ProjectFixtures.Document("Дочерняя карта") with { Canvas = new CanvasDefinition(1600, 1200, "px", "#FF10131A") };
        var plan = ScaleSync.Plan(parent, child, transform);
        Assert.Single(plan.AddRoads);
        Assert.Single(plan.AddRivers);
        Assert.Empty(plan.KeptByHand);
        var inherited = ScaleSync.Apply(child, plan);
        var copiedRoad = inherited.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Single();
        var copiedRiver = inherited.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        AssertOrigin(parent, copiedRoad.Inherited, Objects(parent).Roads.Single().Id);
        AssertOrigin(parent, copiedRiver.Inherited, Objects(parent).Rivers.Single().Id);
        Assert.Equal((0d, 600d), (copiedRoad.Nodes[0].X, copiedRoad.Nodes[0].Y));
        Assert.Equal((1600d, 600d), (copiedRoad.Nodes[^1].X, copiedRoad.Nodes[^1].Y));
        Assert.Equal((800d, 0d, 32d), (copiedRiver.Channels[0].Nodes[0].X, copiedRiver.Channels[0].Nodes[0].Y, copiedRiver.Channels[0].Nodes[0].Width));
        Assert.Equal(1200d, copiedRiver.Channels[0].Nodes[^1].Y);
        var version = Version(inherited);
        var path = workspace.File("from-parent.mapproj");
        Save(inherited.WithCheckpoints([version]), path);
        Assert.Equal(44, Schema(path));
        var reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(inherited, reopened);
        AssertVersionEqual(inherited, version, reopened);
        Assert.Equal(0, ScaleSync.Plan(parent, reopened, transform).Changes);

        Save(child.WithCheckpoints(reopened.Checkpoints), path);
        reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(child, reopened);
        var restored = RestoreCheckpointCommand.Create(reopened, VersionId).Apply(reopened);
        AssertMapEqual(inherited, restored);
        Save(restored, path);
        reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(inherited, reopened);
        AssertVersionEqual(inherited, version, reopened);
        Assert.Equal(0, ScaleSync.Plan(parent, reopened, transform).Changes);
    }

    [Fact]
    public void V35_content_opens_and_resaves_without_inventing_v36_plus_data()
    {
        using var workspace = new TempWorkspace();
        var baseline = Plain();
        var objects = Objects(baseline);
        var expected = baseline.WithLayer(objects.Id, _ => objects with
        {
            Objects = [.. objects.Objects.Select(o => o with { GeographyIntentional = true })],
            Rivers = [.. objects.Rivers.Select(r => r with { GeographyIntentional = true })],
            Roads = [.. objects.Roads.Select(r => r with { GeographyIntentional = true })],
        });
        var path = workspace.File("legacy-v35.mapproj");
        Save(expected, path);
        Assert.Equal(35, Schema(path));
        var reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(expected, reopened);
        AssertNoNewFeatures(reopened);
        Save(reopened, path);
        // The writer chooses the lowest necessary schema; opening with the current reader
        // must not promote a v35 file or manufacture newer fields on its next save.
        Assert.Equal(35, Schema(path));
        reopened = MapProjectStore.Open(path).Document;
        AssertMapEqual(expected, reopened);
        AssertNoNewFeatures(reopened);
        Assert.Empty(reopened.Checkpoints);
        using var archive = ZipFile.OpenRead(path);
        var scene = JsonNode.Parse(archive.GetEntry(MapProjectFormat.SceneEntry)!.Open())!.AsObject();
        foreach (var key in new[] { "entities", "labels", "fog", "transitions", "timeline", "pointcrawl", "planet" })
            Assert.False(scene.ContainsKey(key), key);
        Assert.All(scene["notes"]!.AsArray(), n =>
        {
            foreach (var key in new[] { "linkedMap", "category", "playerVisible", "origin" })
                Assert.False(n!.AsObject().ContainsKey(key), key);
        });
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static ObjectLayer Objects(MapDocument document) => (ObjectLayer)document.FindLayer(ProjectFixtures.FixedLayerId)!;

    private static MapDocument Plain()
    {
        var document = ProjectFixtures.Document("Сочетания схем") with { Canvas = new CanvasDefinition(2048, 1024, "px", "#FF10131A") };
        var objects = Objects(document);
        var river = new River { Name = "Сельва", Channels = [RiverEditor.NewChannel([new(400, 0), new(400, 1000)], 8)], WidthScale = 1.25 };
        var road = RoadEditor.NewRoad([new(0, 350), new(1000, 350)], 12, RoadStyle.Country(), "Тракт", 42);
        return document.WithLayer(objects.Id, _ => objects with { Rivers = [river], Roads = [road] })
            .WithNotes([new MapNote(new NoteId(Id(107)), "Ручная заметка", "**Пещера** у [[Ильград]].", NoteAnchorKind.Object, 96.5, 20.25, ProjectFixtures.FixedRectangleId)]);
    }

    private static MapDocument AddFeature(MapDocument document, int schema)
    {
        var objects = Objects(document);
        switch (schema)
        {
            case 36:
                return document.WithNotes([.. document.Notes.Select(n => n with { LinkedMap = OtherMap, Category = NoteCategory.Secret, PlayerVisible = true })]) with
                {
                    Entities = [new MapEntity
                    {
                        Id = EntityId, Name = "Ильград", Kind = EntityKind.Place, Markdown = "Столица у [[Сельва]].", PlayerVisible = true,
                        Bindings = [new(null, EntityTargetKind.Object, ProjectFixtures.FixedRectangleId.Value, 10.5, 20.25), new(OtherMap, EntityTargetKind.Hex, Guid.Empty, 3, -2)],
                    }],
                };
            case 37:
                var shape = VectorGeometry.CreateFreeShape(RegionId, "Долина", [new(10, 10), new(300, 20), new(280, 200), new(20, 180)]);
                var label = new SceneObject(LabelId, SceneObjectKind.Text, "Долина", 50, 60, 200, 40, 12, "#FF20242B", "#FFFFFFFF", 0, true, false)
                {
                    Text = new TextStyle { Content = "Долина", Importance = 5, Region = new TextRegionBinding { Target = TextRegionTarget.Shape, TargetId = RegionId.Value, EntityId = EntityId, OffsetX = 12.5, OffsetY = -40 } },
                };
                var minor = label with { Id = MinorLabelId, Name = "Хутор", Text = new TextStyle { Content = "Хутор", Importance = 1 } };
                return document.WithLayer(objects.Id, _ => objects with { Objects = [.. objects.Objects, shape, label, minor] }) with
                {
                    Labels = new LabelSettings { ByScale = true, ResolveOverlaps = true, ClearUnderLabels = true, MinScales = [1, .5, .25, .1, 0] },
                };
            case 38:
                var stroke = MapFogEditor.Begin(new MapFog { Enabled = true });
                MapFogEditor.Dab(stroke, 300, 300, 90, true, document.Canvas.Width, document.Canvas.Height);
                var fog = MapFogEditor.Finish(stroke);
                return document with { Fog = fog with { Revealed = fog.Revealed.SetItem(MapFog.Key(1, 0), []) } };
            case 39:
                return document with { Transitions = [.. Enum.GetValues<TransitionKind>().Select((kind, i) => new MapTransition
                {
                    Id = Id(110 + i), Name = $"Переход {kind}", Kind = kind, From = new(null, 40 + i, 50 + i, 0),
                    To = new(OtherMap, 300.5 + i, 200.25 + i, -1), Direction = (TransitionDirection)(i % 3), CostMinutes = 5.5 + i,
                })] };
            case 40:
                var state = VectorGeometry.CreateFreeShape(new ObjectId(Id(115)), "Королевство", [new(20, 20), new(280, 30), new(260, 180)])
                    with { Era = new Era(812, null), MorphKey = "kingdom", Political = PoliticalRole.Disputed };
                return document.WithLayer(objects.Id, _ => objects with { Objects = [.. objects.Objects, state], Roads = [.. objects.Roads.Select(r => r with { Era = new Era(null, 1040) })] }) with
                {
                    Timeline = new MapTimeline { Enabled = true, Year = 900, First = 800, Last = 1100, PoliticalMode = true },
                };
            case 41:
                return document with { Pointcrawl = new MapPointcrawl
                {
                    Nodes = [new(Id(116), "Деревня", 10.5, 20.25) { Visited = true, Note = "Таверна", EntityId = EntityId }, new(Id(117), "Руины", 200, 120) { Note = "Склеп" }],
                    Edges = [new(Id(118), Id(116), Id(117)) { Name = "Старый тракт", Hours = 6.5, Danger = 2, Hidden = true, OneWay = true }],
                    FogByVisit = false, Shown = false,
                } };
            case 42:
                return document with { Planet = new MapPlanet { Enabled = true, SeamlessMeridian = false } };
            case 43:
                var board = HexBoard.Create(HexOrientation.Flat, 40, 8, 10);
                var hex = board.Layout.FromOffset(new HexOffset(2, 3));
                var sector = new HexLayer(SectorId, "Звёзды", true, false, .75, board)
                {
                    Systems = ImmutableDictionary<HexCoord, StarSystem>.Empty.Add(hex, new StarSystem { Name = "Регина", Uwp = "A788899-C", Bases = "NS", Remarks = "Ri Pa", Zone = TravelZone.Red, Pbg = "703", Allegiance = "ImDd", Stars = "F7 V", Notes = "секрет" }),
                };
                return document.WithLayers([.. document.Layers, sector]);
            case 44:
                var parent = document with { Id = new DocumentId(OtherMap) };
                var copy = ScaleSync.Plan(parent, ProjectFixtures.Document(), new AtlasTransform(0, 0, 2048, 1024, 2048, 1024));
                return document.WithLayer(objects.Id, _ => objects with { Rivers = [.. copy.AddRivers], Roads = [.. copy.AddRoads] });
            case 45:
                return document.WithLayer(objects.Id, _ => objects with { Rivers = [.. objects.Rivers.Select(r => r with { Era = new Era(810, 1100) })] });
            case 46:
                return document.WithNotes([.. document.Notes.Select(n => n with { Origin = NoteOrigin.CityKey })]);
            default:
                throw new ArgumentOutOfRangeException(nameof(schema));
        }
    }

    private static MapDocument RemoveFeature(MapDocument document, int schema)
    {
        var objects = Objects(document);
        return schema switch
        {
            36 => document.WithNotes([.. document.Notes.Select(n => n with { LinkedMap = null, Category = NoteCategory.Other, PlayerVisible = false })]) with { Entities = [] },
            37 => document.WithLayer(objects.Id, _ => objects with { Objects = [.. objects.Objects.Where(o => o.Id != RegionId && o.Id != LabelId && o.Id != MinorLabelId)] }) with { Labels = new LabelSettings() },
            38 => document with { Fog = new MapFog() },
            39 => document with { Transitions = [] },
            40 => document.WithLayer(objects.Id, _ => objects with { Objects = [.. objects.Objects.Select(o => o with { Era = null, MorphKey = null, Political = PoliticalRole.None })], Roads = [.. objects.Roads.Select(r => r with { Era = null })] }) with { Timeline = new MapTimeline() },
            41 => document with { Pointcrawl = new MapPointcrawl() },
            42 => document with { Planet = new MapPlanet() },
            43 => document.WithLayers([.. document.Layers.Where(l => l.Id != SectorId)]),
            44 => document.WithLayer(objects.Id, _ => objects with { Roads = [.. objects.Roads.Select(r => r with { Inherited = null })], Rivers = [.. objects.Rivers.Select(r => r with { Inherited = null })] }),
            45 => document.WithLayer(objects.Id, _ => objects with { Rivers = [.. objects.Rivers.Select(r => r with { Era = null })] }),
            46 => document.WithNotes([.. document.Notes.Select(n => n with { Origin = NoteOrigin.Manual })]),
            _ => throw new ArgumentOutOfRangeException(nameof(schema)),
        };
    }

    private static MapDocument GuardFeature(int schema, string part)
    {
        var baseline = Plain();
        var feature = AddFeature(baseline, schema);
        if (part == "all") return feature;
        if (schema == 36)
        {
            if (part == "entities") return baseline with { Entities = feature.Entities };
            return baseline.WithNotes([.. baseline.Notes.Select(n => n with
            {
                LinkedMap = part == "linked-map" ? OtherMap : null,
                Category = part == "note-category" ? NoteCategory.Secret : NoteCategory.Other,
                PlayerVisible = part == "player-visible",
            })]);
        }
        var objects = Objects(feature);
        if (schema == 37)
            return feature.WithLayer(objects.Id, _ => objects with { Objects = [.. objects.Objects.Where(o => o.Id != MinorLabelId).Select(o => o.Text is { } text
                ? o with { Text = text with { Region = part == "region" ? text.Region : null, Importance = part == "importance" ? 5 : LabelSettings.DefaultImportance } } : o)] }) with { Labels = new LabelSettings() };
        if (schema == 40)
            return feature.WithLayer(objects.Id, _ => objects with
            {
                Objects = [.. objects.Objects.Select(o => o with { Era = part == "object-era" ? o.Era : null, MorphKey = part == "morph-key" ? o.MorphKey : null, Political = part == "political-role" ? o.Political : PoliticalRole.None })],
                Roads = [.. objects.Roads.Select(r => r with { Era = part == "road-era" ? r.Era : null })],
            }) with { Timeline = new MapTimeline() };
        if (schema == 44)
            return feature.WithLayer(objects.Id, _ => objects with
            {
                Rivers = [.. objects.Rivers.Select(r => r with { Inherited = part == "river" ? r.Inherited : null })],
                Roads = [.. objects.Roads.Select(r => r with { Inherited = part == "road" ? r.Inherited : null })],
            });
        throw new ArgumentOutOfRangeException(nameof(part));
    }

    private static MapCheckpoint Version(MapDocument document) => new(VersionId, "До удаления новых свойств", ProjectFixtures.FixedModifiedUtc,
        MapCheckpointSnapshot.Capture(document), ProjectFixtures.PreviewPng());

    private static void Save(MapDocument document, string path) => MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

    private static int Schema(string path, CheckpointId? checkpoint = null)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = checkpoint is { } id ? $"{MapProjectFormat.CheckpointPrefix}{id}/manifest.json" : MapProjectFormat.ManifestEntry;
        using var stream = archive.GetEntry(entry)!.Open();
        return JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
    }

    private static void AssertVersionEqual(MapDocument expected, MapCheckpoint version, MapDocument actual)
    {
        var saved = Assert.Single(actual.Checkpoints);
        Assert.Equal(version.Id, saved.Id);
        Assert.Equal(version.Name, saved.Name);
        Assert.Equal(version.CreatedUtc, saved.CreatedUtc);
        Assert.Equal(version.PreviewPng.ToArray(), saved.PreviewPng.ToArray());
        var snapshot = saved.Snapshot.Materialize(actual.Id);
        Assert.Empty(snapshot.Checkpoints);
        AssertMapEqual(expected, snapshot);
    }

    private static void AssertMapEqual(MapDocument expected, MapDocument actual)
    {
        ProjectFixtures.AssertSemanticallyEqual(expected, actual);
        Assert.Equal(expected.Entities.Length, actual.Entities.Length);
        for (var i = 0; i < expected.Entities.Length; i++)
        {
            var (e, a) = (expected.Entities[i], actual.Entities[i]);
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Markdown, a.Markdown);
            Assert.Equal(e.PlayerVisible, a.PlayerVisible);
            Assert.Equal(e.Bindings.ToArray(), a.Bindings.ToArray());
        }
        Assert.Equal(expected.Labels.ByScale, actual.Labels.ByScale);
        Assert.Equal(expected.Labels.ResolveOverlaps, actual.Labels.ResolveOverlaps);
        Assert.Equal(expected.Labels.ClearUnderLabels, actual.Labels.ClearUnderLabels);
        Assert.Equal(expected.Labels.MinScales.ToArray(), actual.Labels.MinScales.ToArray());
        Assert.Equal(expected.Fog.Enabled, actual.Fog.Enabled);
        Assert.Equal(expected.Fog.Revealed.Keys.Order(), actual.Fog.Revealed.Keys.Order());
        foreach (var (key, rows) in expected.Fog.Revealed)
            Assert.Equal(rows.ToArray(), actual.Fog.Revealed[key].ToArray());
        Assert.Equal(expected.Transitions.ToArray(), actual.Transitions.ToArray());
        Assert.Equal(expected.Timeline, actual.Timeline);
        Assert.Equal(expected.Pointcrawl.Nodes.ToArray(), actual.Pointcrawl.Nodes.ToArray());
        Assert.Equal(expected.Pointcrawl.Edges.ToArray(), actual.Pointcrawl.Edges.ToArray());
        Assert.Equal(expected.Pointcrawl.FogByVisit, actual.Pointcrawl.FogByVisit);
        Assert.Equal(expected.Pointcrawl.Shown, actual.Pointcrawl.Shown);
        Assert.Equal(expected.Planet, actual.Planet);
        foreach (var layer in expected.Layers.OfType<ObjectLayer>())
        {
            var read = Assert.IsType<ObjectLayer>(actual.FindLayer(layer.Id));
            for (var i = 0; i < layer.Objects.Count; i++)
            {
                Assert.Equal(layer.Objects[i].Era, read.Objects[i].Era);
                Assert.Equal(layer.Objects[i].MorphKey, read.Objects[i].MorphKey);
                Assert.Equal(layer.Objects[i].Political, read.Objects[i].Political);
                Assert.Equal(layer.Objects[i].GeographyIntentional, read.Objects[i].GeographyIntentional);
            }
            Assert.Equal(layer.Roads.Length, read.Roads.Length);
            for (var i = 0; i < layer.Roads.Length; i++) AssertRoadEqual(layer.Roads[i], read.Roads[i]);
            Assert.Equal(layer.Rivers.Length, read.Rivers.Length);
            for (var i = 0; i < layer.Rivers.Length; i++) AssertRiverEqual(layer.Rivers[i], read.Rivers[i]);
        }
        foreach (var layer in expected.Layers.OfType<HexLayer>())
        {
            var read = Assert.IsType<HexLayer>(actual.FindLayer(layer.Id));
            Assert.Equal(layer.Systems.Keys.OrderBy(k => k.Q).ThenBy(k => k.R), read.Systems.Keys.OrderBy(k => k.Q).ThenBy(k => k.R));
            foreach (var (hex, system) in layer.Systems) Assert.Equal(system, read.Systems[hex]);
        }
    }

    private static void AssertRoadEqual(Road expected, Road actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Nodes.ToArray(), actual.Nodes.ToArray());
        Assert.Equal(expected.Branches.Length, actual.Branches.Length);
        for (var i = 0; i < expected.Branches.Length; i++)
        {
            var (e, a) = (expected.Branches[i], actual.Branches[i]);
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Closed, a.Closed);
            Assert.Equal(e.Style, a.Style);
            Assert.Equal(e.Vertices.ToArray(), a.Vertices.ToArray());
        }
        Assert.Equal(expected.Style, actual.Style);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.Equal(expected.Locked, actual.Locked);
        Assert.Equal(expected.WidthScale, actual.WidthScale);
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.GeographyIntentional, actual.GeographyIntentional);
        Assert.Equal(expected.Inherited, actual.Inherited);
        Assert.Equal(expected.Era, actual.Era);
        Assert.Equal(expected.GenerationReason, actual.GenerationReason);
        Assert.Equal(expected.CrossingOverrides.ToArray(), actual.CrossingOverrides.ToArray());
    }

    private static void AssertRiverEqual(River expected, River actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Channels.Length, actual.Channels.Length);
        for (var i = 0; i < expected.Channels.Length; i++)
        {
            var (e, a) = (expected.Channels[i], actual.Channels[i]);
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Nodes.ToArray(), a.Nodes.ToArray());
            Assert.Equal(e.Source, a.Source);
            Assert.Equal(e.Mouth, a.Mouth);
            Assert.Equal(e.Closed, a.Closed);
        }
        Assert.Equal(expected.Style, actual.Style);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.Equal(expected.Locked, actual.Locked);
        Assert.Equal(expected.WidthScale, actual.WidthScale);
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.GeographyIntentional, actual.GeographyIntentional);
        Assert.Equal(expected.Inherited, actual.Inherited);
        Assert.Equal(expected.Era, actual.Era);
        Assert.Equal(expected.GenerationReason, actual.GenerationReason);
    }

    private static void AssertOrigin(MapDocument parent, InheritedFrom? inherited, Guid source)
    {
        Assert.NotNull(inherited);
        Assert.Equal(parent.Id.Value, inherited.ParentMap);
        Assert.Equal(source, inherited.SourceId);
        Assert.False(string.IsNullOrWhiteSpace(inherited.Fingerprint));
    }

    private static void AssertNoNewFeatures(MapDocument document)
    {
        Assert.Empty(document.Entities);
        Assert.True(document.Labels.IsDefault);
        Assert.True(document.Fog.IsDefault);
        Assert.Empty(document.Transitions);
        Assert.True(document.Timeline.IsDefault);
        Assert.True(document.Pointcrawl.IsDefault);
        Assert.True(document.Planet.IsDefault);
        Assert.All(document.Notes, n =>
        {
            Assert.Null(n.LinkedMap);
            Assert.Equal(NoteCategory.Other, n.Category);
            Assert.False(n.PlayerVisible);
            Assert.Equal(NoteOrigin.Manual, n.Origin);
        });
        Assert.All(document.Layers.OfType<HexLayer>(), l => Assert.Empty(l.Systems));
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            Assert.All(layer.Objects, o =>
            {
                Assert.Null(o.Era);
                Assert.Null(o.MorphKey);
                Assert.Equal(PoliticalRole.None, o.Political);
                if (o.Text is { } text)
                {
                    Assert.Null(text.Region);
                    Assert.Equal(LabelSettings.DefaultImportance, text.Importance);
                }
            });
            Assert.All(layer.Rivers, r => { Assert.Null(r.Era); Assert.Null(r.Inherited); });
            Assert.All(layer.Roads, r => { Assert.Null(r.Era); Assert.Null(r.Inherited); });
        }
    }
}
