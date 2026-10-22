using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static bool NeedsTravelV21(HexLayer l) => !l.Journey.IsDefault;
    private static TravelV21Dto? WriteTravelV21(HexLayer l)
    {
        if (!NeedsTravelV21(l)) return null;
        int[]? At(HexCoord? h) => h is { } p ? [l.Board.Layout.ToOffset(p).Column, l.Board.Layout.ToOffset(p).Row] : null;
        return new() { Mode = (int)l.Journey.Mode, Seed = l.Journey.Seed, From = At(l.Journey.From), To = At(l.Journey.To),
            Secret = l.Journey.SecretRoute, Reach = l.Journey.ShowReach,
            Profiles = l.Journey.Profiles.Select(p => new TravelProfileV21Dto { Mode = (int)p.Mode, Miles = p.MilesPerDay, Costs = p.Costs.ToDictionary() }).ToList(),
            Encounters = l.Journey.Encounters.Select(t => new EncounterV21Dto { Terrain = t.Terrain, Region = t.Region, Entries = [.. t.Entries] }).ToList() };
    }
    // Изолированный шаг v20 → v21. Для v4…v20 отсутствие journey оставляет прежние данные и четыре стандартных профиля.
    private static HexLayer ReadTravelV21(HexLayer l, TravelV21Dto? d)
    {
        if (d is null) return l;
        static MapProjectCorruptedException Bad() => new("Повреждены путешествия гексов (v21).");
        HexCoord? At(int[]? a)
        {
            if (a is null) return null;
            if (a.Length != 2 || a[0] < 0 || a[1] < 0 || a[0] >= l.Board.Columns || a[1] >= l.Board.Rows) throw Bad();
            return l.Board.Layout.FromOffset(new HexOffset(a[0], a[1]));
        }
        if (!Enum.IsDefined((TravelMode)d.Mode) || d.Profiles is not { Count: 4 } || d.Encounters is null || d.Encounters.Count > 256) throw Bad();
        var seen = new HashSet<int>();
        foreach (var p in d.Profiles)
        {
            if (p is null || !Enum.IsDefined((TravelMode)p.Mode) || !seen.Add(p.Mode) || !double.IsFinite(p.Miles) || p.Miles is < .1 or > 100_000
                || p.Costs is null || p.Costs.Count > 256 || p.Costs.Any(c => c.Key.Length > 200 || !double.IsFinite(c.Value) || c.Value is < .1 or > 100)) throw Bad();
        }
        foreach (var t in d.Encounters)
            if (t is null || t.Terrain is null || t.Terrain.Length > 200 || t.Region < 0 || t.Region != 0 && l.Region(t.Region) is null
                || t.Entries is not { Count: > 0 and <= 100 } || t.Entries.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1000)) throw Bad();
        return l with { Journey = new HexTravelSettings { Mode = (TravelMode)d.Mode, Seed = d.Seed, From = At(d.From), To = At(d.To),
            SecretRoute = d.Secret, ShowReach = d.Reach,
            Profiles = d.Profiles.Select(p => new TravelProfile((TravelMode)p.Mode, p.Miles, p.Costs.ToImmutableDictionary())).ToImmutableList(),
            Encounters = d.Encounters.Select(t => new EncounterTable(t.Terrain, t.Region, [.. t.Entries])).ToImmutableList() } };
    }
}
internal sealed class TravelV21Dto
{
    public int Mode { get; set; }
    public int Seed { get; set; } = 1801;
    public int[]? From { get; set; }
    public int[]? To { get; set; }
    public bool Secret { get; set; } = true;
    public bool Reach { get; set; }
    public List<TravelProfileV21Dto> Profiles { get; set; } = [];
    public List<EncounterV21Dto> Encounters { get; set; } = [];
}
internal sealed class TravelProfileV21Dto
{
    public int Mode { get; set; }
    public double Miles { get; set; }
    public Dictionary<string, double> Costs { get; set; } = [];
}
internal sealed class EncounterV21Dto
{
    public string Terrain { get; set; } = "";
    public int Region { get; set; }
    public List<string> Entries { get; set; } = [];
}
