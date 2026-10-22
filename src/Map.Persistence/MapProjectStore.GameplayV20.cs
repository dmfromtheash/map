using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Map.Core.Documents;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static bool NeedsGameplayV20(HexLayer l) => l.CampaignDay != 0
        || l.Party.Position is not null || l.Party.SightRadius != 1 || l.Party.History.Count != 0 || l.Party.Visited.Count != 0
        || l.Notes.Values.Any(n => n.RevealCondition != RevealCondition.Always || n.ManuallyRevealed || n.RevealDay != 0
            || n.Rumor.Length != 0 || n.ApproximateLabel.Length != 0)
        || l.Regions.Any(r => r.Rumor.Length != 0 || r.ApproximateLabel.Length != 0)
        || l.Board.Cells.Any(p => p.Value.PointsOfInterest.Any(p => p.Rumor.Length != 0 || p.ApproximateLabel.Length != 0));

    private static GameplayDto? WriteGameplayV20(HexLayer l)
    {
        if (!NeedsGameplayV20(l)) return null;
        int[] At(HexCoord h) { var o = l.Board.Layout.ToOffset(h); return [o.Column, o.Row]; }
        return new GameplayDto
        {
            Day = l.CampaignDay, Position = l.Party.Position is { } h ? At(h) : null, Radius = l.Party.SightRadius,
            History = l.Party.History.Select(At).ToList(),
            Visited = l.Party.Visited.OrderBy(h => h.Q).ThenBy(h => h.R).Select(At).ToList(),
            Notes = l.Notes.Select(p => new GameplayNoteDto { Hex = At(p.Key), Condition = (int)p.Value.RevealCondition,
                Revealed = p.Value.ManuallyRevealed, Day = p.Value.RevealDay,
                Rumor = p.Value.Rumor, Label = p.Value.ApproximateLabel }).ToList(),
            Regions = l.Regions.Select(r => new GameplayRegionDto { Id = r.Id, Rumor = r.Rumor, Label = r.ApproximateLabel }).ToList(),
            Points = l.Board.Cells.Where(p => p.Value.PointsOfInterest.Any(t => t.Rumor.Length != 0 || t.ApproximateLabel.Length != 0))
                .SelectMany(p => p.Value.PointsOfInterest.Select((t, i) => new GameplayPointDto
                { Hex = At(p.Key), Index = i, Rumor = t.Rumor, Label = t.ApproximateLabel })).ToList(),
        };
    }

    // v18 → v20: defaults are the whole migration for v4..v18. v19 is reserved by R10.
    // After integration keep these two calls after the v19 reader/writer; earlier cell strides do not change.
    private static HexLayer ReadGameplayV20(HexLayer l, GameplayDto? d)
    {
        if (d is null) return l;
        static MapProjectCorruptedException Bad() => new("Повреждены игровые данные гексов (v20).");
        HexCoord At(int[]? a)
        {
            if (a is not { Length: 2 }) throw Bad();
            var h = l.Board.Layout.FromOffset(new HexOffset(a[0], a[1]));
            return l.Board.Contains(h) ? h : throw Bad();
        }
        static void Text(string? rumor, string? label)
        {
            if (rumor is null || label is null || rumor.Length > HexNote.MaxTextLength || label.Length > HexNote.MaxTitleLength) throw Bad();
        }
        if (d.Day < 0 || d.Radius is < 0 or > HexGameplay.MaxSightRadius || d.History is null || d.Visited is null
            || d.Notes is null || d.Regions is null || d.Points is null) throw Bad();
        var notes = l.Notes;
        var seen = new HashSet<HexCoord>();
        foreach (var n in d.Notes)
        {
            if (n is null) throw Bad();
            var h = At(n.Hex); Text(n.Rumor, n.Label);
            if (!seen.Add(h) || !notes.TryGetValue(h, out var note) || !Enum.IsDefined((RevealCondition)n.Condition) || n.Day < 0) throw Bad();
            notes = notes.SetItem(h, note with { RevealCondition = (RevealCondition)n.Condition,
                ManuallyRevealed = n.Revealed, RevealDay = n.Day, Rumor = n.Rumor, ApproximateLabel = n.Label });
        }
        var regions = l.Regions;
        var ids = new HashSet<int>();
        foreach (var r in d.Regions)
        {
            if (r is null) throw Bad();
            Text(r.Rumor, r.Label);
            var region = regions.FirstOrDefault(t => t.Id == r.Id);
            if (region is null || !ids.Add(r.Id)) throw Bad();
            regions = regions.Replace(region, region with { Rumor = r.Rumor, ApproximateLabel = r.Label });
        }
        var board = l.Board;
        var points = new HashSet<(HexCoord, int)>();
        foreach (var p in d.Points)
        {
            if (p is null) throw Bad();
            var h = At(p.Hex); Text(p.Rumor, p.Label);
            if (board[h] is not { } cell || p.Index < 0 || p.Index >= cell.PointsOfInterest.Count || !points.Add((h, p.Index))) throw Bad();
            board = board.With([new(h, cell.WithPointsOfInterest(cell.PointsOfInterest.SetItem(p.Index,
                cell.PointsOfInterest[p.Index] with { Rumor = p.Rumor, ApproximateLabel = p.Label })))]);
        }
        return l with { Board = board, Regions = regions, Notes = notes, CampaignDay = d.Day,
            Party = new HexParty { Position = d.Position is null ? null : At(d.Position), SightRadius = d.Radius,
                History = d.History.Select(At).ToImmutableList(), Visited = d.Visited.Select(At).ToImmutableHashSet() } };
    }
}

internal sealed class GameplayDto
{
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("position")] public int[]? Position { get; set; }
    [JsonPropertyName("radius")] public int Radius { get; set; } = 1;
    [JsonPropertyName("history")] public List<int[]> History { get; set; } = [];
    [JsonPropertyName("visited")] public List<int[]> Visited { get; set; } = [];
    [JsonPropertyName("notes")] public List<GameplayNoteDto> Notes { get; set; } = [];
    [JsonPropertyName("regions")] public List<GameplayRegionDto> Regions { get; set; } = [];
    [JsonPropertyName("points")] public List<GameplayPointDto> Points { get; set; } = [];
}
internal sealed class GameplayNoteDto
{
    public int[] Hex { get; set; } = [];
    public int Condition { get; set; }
    public bool Revealed { get; set; }
    public int Day { get; set; }
    public string Rumor { get; set; } = "";
    public string Label { get; set; } = "";
}
internal sealed class GameplayRegionDto
{
    public int Id { get; set; }
    public string Rumor { get; set; } = "";
    public string Label { get; set; } = "";
}
internal sealed class GameplayPointDto
{
    public int[] Hex { get; set; } = [];
    public int Index { get; set; }
    public string Rumor { get; set; } = "";
    public string Label { get; set; } = "";
}
