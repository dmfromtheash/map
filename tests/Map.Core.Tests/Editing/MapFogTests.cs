using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R27: the fog of war of a painted map - the brush reveals and hides cells in its circle, a stroke
/// is one undo step, full tiles are kept compact; the players' copy holds only the labels, notes
/// and cards they may see.
/// </summary>
public sealed class MapFogTests
{
    [Fact]
    public void A_stroke_reveals_its_circle_hides_again_and_is_one_undo_step()
    {
        var session = new EditingSession(Document());
        var fog = session.Document.Fog with { Enabled = true };
        session.Execute(new SetFogCommand(session.Document.Fog, fog, "on"));
        Assert.False(session.Document.Fog.Sees(500, 500));

        var stroke = MapFogEditor.Begin(session.Document.Fog);
        MapFogEditor.Dab(stroke, 500, 500, 100, reveal: true, 2048, 2048);
        var revealed = MapFogEditor.Finish(stroke);
        Assert.True(revealed.Sees(500, 500));
        Assert.True(revealed.Sees(570, 500));
        Assert.False(revealed.Sees(620, 500));
        Assert.False(revealed.Sees(500, 640));
        session.Execute(new SetFogCommand(session.Document.Fog, revealed, "reveal"));

        var hide = MapFogEditor.Begin(session.Document.Fog);
        MapFogEditor.Dab(hide, 500, 500, 30, reveal: false, 2048, 2048);
        var holed = MapFogEditor.Finish(hide);
        Assert.False(holed.Sees(500, 500));
        Assert.True(holed.Sees(570, 500));
        session.Execute(new SetFogCommand(session.Document.Fog, holed, "hide"));

        Assert.True(session.Undo());
        Assert.True(session.Document.Fog.Sees(500, 500));
        Assert.True(session.Undo());
        Assert.False(session.Document.Fog.Sees(500, 500));
        Assert.True(session.Undo());
        Assert.False(session.Document.Fog.Enabled);
        Assert.True(session.Document.Fog.Sees(500, 500), "without fog the players see everything");
    }

    [Fact]
    public void Reveal_all_keeps_full_tiles_compact_and_the_canvas_edge_bounds_the_brush()
    {
        var all = MapFogEditor.RevealAll(new MapFog { Enabled = true }, 3000, 1100);
        Assert.Equal(3 * 2, all.Revealed.Count);
        Assert.All(all.Revealed.Values, rows => Assert.Empty(rows));
        Assert.True(all.Sees(2999, 1099));
        Assert.Empty(MapFogEditor.HideAll(all).Revealed);

        var edge = MapFogEditor.Begin(new MapFog { Enabled = true });
        MapFogEditor.Dab(edge, 5, 5, 400, reveal: true, 100, 100);
        var fog = MapFogEditor.Finish(edge);
        Assert.True(fog.Sees(90, 90));
        Assert.False(fog.IsRevealedCell(100 / MapFog.Cell + 1, 0));
        fog.Validate();
    }

    [Fact]
    public void The_players_copy_holds_only_what_they_have_seen()
    {
        var seenLabel = Label("Порт", 100, 100);
        var hiddenLabel = Label("Логово", 1500, 1500);
        var stroke = MapFogEditor.Begin(new MapFog { Enabled = true });
        MapFogEditor.Dab(stroke, 160, 120, 200, reveal: true, 2048, 2048);
        var openNote = new MapNote(NoteId.New(), "Таверна", "Здесь отдыхают", NoteAnchorKind.World, 150, 150) { PlayerVisible = true };
        var secretNote = new MapNote(NoteId.New(), "Ловушка", "Только для ведущего", NoteAnchorKind.World, 150, 150);
        var fardNote = new MapNote(NoteId.New(), "Руины", "Далеко", NoteAnchorKind.World, 1500, 1500) { PlayerVisible = true };
        var document = Document(seenLabel, hiddenLabel).WithNotes([openNote, secretNote, fardNote]) with
        {
            Fog = MapFogEditor.Finish(stroke),
            Entities = [new MapEntity { Name = "Король", PlayerVisible = true }, new MapEntity { Name = "Заговор" }],
        };

        var players = new ExportScene(document, recipient: ExportRecipient.Players).Document;
        var labels = players.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Text).Select(o => o.Text!.Content);
        Assert.Equal(["Порт"], labels);
        Assert.Equal(["Таверна"], players.Notes.Select(n => n.Title));
        Assert.Equal(["Король"], players.Entities.Select(e => e.Name));
        Assert.Null(players.Atlas);

