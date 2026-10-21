namespace Map.Core.Documents;

/// <summary>Kinds of map label with a ready look and a matching name generator (R07, S13).</summary>
public enum LabelPreset
{
    Country = 0,
    Region = 1,
    City = 2,
    Village = 3,
    River = 4,
    Mountains = 5,
    Sea = 6,
}

/// <summary>
/// The look of each label kind. A preset changes size, weight, spacing, colours, outline and
/// shadow; the font family, the words and the placement stay the user's.
/// </summary>
public static class LabelPresets
{
    public static IReadOnlyList<LabelPreset> All { get; } = Enum.GetValues<LabelPreset>();

    public static string Title(LabelPreset preset) => preset switch
    {
        LabelPreset.Country => "Страна",
        LabelPreset.Region => "Регион",
        LabelPreset.City => "Город",
        LabelPreset.Village => "Деревня",
        LabelPreset.River => "Река",
        LabelPreset.Mountains => "Горы",
        _ => "Море",
    };

    public static TextStyle Apply(TextStyle text, LabelPreset preset)
    {
        ArgumentNullException.ThrowIfNull(text);
        return preset switch
        {
            LabelPreset.Country => text with
            {
                FontSize = 64, FontWeight = 700, LetterSpacing = 12, FillColor = "#FF3B2A1E",
                OutlineColor = "#E6F4E9D8", OutlineWidth = 3, ShadowOpacity = 0.25, ShadowBlur = 6,
                ShadowOffsetX = 2, ShadowOffsetY = 2,
            },
            LabelPreset.Region => text with
            {
                FontSize = 44, FontWeight = 600, LetterSpacing = 8, FillColor = "#FF4A3424",
                OutlineColor = "#E6F4E9D8", OutlineWidth = 2.5, ShadowOpacity = 0.18, ShadowBlur = 5,
                ShadowOffsetX = 2, ShadowOffsetY = 2,
            },
            LabelPreset.City => text with
            {
                FontSize = 30, FontWeight = 700, LetterSpacing = 1, FillColor = "#FF20242B",
                OutlineColor = "#FFFFFFFF", OutlineWidth = 3, ShadowOpacity = 0.3, ShadowBlur = 3,
                ShadowOffsetX = 1.5, ShadowOffsetY = 1.5,
            },
            LabelPreset.Village => text with
            {
                FontSize = 20, FontWeight = 400, LetterSpacing = 0.5, FillColor = "#FF2F2A24",
                OutlineColor = "#F2FFFFFF", OutlineWidth = 2, ShadowOpacity = 0, ShadowBlur = 2,
            },
            LabelPreset.River => text with
            {
                FontSize = 22, FontWeight = 400, LetterSpacing = 3, FillColor = "#FF2E5C7A",
                OutlineColor = "#CCE8F2F6", OutlineWidth = 2, ShadowOpacity = 0,
            },
            LabelPreset.Mountains => text with
            {
                FontSize = 26, FontWeight = 600, LetterSpacing = 6, FillColor = "#FF4D3F33",
                OutlineColor = "#E6F4E9D8", OutlineWidth = 2, ShadowOpacity = 0.15, ShadowBlur = 4,
                ShadowOffsetX = 1.5, ShadowOffsetY = 1.5,
            },
            _ => text with
            {
                FontSize = 40, FontWeight = 400, LetterSpacing = 14, FillColor = "#FF2B5A78",
                OutlineColor = "#00FFFFFF", OutlineWidth = 0, ShadowOpacity = 0,
            },
        };
    }
}
