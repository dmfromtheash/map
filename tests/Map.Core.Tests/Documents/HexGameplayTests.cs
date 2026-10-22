using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public sealed class HexGameplayTests
{
    [Fact]
    public void Approximate_label_alone_is_public_and_survives_repeated_sanitization()
    {
        var l = Layer(); var h = l.Board.Layout.FromOffset(new(1, 1));
        l = l with { Notes = l.Notes.Add(h, new HexNote("Секретное имя") { ApproximateLabel = "Примерный ориентир" }) };
        var safe = ExportPlayerVisibility.Sanitize(ExportPlayerVisibility.Sanitize(l));
        Assert.Equal("Примерный ориентир", safe.Notes[h].Title);
        Assert.Empty(safe.Notes[h].PlayerText);
    }

    [Fact]
    public void Board_changes_move_and_clip_party_history_and_visits()
    {
        var l = Layer(); var h = l.Board.Layout.FromOffset(new(2, 2));
        l = HexGameplay.MoveParty(l, h);
        var shifted = Map.Core.Editing.HexBoardOps.InsertColumns(l, 0, 1);
        var moved = shifted.Board.Layout.FromOffset(new(3, 2));
        Assert.Equal(moved, shifted.Party.Position); Assert.Equal(moved, Assert.Single(shifted.Party.History));
        Assert.Contains(moved, shifted.Party.Visited);
        var clipped = Map.Core.Editing.HexBoardOps.Resize(shifted, 1, 1);
        Assert.Null(clipped.Party.Position); Assert.Empty(clipped.Party.History); Assert.Empty(clipped.Party.Visited);
    }

    internal static HexLayer Layer() => HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch,
        new MapCategoryId("hex"), HexOrientation.Flat, 40, 5, 5, "#FFFFFFFF").Layers.OfType<HexLayer>().Single();

    [Theory]
    [InlineData(RevealCondition.Always, false, false, 0, 10, true)]
    [InlineData(RevealCondition.Manual, false, true, 20, 0, false)]
    [InlineData(RevealCondition.Manual, true, false, 0, 0, true)]
    [InlineData(RevealCondition.OnVisit, false, false, 0, 0, false)]
    [InlineData(RevealCondition.OnVisit, false, true, 0, 0, true)]
    [InlineData(RevealCondition.FromDate, false, false, 9, 10, false)]
    [InlineData(RevealCondition.FromDate, false, false, 10, 10, true)]
    public void Reveal_condition_controls_player_package(RevealCondition condition, bool manual, bool visited, int day, int due, bool expected)
    {
        var l = Layer(); var h = l.Board.Layout.FromOffset(new(2, 2));
        var n = new HexNote("Название", playerText: "Игрокам") { RevealCondition = condition, ManuallyRevealed = manual, RevealDay = due };
        l = l with { Notes = l.Notes.Add(h, n), CampaignDay = day,
            Party = new HexParty { Visited = visited ? [h] : [] } };
        Assert.Equal(expected, ExportPlayerVisibility.NoteVisible(l, h, n));
        Assert.Equal(expected, ExportPlayerVisibility.Sanitize(l).Notes.ContainsKey(h));
        l = l with { FogEnabled = true };
        Assert.False(ExportPlayerVisibility.NoteVisible(l, h, n));
    }

    [Fact]
    public void Party_reveals_radius_records_visits_without_treating_sight_as_visit()
    {
        var l = Layer(); var a = l.Board.Layout.FromOffset(new(2, 2)); var b = a.Neighbors().First();
        l = l with { FogEnabled = true, Board = l.Board.With([new(a, HexCell.Of("starter/grass")), new(b, HexCell.Of("starter/forest"))]) };
        var moved = HexGameplay.MoveParty(l, a);
        Assert.True(moved.Board[a]!.Revealed); Assert.True(moved.Board[b]!.Revealed);
        Assert.False(l.Board[a]!.Revealed); Assert.Null(l.Party.Position);
        Assert.Single(moved.Party.Visited); Assert.DoesNotContain(b, moved.Party.Visited);
        var next = HexGameplay.MoveParty(moved, b);
        Assert.Equal(new[] { a, b }, next.Party.History); Assert.Equal(2, next.Party.Visited.Count);
        Assert.Same(next, HexGameplay.MoveParty(next, b));
        Assert.Equal(l.Board.CellCount, next.Board.CellCount);
    }

    [Fact]
    public void Rumors_replace_truth_and_sanitization_is_idempotent()
    {
        var l = Layer(); var h = l.Board.Layout.FromOffset(new(2, 2));
        var cell = HexCell.Of("starter/grass", region: 1, revealed: true).WithPointsOfInterest(
            [new HexPoi("starter/ruins", "Правда о точке") { Rumor = "Слух о башне", ApproximateLabel = "Где-то башня" }]);
        l = l with { Board = l.Board.With([new(h, cell)]), FogEnabled = true,
            Regions = [new HexRegion(1, "Истинный край", "#FFFFFFFF") { Rumor = "Неспокойно" }],
            Notes = l.Notes.Add(h, new HexNote("Истинное имя", gmText: "Секрет", playerText: "Правда", childMap: "секрет.mapproj")
            { Rumor = "Говорят, там клад", ApproximateLabel = "Примерное место" }) };
        var safe = ExportPlayerVisibility.Sanitize(l); var n = safe.Notes[h];
        Assert.Equal("Примерное место", n.Title); Assert.Equal("Говорят, там клад", n.PlayerText);
        Assert.Empty(n.GmText); Assert.Null(n.ChildMap);
        Assert.Equal("Неизвестный край", safe.Regions[0].Name);
        Assert.Equal("Где-то башня", safe.Board[h]!.PointsOfInterest[0].Name);
        var again = ExportPlayerVisibility.Sanitize(safe);
        Assert.Equal(n, again.Notes[h]); Assert.Equal(safe.Board[h], again.Board[h]);
        Assert.Equal("Истинное имя", l.Notes[h].Title);
    }
}