        // The master's copy is untouched.
        Assert.Equal(2, new ExportScene(document).Document.Layers.OfType<ObjectLayer>().Single().Objects.Count);
    }

    [Fact]
    public void A_card_whose_place_is_under_the_fog_stays_from_the_players_until_the_fog_opens()
    {
        // DEC-077: the place decides, not only «Видно игрокам».
        var lair = Label("Логово", 1500, 1500);
        var hut = new MapNote(NoteId.New(), "Хижина", "", NoteAnchorKind.World, 1600, 400);
        MapEntity Card(string name, params EntityBinding[] places) => new() { Name = name, PlayerVisible = true, Bindings = [.. places] };
        var dragon = Card("Дракон", new EntityBinding(null, EntityTargetKind.Object, lair.Id.Value, 0, 0));
        var witch = Card("Ведьма", new EntityBinding(null, EntityTargetKind.Note, hut.Id.Value, 0, 0));
        var spring = Card("Родник", new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, 300, 300));
        var king = Card("Король");
        var elsewhere = Card("Посол", new EntityBinding(Guid.NewGuid(), EntityTargetKind.Point, Guid.Empty, 1500, 1500));
        var secret = new MapEntity { Name = "Заговор", Bindings = [new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, 120, 120)] };
        var start = MapFogEditor.Begin(new MapFog { Enabled = true });
        MapFogEditor.Dab(start, 120, 120, 100, reveal: true, 2048, 2048);
        var document = Document(lair).WithNotes([hut]) with
        {
            Fog = MapFogEditor.Finish(start),
            Entities = [dragon, witch, spring, king, elsewhere, secret],
        };

        string[] Players(MapDocument d) => [.. new ExportScene(d, recipient: ExportRecipient.Players).Document.Entities.Select(e => e.Name)];
        // Under the fog: the lair, the hut and the spring. Without a place here: the king and the envoy of another map.
        Assert.Equal(["Король", "Посол"], Players(document));
        Assert.Equal(3, PlayerScene.HiddenByFogCount(document));
        Assert.True(PlayerScene.HiddenByFog(document, dragon));
        Assert.False(PlayerScene.HiddenByFog(document, king));

        // The players reach the lair and the spring: their cards come back; the witch's hut is still hidden.
        var walk = MapFogEditor.Begin(document.Fog);
        MapFogEditor.Dab(walk, 1560, 1520, 120, reveal: true, 2048, 2048);
        MapFogEditor.Dab(walk, 300, 300, 60, reveal: true, 2048, 2048);
        var opened = document with { Fog = MapFogEditor.Finish(walk) };
        Assert.Equal(["Дракон", "Родник", "Король", "Посол"], Players(opened));
        Assert.Equal(1, PlayerScene.HiddenByFogCount(opened));

        // The object is looked up where it stands now: moved under the fog, the dragon hides again.
        var moved = opened.WithLayers([.. opened.Layers.Select(l => l is ObjectLayer o
            ? o with { Objects = [.. o.Objects.Select(x => x.Id == lair.Id ? SceneObjectClone.At(x, 900, 1800) : x)] } : l)]);
        Assert.DoesNotContain("Дракон", Players(moved));

        // The fog switched off hides nothing; the master always gets every card.
        Assert.Equal(["Дракон", "Ведьма", "Родник", "Король", "Посол"], Players(document with { Fog = document.Fog with { Enabled = false } }));
        Assert.Equal(0, PlayerScene.HiddenByFogCount(document with { Fog = document.Fog with { Enabled = false } }));
        Assert.Equal(6, new ExportScene(document).Document.Entities.Length);
    }

    private static SceneObject Label(string content, double x, double y) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, content, x, y, 120, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = content } };

    private static MapDocument Document(params SceneObject[] objects) =>
        new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]);
}
