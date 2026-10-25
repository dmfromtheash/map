using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The Стили section (R09, DEC-059): one switch for the whole style, the preset applied with one
/// click (one Undo step), its overall strength, and the medium's effects - each with its own
/// switch and strength, as in Свет. A slider being dragged only previews; its release is one step.
/// </summary>
public sealed partial class EditorViewModel
{
    private MapStyle? _stylePreview;
    private MapDocument? _stylePreviewBase;

    /// <summary>The map's style as edited (the first preset, switched off, for a map that never used it).</summary>
    public MapStyle Style => Session.Document.Style ?? MapStyle.For(MapStylePreset.InkWetBrush) with { Enabled = false };

    /// <summary>The style shown right now: a slider's preview while it is dragged, else the document's.</summary>
    public MapStyle ShownStyle => ValidStylePreview ?? Style;

    private MapStyle? ValidStylePreview => ReferenceEquals(_stylePreviewBase, Session.Document) ? _stylePreview : null;

    /// <summary>Applies a change to the style: a preview while dragging, or one Undo step on release.</summary>
    public void ChangeStyle(string name, Func<MapStyle, MapStyle> change, bool commit)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(ShownStyle).Normalized();
        if (!commit)
        {
            _stylePreview = next;
            _stylePreviewBase = Session.Document;
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _stylePreview = null;
        _stylePreviewBase = null;
        if (Session.Document.Style == next) { SceneChanged?.Invoke(this, EventArgs.Empty); return; }
        Session.Execute(new SetMapStyleCommand(name, Session.Document.Style, next));
        AfterEdit();
        Raise(nameof(Style));
    }

    /// <summary>
    /// «Применить стиль»: the preset as it comes, switched on, in one Undo step. Applying the preset
    /// already shown brings back its own effect settings; the overall strength is kept.
    /// </summary>
    public void ApplyStylePreset(MapStylePreset preset) =>
        ChangeStyle("Стиль «" + PresetTitle(preset) + "»",
            s => MapStyle.For(preset) with { Strength = s.Enabled && s.Strength > 0 ? s.Strength : 1 }, true);

    public void SetStyleEnabled(bool on) => ChangeStyle(on ? "Стиль включён" : "Стиль выключен", s => s with { Enabled = on }, true);

    public void SetStyleStrength(double strength, bool commit) =>
        ChangeStyle("Сила стиля", s => s with { Strength = strength, Enabled = true }, commit);

    public void SetStyleEffect(StyleEffectKind kind, bool? enabled, double? strength, bool commit) =>
        ChangeStyle(StyleEffectTitle(kind, ShownStyle.Preset), s =>
        {
            var current = s.Effect(kind);
            return s.WithEffect(kind, new StyleEffect(enabled ?? current.Enabled, strength ?? current.Strength));
        }, commit);

    public static string PresetTitle(MapStylePreset preset) => preset switch
    {
        MapStylePreset.OilAllaPrima => "Алла прима",
        MapStylePreset.OilImpastoRhythm => "Ритм импасто",
        MapStylePreset.CharcoalVineStump => "Ивовый уголь",
        MapStylePreset.CharcoalHatching => "Штриховка углём",
        MapStylePreset.BlueprintSurvey => "Топографическая синька",
        MapStylePreset.WatercolorBigShapes => "Крупные пятна",
        MapStylePreset.UkiyoeSosakuHanga => "Сосаку-ханга",
        MapStylePreset.PrintLetterpress => "Высокая печать",
        MapStylePreset.PrintTintedLithograph => "Литография с тоном",
        MapStylePreset.OldMapBlaeu => "Атлас Блау",
        MapStylePreset.EngravingAquatint => "Офорт с акватинтой",
        MapStylePreset.InkPenHatching => "Перо, штриховка",
        MapStylePreset.InkPenStipple => "Перо, точки",
        _ => "Суми-э, мокрая кисть",
    };

    public static string StyleFamilyTitle(MapStylePreset preset) => preset switch
    {
        MapStylePreset.OilAllaPrima or MapStylePreset.OilImpastoRhythm => "Масло",
        MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Уголь",
        MapStylePreset.BlueprintSurvey => "Синька",
        MapStylePreset.WatercolorBigShapes => "Акварель",
        MapStylePreset.UkiyoeSosakuHanga => "Укиё-э",
        MapStylePreset.PrintLetterpress or MapStylePreset.PrintTintedLithograph => "Печать",
        MapStylePreset.OldMapBlaeu => "Старая карта",
        MapStylePreset.EngravingAquatint => "Гравюра",
        _ => "Тушь",
    };

