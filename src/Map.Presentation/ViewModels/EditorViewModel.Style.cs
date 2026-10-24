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
        MapStylePreset.InkPenHatching => "Перо, штриховка",
        MapStylePreset.InkPenStipple => "Перо, точки",
        _ => "Суми-э, мокрая кисть",
    };

    public static string StyleEffectTitle(StyleEffectKind kind, MapStylePreset preset = MapStylePreset.InkWetBrush) => kind switch
    {
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
