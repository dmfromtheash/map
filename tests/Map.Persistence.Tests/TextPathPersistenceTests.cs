using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R07: a label bound to a river takes schema v16 and reopens bound; free text keeps the older schema.</summary>
public sealed class TextPathPersistenceTests
{
    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 16)]
    public void A_bound_label_reopens_with_its_binding_and_baseline(bool bound, int schema)
    {
        using var workspace = new TempWorkspace();
        var document = WithLabel(bound);
        var path = workspace.File("labels.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(schema, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());

        var reopened = MapProjectStore.Open(path).Document;
        var label = reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single();
        var saved = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single();
        Assert.Equal(saved.Text, label.Text);
        Assert.Equal(saved.X, label.X); Assert.Equal(saved.Y, label.Y);
        if (!bound)
        {
            Assert.DoesNotContain("\"path\"", ReadJson(path, "scene.json").ToJsonString());
            return;
        }

        var binding = label.Text!.Path!;
        Assert.Equal(TextPathTarget.River, binding.Target);
        Assert.Equal(TextPathSide.Below, binding.Side);
        Assert.Equal(0.3, binding.Position);
        Assert.False(binding.KeepUpright);
        Assert.True(binding.Baseline.Length >= 2);
        // Reopened, the label still follows its river.
        var river = reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        Assert.Same(label, TextOnPath.Layout(reopened, label));
        Assert.Equal(river.Id, binding.TargetId);
    }

    [Fact]
    public void A_bound_label_only_in_a_named_version_also_requires_v16()
    {
        using var workspace = new TempWorkspace();
        var document = WithLabel(false).WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Подписи",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(WithLabel(true)), ProjectFixtures.PreviewPng())]);
        var path = workspace.File("version.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(16, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());
        var snapshot = MapProjectStore.Open(path).Document.Checkpoints.Single().Snapshot;
        Assert.NotNull(snapshot.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single().Text!.Path);
    }

    private static MapDocument WithLabel(bool bound)
    {
        var river = new River { Name = "Река", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new(50, 200), new(350, 220)], 20)] };
        var layer = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Река", 40, 40, 200, 60, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Река",
                Path = bound ? new TextPathBinding
                {
                    Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id,
                    Position = 0.3, Side = TextPathSide.Below, KeepUpright = false, Gap = 4,
                } : null,
            },
        };
        var document = new MapDocument(DocumentId.New(), "labels", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer with { Objects = [label] }]);
        if (!bound) return document;
        var laid = TextOnPath.Layout(document, label);
        return document.WithLayer(layer.Id, l => ((ObjectLayer)l) with { Objects = [laid] });
    }

    private static JsonNode ReadJson(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(entry)!.Open();
        return JsonNode.Parse(stream)!;
    }
}