    public static string StyleEffectTitle(StyleEffectKind kind, MapStylePreset preset = MapStylePreset.InkWetBrush) => kind switch
    {
        StyleEffectKind.Grid => "Координатная сетка",
        StyleEffectKind.FormLines => "Горизонтали формы",
        StyleEffectKind.Crest when preset == MapStylePreset.BlueprintSurvey => "Гребни и силуэт",
        StyleEffectKind.WaterLines when preset == MapStylePreset.BlueprintSurvey => "Линии глубин",
        StyleEffectKind.Paper when preset == MapStylePreset.BlueprintSurvey => "Пятна на синьке",
        StyleEffectKind.Smudge => "Растушёвка",
        StyleEffectKind.CrossHatch => "Штриховка накрест",
        StyleEffectKind.Strokes when preset == MapStylePreset.CharcoalVineStump => "Штрихи по форме",
        StyleEffectKind.Strokes when preset == MapStylePreset.CharcoalHatching => "Штриховка по форме",
        StyleEffectKind.Crest when preset == MapStylePreset.CharcoalVineStump => "Контур и тёмные акценты",
        StyleEffectKind.WaterLines when preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Линии воды",
        StyleEffectKind.Paper when preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Зерно бумаги",
        StyleEffectKind.Impasto => "Рельеф краски",
        StyleEffectKind.Imprimatura when preset == MapStylePreset.OilImpastoRhythm => "Бороздки между штрихами",
        StyleEffectKind.Imprimatura => "Грунт между мазками",
        StyleEffectKind.Gloss => "Влажный блик",
        StyleEffectKind.Crest when preset == MapStylePreset.OilAllaPrima => "Тёмные акценты гребней",
        StyleEffectKind.Paper when preset == MapStylePreset.OilAllaPrima => "Холст",
        StyleEffectKind.Bleed when preset == MapStylePreset.WatercolorBigShapes => "Мокрая заливка",
        StyleEffectKind.Edges when preset == MapStylePreset.WatercolorBigShapes => "Засохший край",
        StyleEffectKind.Sea when preset == MapStylePreset.WatercolorBigShapes => "Море и берег",
        StyleEffectKind.Sea when preset == MapStylePreset.PrintTintedLithograph => "Море",
        StyleEffectKind.Contour when preset == MapStylePreset.UkiyoeSosakuHanga => "Контур силуэта",
        StyleEffectKind.Contour when preset is MapStylePreset.PrintLetterpress or MapStylePreset.PrintTintedLithograph => "Контур",
        StyleEffectKind.Paper when preset >= MapStylePreset.WatercolorBigShapes => "Бумага",
        StyleEffectKind.Granulation => "Грануляция",
        StyleEffectKind.Crest => "Линии гребня",
        StyleEffectKind.Gouge => "Срезы резца",
        StyleEffectKind.SeaCuts => "Срезы в море",
        StyleEffectKind.WoodGrain => "Зерно доски",
        StyleEffectKind.InkSqueeze => "Выдавленная кромка",
        StyleEffectKind.Starved => "Голодная середина",
        StyleEffectKind.WaterLines => "Линии воды",
        StyleEffectKind.StoneCrayon => "Карандаш по камню",
        StyleEffectKind.TintPlate => "Тоновая доска",
        StyleEffectKind.Scraped => "Процарапанные блики",
        StyleEffectKind.Sea => "Рисунок моря",
        StyleEffectKind.Coast => "Линия берега",
        StyleEffectKind.ColourWash => "Ручная раскраска",
        StyleEffectKind.Aquatint => "Акватинта склонов",
        StyleEffectKind.Plate => "Тиснение доски",
        StyleEffectKind.Border => "Рамка атласа",
        StyleEffectKind.Pen when preset == MapStylePreset.OldMapBlaeu => "Гравированные штрихи",
        StyleEffectKind.Pen when preset == MapStylePreset.EngravingAquatint => "Свободные штрихи",
        StyleEffectKind.Contour when preset == MapStylePreset.EngravingAquatint => "Контур и главные линии",
        StyleEffectKind.Paper when preset is MapStylePreset.OldMapBlaeu or MapStylePreset.EngravingAquatint => "Бумага и следы печати",
        StyleEffectKind.Pen when preset == MapStylePreset.InkPenStipple => "Точки пера",
        StyleEffectKind.Strokes => "Мазки по форме",
        StyleEffectKind.Bleed => "Растекание туши",
        StyleEffectKind.Edges => "Кайма высохшей воды",
        StyleEffectKind.DryBrush => "Сухая кисть",
        StyleEffectKind.Mist => "Туманные просветы",
        StyleEffectKind.Pen => "Штрих пера",
        StyleEffectKind.Paper => "Волокна бумаги",
        _ => "Контур гор",
    };
}
