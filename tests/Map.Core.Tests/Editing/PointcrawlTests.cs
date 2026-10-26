using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R32: the pointcrawl - what the players see by visits, a deterministic spring layout inside the canvas, validation.</summary>
public sealed class PointcrawlTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static MapPointcrawl Crawl() => new()
    {
        Nodes =
        [
            new PointcrawlNode(A, "Деревня", 100, 100) { Visited = true },
            new PointcrawlNode(B, "Руины", 400, 120),
            new PointcrawlNode(C, "Логово", 700, 500),
            new PointcrawlNode(D, "Пещера", 300, 600),
        ],
        Edges =
        [
            new PointcrawlEdge(Guid.NewGuid(), A, B) { Hours = 6 },
            new PointcrawlEdge(Guid.NewGuid(), B, C) { Danger = 3 },
            new PointcrawlEdge(Guid.NewGuid(), A, D) { Hidden = true },
            new PointcrawlEdge(Guid.NewGuid(), C, A) { OneWay = true },
        ],
    };

    [Fact]
    public void Players_see_visited_places_their_ways_and_unknown_far_ends()
    {
        var (nodes, edges) = Crawl().ForPlayers();
        Assert.Equal([(A, true), (B, false)], nodes.Select(n => (n.Node.Id, n.Known)));
        Assert.Single(edges);
        // The one-way way C→A cannot be taken from A, so it does not show from there.
        Assert.DoesNotContain(edges, e => e.From == C);

        var all = (Crawl() with { FogByVisit = false }).ForPlayers();
        Assert.Equal(4, all.Nodes.Count);
        Assert.Equal(3, all.Edges.Count);

        var players = PlayerScene.Sanitize(Doc() with { Pointcrawl = Crawl() }).Pointcrawl;
        Assert.Equal(["Деревня", "?"], players.Nodes.Select(n => n.Name));
        players.Validate();
    }

    [Fact]
    public void The_layout_keeps_places_inside_brings_linked_ones_closer_and_repeats()
    {
        var crawl = Crawl() with { Nodes = [.. Crawl().Nodes.Select(n => n with { X = 500, Y = 500 })] };
        var arranged = PointcrawlLayout.Arrange(crawl, 1000, 800);
        Assert.All(arranged.Nodes, n => { Assert.InRange(n.X, 0, 1000); Assert.InRange(n.Y, 0, 800); });
        Assert.Equal(arranged.Nodes.ToArray(), PointcrawlLayout.Arrange(crawl, 1000, 800).Nodes.ToArray());
        double Distance(Guid x, Guid y) { var (p, q) = (arranged.Nodes.Single(n => n.Id == x), arranged.Nodes.Single(n => n.Id == y)); return double.Hypot(p.X - q.X, p.Y - q.Y); }
        Assert.True(Distance(A, B) < Distance(B, D), "linked places end closer than unlinked ones");
    }

    [Fact]
    public void A_broken_crawl_is_refused()
    {
        Crawl().Validate();
        Assert.Throws<ArgumentException>(() => (Crawl() with { Edges = [new PointcrawlEdge(Guid.NewGuid(), A, Guid.NewGuid())] }).Validate());
        Assert.Throws<ArgumentException>(() => (Crawl() with { Edges = [new PointcrawlEdge(Guid.NewGuid(), A, A)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Crawl() with { Edges = [new PointcrawlEdge(Guid.NewGuid(), A, B) { Danger = 7 }] }).Validate());
        var session = new EditingSession(Doc());
        session.Execute(new SetPointcrawlCommand(session.Document.Pointcrawl, Crawl(), "crawl"));
        Assert.Equal(4, session.Document.Pointcrawl.Nodes.Length);
        Assert.True(session.Undo());
        Assert.True(session.Document.Pointcrawl.IsDefault);
    }

    private static MapDocument Doc() =>
        new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);
}
