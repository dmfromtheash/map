using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class ControlledAreaGenerationTests
{
    [Fact]
    public void Protected_area_members_remain_exact_and_other_areas_are_untouched()
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "R20", DateTimeOffset.UnixEpoch, new(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        var sha = new string('a', 64);
        document = document.WithAssets([new MapAssetReference(sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: sha)]);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea { LayoutVersion = 4, Density = 70, Seed = 3,
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 500, 10), new(Guid.NewGuid(), 500, 500), new(Guid.NewGuid(), 10, 500)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 40 } });
        document = new PlacementAreaCommand(layer.Id, area.Id, area, "Create").Apply(document);
        layer = (ObjectLayer)document.FindLayer(layer.Id)!; area = layer.PlacementAreas.Single();
        Assert.NotEmpty(area.Members);
        var member = area.Members.First(); var original = layer.Objects.Single(o => o.Id == member.ObjectId);
        var protectedArea = area with { GenerationProtection = [member.Slot] };
        var other = protectedArea with { Id = Guid.NewGuid(), Cells = ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty,
            MemberCount = 0, Density = 0, GenerationProtection = null };
        document = document.WithLayer(layer.Id, _ => layer with { PlacementAreas = [protectedArea, other] });
        var result = ControlledAreaGeneration.Create(document, layer.Id, area.Id, default).Apply(document);
        var after = (ObjectLayer)result.FindLayer(layer.Id)!;
        Assert.Same(original, after.Objects.Single(o => o.Id == member.ObjectId));
        Assert.Equal(other, after.PlacementAreas.Single(a => a.Id == other.Id));
        var generated = after.PlacementAreas.Single(a => a.Id == area.Id);
        generated.ValidateMembers(); Assert.NotNull(generated.GenerationRecipe);
        Assert.All(after.Objects.Where(o => o.Id != member.ObjectId), o => Assert.NotNull(o.GenerationReason));
        var session = new EditingSession(document); session.Execute(ControlledAreaGeneration.Create(document, layer.Id, area.Id, default));
        session.Undo(); Assert.Equal(document.Layers, session.Document.Layers);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, protectedArea with { Density = 0 }, "Density"));
        var zero = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
        Assert.Equal(original, Assert.Single(zero.Objects)); zero.PlacementAreas.Single(a => a.Id == area.Id).ValidateMembers();
    }
}
