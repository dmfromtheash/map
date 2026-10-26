using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>R26: cards of the world - [[links]], backlinks, search, bindings and one undo step per change.</summary>
public sealed class MapEntityTests
{
    private static MapDocument Empty() => new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);

    [Fact]
    public void Links_backlinks_and_search_follow_the_names()
    {
        var city = new MapEntity { Name = "Ильград", Kind = EntityKind.Place, Markdown = "Столица у реки [[Сельва]]. Правит [[Дом Орла]]." };
        var river = new MapEntity { Name = "Сельва", Kind = EntityKind.River, Markdown = "Течёт мимо [[ильград]]." };
        var house = new MapEntity { Name = "Дом Орла", Kind = EntityKind.Faction };
        IReadOnlyList<MapEntity> all = [city, river, house];
        Assert.Equal(["Сельва", "Дом Орла"], city.LinkedNames());
        Assert.Equal([river], MapEntities.Backlinks(all, city));
        Assert.Equal([city], MapEntities.Backlinks(all, house));
        Assert.Same(river, MapEntities.Resolve(all, " сельва "));
        Assert.Equal([river], MapEntities.Search(all, "мимо"));
        Assert.Equal([house], MapEntities.Search(all, "фракц"));
        Assert.Equal(3, MapEntities.Search(all, "").Count());
    }

    [Fact]
    public void Cards_are_validated_and_replaced_as_one_undo_step()
    {
        Assert.Throws<ArgumentException>(() => new MapEntity { Name = " " }.Validate());
        Assert.Throws<ArgumentException>(() => new MapEntity { Name = "Х", Markdown = "<script>" }.Validate());
        Assert.Throws<ArgumentException>(() => new MapEntity { Name = "Х", Bindings = [new EntityBinding(null, EntityTargetKind.Object, Guid.Empty, 0, 0)] }.Validate());

        var session = new EditingSession(Empty());
        var card = new MapEntity { Name = "Ильград", Bindings = [new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, 10, 20)] };
        session.Execute(new SetEntitiesCommand([], [card], "Новая карточка"));
        Assert.Single(session.Document.Entities);
        // The cards travel with every copy of the document: layers, checkpoints, resize.
        Assert.Single(session.Document.WithLayers(session.Document.Layers).Entities);
        Assert.Single(MapCheckpointSnapshot.Capture(session.Document).Materialize(session.Document.Id).Entities);
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Entities);
        Assert.Throws<InvalidOperationException>(() => session.Execute(new SetEntitiesCommand([], [card, card], "Дубль")));
    }
}
