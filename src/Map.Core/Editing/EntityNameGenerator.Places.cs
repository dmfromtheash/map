using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Place names for map labels (R07, S13): Russian-sounding fantasy names built from syllables,
/// shaped by the label kind (a country ends like a country, a river like a river). Deterministic
/// for a seed; never repeats a name some text of the document already shows.
/// </summary>
public static partial class EntityNameGenerator
{
    private static readonly string[] Starts =
    [
        "Аль", "Бер", "Вел", "Гар", "Дор", "Ель", "Зар", "Иль", "Кар", "Лис", "Мир", "Нор", "Ост", "Пер",
        "Рав", "Сол", "Тар", "Ур", "Фел", "Хол", "Чер", "Яр", "Сев", "Бел", "Вор", "Лад", "Мор", "Сит",
    ];

    private static readonly string[] Middles =
    [
        "а", "е", "и", "о", "ин", "ер", "ан", "ов", "ел", "ор", "ив", "ен", "ар", "ул",
    ];

    public static string PlaceName(MapDocument document, LabelPreset kind, int seed)
    {
        ArgumentNullException.ThrowIfNull(document);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        foreach (var item in layer.Objects)
        {
            if (item.Text is { } text) used.Add(text.Content.Trim());
        }

        var state = (uint)seed * 2654435761u ^ (uint)kind * 40503u ^ 0x9E3779B9u;
        uint Next()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return state;
        }

        string Pick(string[] items) => items[(int)(Next() % (uint)items.Length)];
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var stem = Pick(Starts) + (Next() % 3 == 0 ? Pick(Middles) : "");
            var name = Shape(kind, stem, Next());
            if (!used.Contains(name)) return name;
        }

        return Shape(kind, Pick(Starts), Next()) + " " + (seed & 0xFFFF);
    }

    private static string Shape(LabelPreset kind, string stem, uint roll)
    {
        string Of(params string[] endings) => Join(stem, endings[(int)(roll % (uint)endings.Length)]);
        return kind switch
        {
            LabelPreset.Country => Of("ия", "ания", "ор", "енмарк", "ланд", "ея"),
            LabelPreset.Region => Of("ье", "щина", "ский край", "ские пределы"),
            LabelPreset.City => Of("град", "бург", "ск", "поль", "мост", "гард"),
            LabelPreset.Village => Of("ки", "ово", "ино", "ец", "ица", "ня"),
            LabelPreset.River => Of("а", "ва", "ица", "ея", "ыня", "ь"),
            LabelPreset.Mountains => Of("ские горы", "ский хребет", "ские пики"),
            _ => Of("ское море", "ский залив", "ское взморье"),
        };
    }

    private static string Join(string stem, string ending)
    {
        // Two vowels meeting at the seam read badly ("Ала" + "ания"): drop the stem's last vowel.
        const string vowels = "аеёиоуыэюя";
        if (stem.Length > 2 && vowels.Contains(char.ToLowerInvariant(stem[^1])) && vowels.Contains(char.ToLowerInvariant(ending[0])))
            stem = stem[..^1];
        return stem + ending;
    }
}
