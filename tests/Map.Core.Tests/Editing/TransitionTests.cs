using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R29: transitions are validated, kept as one undo step, moved with the content, and the check finds every end that leads nowhere.</summary>
public sealed class TransitionTests
{
    [Fact]
    public void The_check_finds_every_end_that_leads_nowhere()
    {
        var other = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var atlasMap = new AtlasMap(other, "Пещеры.mapproj", "Пещеры") { Level = WorldLevel.Underdark };
        var document = Doc() with
        {
            Transitions =
            [
                new MapTransition { Name = "Лестница", Kind = TransitionKind.Stairs, From = new(null, 100, 100), To = new(null, 300, 300) },
                new MapTransition { Name = "Колодец", Kind = TransitionKind.Shaft, From = new(null, 200, 200) },
                new MapTransition { Name = "Вход", Kind = TransitionKind.CaveEntrance, From = new(null, 50, 50), To = new(other, 9000, 10) },
                new MapTransition { Name = "Портал", Kind = TransitionKind.Portal, From = new(null, 60, 60), To = new(gone, 10, 10) },
                new MapTransition { Name = "Обрыв", From = new(null, 5000, 60), To = new(null, 10, 10) },
            ],
        };
        var atlas = new MapAtlas(Guid.NewGuid(), [new AtlasMap(document.Id.Value, "Мир.mapproj", "Мир"), atlasMap], []);
        atlas.Validate();
        var issues = MapTransitions.Check(document, atlas, new Dictionary<Guid, (double, double)> { [other] = (1000, 1000) });
        Assert.DoesNotContain(issues, i => i.Name == "Лестница");
        Assert.Contains(issues, i => i.Name == "Колодец" && i.Problem == "второй конец не задан");
        Assert.Contains(issues, i => i.Name == "Вход" && i.Problem == "второй конец за краем своей карты");
        Assert.Contains(issues, i => i.Name == "Портал" && i.Problem == "карта второго конца не найдена в атласе");
        Assert.Contains(issues, i => i.Name == "Обрыв" && i.Problem == "первый конец за краем карты");
        Assert.Throws<InvalidOperationException>(() => (atlas with { Maps = [.. atlas.Maps, new AtlasMap(Guid.NewGuid(), "x.mapproj", "x") { Level = (WorldLevel)7 }] }).Validate());
    }

    [Fact]
    public void Transitions_are_one_undo_step_and_move_with_a_resized_canvas()
    {
        var session = new EditingSession(Doc());
        var stairs = new MapTransition { Name = "Лестница", From = new(null, 100, 100, 0), To = new(null, 100, 100, -1) };
        session.Execute(new SetTransitionsCommand([], [stairs], "add"));
        Assert.Single(session.Document.Transitions);
        Assert.Throws<InvalidOperationException>(() => session.Execute(new SetTransitionsCommand(session.Document.Transitions, [stairs, stairs], "dup")));
        Assert.Throws<ArgumentException>(() => (stairs with { From = new(Guid.NewGuid(), 1, 1) }).Validate());
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Transitions);

        var withStairs = Doc() with { Transitions = [stairs] };
        var plan = CanvasTransformService.PlanResize(withStairs, 2048, 2048, CanvasAnchor.Center);
        var resized = CanvasTransformService.ApplyResize(withStairs, plan);
        var moved = resized.Transitions.Single();
        Assert.Equal(100 + plan.OffsetX, moved.From.X); Assert.Equal(100 + plan.OffsetY, moved.From.Y);
        Assert.Equal(100 + plan.OffsetX, moved.To!.X);
        Assert.NotEqual(0, plan.OffsetX);
        Assert.Single(MapCheckpointSnapshot.Capture(withStairs).Materialize(withStairs.Id).Transitions);
    }

    private static MapDocument Doc() =>
        new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);
}
