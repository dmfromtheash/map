using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R33: sector data as text. Reads the tab-separated T5 «Second Survey» table (a header naming
/// Hex, Name, UWP, Bases, Remarks, Zone, PBG, Allegiance, Stars) and the older column «.sec» lines
/// («Name  0101 A788899-C  N  Ag Ni  A  123 Im  G2 V»); writes the tab-separated table.
/// </summary>
public static partial class SecFormat
{
    [GeneratedRegex(@"^(?<name>.*?)\s*(?<hex>\d{4})\s+(?<uwp>[A-HX][0-9A-HJ-NP-Z]{6}-[0-9A-HJ-NP-Z])\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLine();

    [GeneratedRegex(@"^\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PbgToken();

    public sealed record Entry(HexOffset Hex, StarSystem System);

    public static IReadOnlyList<Entry> Read(string text, out IReadOnlyList<string> problems)
    {
        var entries = new List<Entry>();
        var issues = new List<string>();
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var header = lines.Select((l, i) => (l, i)).FirstOrDefault(p => p.l.Contains('\t') && p.l.Split('\t').Any(c => c.Trim().Equals("Hex", StringComparison.OrdinalIgnoreCase)));
        if (header.l is not null)
        {
            var columns = header.l.Split('\t').Select(c => c.Trim().ToLowerInvariant()).ToArray();
            int Col(string name) => Array.IndexOf(columns, name);
            foreach (var (line, index) in lines.Select((l, i) => (l, i)).Skip(header.i + 1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cells = line.Split('\t');
                string Cell(string name) => Col(name) is var c && c >= 0 && c < cells.Length ? cells[c].Trim() : "";
                if (StarSector.Parse(Cell("hex")) is not { } hex) { issues.Add($"строка {index + 1}: нет номера гекса"); continue; }
                var system = new StarSystem
                {
                    Name = Cell("name"), Uwp = Cell("uwp"), Bases = Cell("bases"), Remarks = Cell("remarks"),
                    Zone = ZoneOf(Cell("zone")), Pbg = Cell("pbg"), Allegiance = Cell("allegiance"), Stars = Cell("stars"),
                };
                if (!system.IsValid) { issues.Add($"строка {index + 1}: неверные данные ({system.Uwp})"); continue; }
                entries.Add(new Entry(hex, system));
            }
            problems = issues;
            return entries;
        }

        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith('@')) continue;
            var match = LegacyLine().Match(line.TrimEnd());
            if (!match.Success) { if (line.Any(char.IsDigit)) issues.Add($"строка {index + 1}: не похожа на запись сектора"); continue; }
            if (StarSector.Parse(match.Groups["hex"].Value) is not { } hex) continue;
            var tokens = match.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Bases: a short token of base letters right after the UWP.
            var bases = tokens.Count > 0 && tokens[0].Length <= 2 && tokens[0].All(ch => "ABCDEFGHKLMNPRSTVW".Contains(ch)) && !IsRemark(tokens[0]) ? Take(tokens, 0) : "";
            var pbgAt = tokens.FindIndex(t => PbgToken().IsMatch(t));
            var remarks = new List<string>();
            var zone = TravelZone.Green;
            var limit = pbgAt < 0 ? tokens.Count : pbgAt;
            for (var i = 0; i < limit; i++)
            {
                if (tokens[i] is "A" or "R") zone = ZoneOf(tokens[i]);
                else remarks.Add(tokens[i]);
            }
            var pbg = pbgAt >= 0 ? tokens[pbgAt] : "";
            var after = pbgAt >= 0 ? tokens.Skip(pbgAt + 1).ToList() : [];
            var allegiance = after.Count > 0 && after[0].Length <= 4 && !char.IsDigit(after[0][^1]) && !"OBAFGKMD".Contains(after[0][0]) ? Take(after, 0) : "";
            var system = new StarSystem
            {
                Name = match.Groups["name"].Value.Trim(), Uwp = match.Groups["uwp"].Value, Bases = bases, Remarks = string.Join(' ', remarks),
                Zone = zone, Pbg = pbg, Allegiance = allegiance, Stars = string.Join(' ', after),
            };
            if (!system.IsValid) { issues.Add($"строка {index + 1}: неверные данные"); continue; }
            entries.Add(new Entry(hex, system));
        }
        problems = issues;
        return entries;
    }

    public static string Write(IEnumerable<Entry> entries)
    {
        var text = new StringBuilder("Hex\tName\tUWP\tBases\tRemarks\tZone\tPBG\tAllegiance\tStars\n");
        foreach (var (hex, s) in entries.OrderBy(e => e.Hex.Column).ThenBy(e => e.Hex.Row))
            text.Append(StarSector.Number(hex)).Append('\t').Append(s.Name).Append('\t').Append(s.Uwp).Append('\t').Append(s.Bases).Append('\t')
                .Append(s.Remarks).Append('\t').Append(s.Zone switch { TravelZone.Amber => "A", TravelZone.Red => "R", _ => "" }).Append('\t')
                .Append(s.Pbg).Append('\t').Append(s.Allegiance).Append('\t').Append(s.Stars).Append('\n');
        return text.ToString();
    }

    private static TravelZone ZoneOf(string zone) => zone.Trim().ToUpperInvariant() switch { "A" => TravelZone.Amber, "R" => TravelZone.Red, _ => TravelZone.Green };

    private static bool IsRemark(string token) => token.Length == 2 && char.IsUpper(token[0]) && char.IsLower(token[1]);

    private static string Take(List<string> tokens, int index)
    {
        var token = tokens[index];
        tokens.RemoveAt(index);
        return token;
    }
}

/// <summary>The star systems of a hex layer replaced as one undo step (R33).</summary>
public sealed class SetHexSystemsCommand(LayerId layerId, ImmutableDictionary<HexCoord, StarSystem> before, ImmutableDictionary<HexCoord, StarSystem> after, string name) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        if (after.Values.Any(s => !s.IsValid)) throw new ArgumentException("Неверные данные звёздной системы.");
        return document.WithLayer(layerId, layer => layer is HexLayer hex ? hex with { Systems = after } : layer);
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(layerId, layer => layer is HexLayer hex ? hex with { Systems = before } : layer);
}

/// <summary>A hex map dressed as a star sector (R33): the dark field of space and a parsec per hex. One undo step.</summary>
public sealed class MakeStarSectorCommand(LayerId hexLayerId) : IEditCommand
{
    private MapDocument? _before;

    public string Name => "Звёздный сектор";

    public MapDocument Apply(MapDocument document)
    {
        _before = document;
        var layers = document.Layers.Select(layer => layer switch
        {
            BaseColorLayer background => background with { Color = StarSector.Background },
            HexLayer hex when hex.Id == hexLayerId => hex with { Travel = StarSector.Parsecs },
            _ => layer,
        }).ToArray();
        return document.WithLayers(layers);
    }

    public MapDocument Revert(MapDocument document) => _before ?? document;
}
