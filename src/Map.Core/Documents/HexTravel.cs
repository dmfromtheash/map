using System.Collections.Immutable;

namespace Map.Core.Documents;

public enum TravelMode { Walk, Ride, Cart, Boat }
public enum CampaignSeason { Spring, Summer, Autumn, Winter }

/// <summary>Сохраняемые правила. Расчётные маршруты и кольца в файл не попадают.</summary>
public sealed record TravelProfile(TravelMode Mode, double MilesPerDay, ImmutableDictionary<string, double> Costs)
{
    public string Name => Mode switch { TravelMode.Walk => "Пешком", TravelMode.Ride => "Верхом", TravelMode.Cart => "Телега", _ => "Лодка" };
    public static TravelProfile Default(TravelMode mode) => new(mode, mode switch
    { TravelMode.Ride => 36, TravelMode.Boat => 30, TravelMode.Cart => 18, _ => 24 }, ImmutableDictionary<string, double>.Empty);
}

public sealed record EncounterTable(string Terrain, int Region, ImmutableArray<string> Entries);

public sealed record HexTravelSettings
{
    public ImmutableList<TravelProfile> Profiles { get; init; } = [.. Enum.GetValues<TravelMode>().Select(TravelProfile.Default)];
    public TravelMode Mode { get; init; }
    public int Seed { get; init; } = 1801;
    public ImmutableList<EncounterTable> Encounters { get; init; } = [];
    public HexCoord? From { get; init; }
    public HexCoord? To { get; init; }
    public bool SecretRoute { get; init; } = true;
    public bool ShowReach { get; init; }
    public TravelProfile Profile => Profiles.First(p => p.Mode == Mode);
    public bool IsDefault => Mode == TravelMode.Walk && Seed == 1801 && Encounters.Count == 0 && From is null && To is null
        && SecretRoute && !ShowReach && Profiles.Count == 4 && Profiles.All(p => p.MilesPerDay == TravelProfile.Default(p.Mode).MilesPerDay && p.Costs.Count == 0);
}

/// <summary>Календарь: день 0 — 1 весны, месяцы по 30 дней, год 360 дней.</summary>
public static class CampaignCalendar
{
    public static CampaignSeason Season(int day) => (CampaignSeason)((Math.Max(0, day) % 360) / 90);
    public static string Date(int day) => $"{Math.Max(0, day) % 30 + 1} {Month(Math.Max(0, day) % 360 / 30)}, год {Math.Max(0, day) / 360 + 1} · {SeasonName(Season(day))}";
    private static string Month(int m) => new[] { "первых вод", "ростков", "цветов", "солнца", "жары", "жатвы", "листьев", "туманов", "дождей", "снега", "стужи", "оттепели" }[m];
    public static string SeasonName(CampaignSeason s) => s switch { CampaignSeason.Spring => "весна", CampaignSeason.Summer => "лето", CampaignSeason.Autumn => "осень", _ => "зима" };
    public static double Factor(int day, string terrain) => Season(day) switch
    {
        CampaignSeason.Winter => terrain.Contains("mountain", StringComparison.Ordinal) ? 1.5 : 1.2,
        CampaignSeason.Spring or CampaignSeason.Autumn => 1.3,
        _ => 1,
    };
    public static string Weather(HexLayer layer, HexCoord hex, int day)
    {
        var terrain = layer.Board[hex]?.Terrain ?? "";
        var roll = Stable(hex, layer.Journey.Seed, day) % 3;
        if (Season(day) == CampaignSeason.Winter) return roll == 0 ? "Снегопад" : "Мороз, ясно";
        if (terrain.Contains("desert", StringComparison.Ordinal)) return roll == 0 ? "Песчаный ветер" : "Сухо, жара";
        if (terrain.Contains("swamp", StringComparison.Ordinal)) return roll == 0 ? "Туман" : "Сыро, дождь";
        return roll == 0 ? "Дождь" : roll == 1 ? "Облачно" : "Ясно";
    }
    public static string Encounter(HexLayer layer, HexCoord hex, int day)
    {
        var cell = layer.Board[hex];
        var table = layer.Journey.Encounters.Where(t => (t.Region == 0 || t.Region == cell?.Region)
            && (t.Terrain.Length == 0 || t.Terrain == cell?.Terrain))
            .OrderByDescending(t => (t.Region != 0 ? 2 : 0) + (t.Terrain.Length != 0 ? 1 : 0)).FirstOrDefault();
        if (table is null || table.Entries.IsDefaultOrEmpty) return "Встреч не выпало";
        return table.Entries[(int)(Stable(hex, layer.Journey.Seed, day) % (uint)table.Entries.Length)];
    }
    private static uint Stable(HexCoord h, int seed, int day)
    {
        uint hash = 2166136261;
        foreach (var n in new[] { h.Q, h.R, seed, day })
            for (var b = 0; b < 4; b++) hash = unchecked((hash ^ (byte)(n >> (b * 8))) * 16777619);
        return hash;
    }
}
