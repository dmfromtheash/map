using System.Collections.Immutable;

namespace Map.Core.Documents;

public enum RevealCondition { Always, OnVisit, Manual, FromDate }

/// <summary>Игровое состояние слоя. Посещения сохраняются отдельно от очищаемого следа.</summary>
public sealed record HexParty
{
    public HexCoord? Position { get; init; }
    public int SightRadius { get; init; } = 1;
    public ImmutableList<HexCoord> History { get; init; } = [];
    public ImmutableHashSet<HexCoord> Visited { get; init; } = [];
}

public static class HexGameplay
{
    public const int MaxSightRadius = 32;

    public static bool IsRevealed(HexLayer layer, HexCoord hex, HexNote note) => note.RevealCondition switch
    {
        RevealCondition.Always => true,
        RevealCondition.OnVisit => layer.Party.Visited.Contains(hex),
        RevealCondition.Manual => note.ManuallyRevealed,
        RevealCondition.FromDate => layer.CampaignDay >= note.RevealDay,
        _ => false,
    };

    /// <summary>Один шаг: позиция, посещение, след и раскрытие существующей местности.</summary>
    public static HexLayer MoveParty(HexLayer layer, HexCoord hex)
    {
        if (!layer.Board.Contains(hex) || layer.Party.Position == hex) return layer;
        var party = layer.Party;
        var edits = HexCoord.Range(hex, Math.Clamp(party.SightRadius, 0, MaxSightRadius))
            .Where(layer.Board.Contains).Where(h => layer.Board[h] is { Revealed: false })
            .Select(h => new KeyValuePair<HexCoord, HexCell?>(h, layer.Board[h]!.WithRevealed(true)));
        return layer with
        {
            Board = layer.Board.With(edits),
            Party = party with { Position = hex, History = party.History.Add(hex), Visited = party.Visited.Add(hex) },
        };
    }

    // Слух без приблизительного имени не раскрывает истинное название.
    public static string PlayerLabel(string truth, string approximate, string rumor, string fallback) =>
        !string.IsNullOrWhiteSpace(approximate) ? approximate : !string.IsNullOrWhiteSpace(rumor) ? fallback : truth;
}
