using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Cartographic decorations and the map scale (R08, S5/I18): scale bar, compass rose, cartouche,
/// live legend, plus «Измерить» for distances in map units. Every change is one Undo step; the
/// editing session refreshes scale bars and legends after each edit.
/// </summary>
public sealed partial class EditorViewModel
{
    public MapScale? MapScale => Session.Document.Canvas.Scale;

    public DecorationStyle? SelectedDecoration => Session.SelectedObject?.Decoration;

    public static string DecorationTitle(DecorationKind kind) => DecorationStyle.DefaultName(kind);

    public static IReadOnlyList<string> DecorationVariantTitles(DecorationKind kind) => kind switch
    {
        DecorationKind.Compass => ["Восемь лучей", "Четыре луча", "Шестнадцать лучей", "Стрелка", "Портолан"],
        DecorationKind.Cartouche => ["Рамка", "Лента", "Овал"],
        DecorationKind.ScaleBar => ["Шашки", "Штрихи"],
        _ => [],
    };

    /// <summary>Distance per 100 px in map units — the number the panel shows, or null without a scale.</summary>
    public double? MapScalePer100Pixels => MapScale is { } scale ? Math.Round(scale.DistancePerPixel * 100, 6) : null;

    /// <summary>Sets «100 px = <paramref name="distancePer100Pixels"/> <paramref name="unit"/>»; 0 or less clears the scale.</summary>
    public bool SetMapScale(double distancePer100Pixels, string unit)
    {
        var before = Session.Document.Canvas.Scale;
        MapScale? after = null;
        if (double.IsFinite(distancePer100Pixels) && distancePer100Pixels > 0)
        {
            if (string.IsNullOrWhiteSpace(unit)) { StatusDetail = "Укажите единицы масштаба."; return false; }
            after = new MapScale(distancePer100Pixels / 100, unit);
        }

        if (Equals(before, after)) return true;
        Session.Execute(new SetMapScaleCommand(before, after));
        AfterEdit();
        StatusDetail = after is null ? "Масштаб снят: карта меряется в пикселях." : "Масштаб: 100 px = " + after.Format(100) + ".";
        return Equals(Session.Document.Canvas.Scale, after);
    }

    /// <summary>Adds a decoration in the middle of the view, sized to the view, and selects it.</summary>
    public bool AddDecoration(DecorationKind kind)
    {
        if ((ActiveObjectLayer() ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(l => l is { Visible: true, Locked: false }))
            is not { } target)
        {
            StatusDetail = "Нет доступного слоя объектов для украшения.";
            return false;
        }

        var (cx, cy) = ViewCentre();
        var left = Session.Viewport.ScreenToWorld(0, 0);
        var right = Session.Viewport.ScreenToWorld(Session.Viewport.ViewWidth, 0);
        var view = Math.Abs(right.X - left.X);
        if (!double.IsFinite(view) || view <= 0) view = Math.Min(Session.Document.Canvas.Width, Session.Document.Canvas.Height);
        var unit = Math.Clamp(view, 64, 1_000_000);
        var (width, height) = kind switch
        {
            DecorationKind.ScaleBar => (unit * 0.3, unit * 0.06),
            DecorationKind.Compass => (unit * 0.16, unit * 0.16),
            DecorationKind.Cartouche => (unit * 0.36, unit * 0.12),
            _ => (unit * 0.2, unit * 0.28),
        };
        var style = new DecorationStyle
        {
            Kind = kind,
            Title = kind == DecorationKind.Cartouche ? Session.Document.Title : "",
        };
        var item = new SceneObject(ObjectId.New(), SceneObjectKind.Decoration,
            EntityNameGenerator.Object(Session.Document, SceneObjectKind.Decoration, DecorationTitle(kind)),
            cx - width / 2, cy - height / 2, width, height, 0, "#00000000", "#00000000", 0, true, false)
        { Decoration = style };
        Session.Execute(new AddObjectCommand(target.Id, item));
        if (Session.Document.FindLayer(target.Id) is not ObjectLayer added || added.Objects.All(o => o.Id != item.Id)) return false;
        ActiveTool = EditorTool.Select;
        Session.SelectedObjectId = item.Id;
        _activeLayerId = target.Id;
        AfterEdit();
        StatusDetail = DecorationTitle(kind) + " добавлена на карту." + (kind == DecorationKind.ScaleBar && MapScale is null
            ? " Задайте масштаб карты, чтобы линейка показывала километры или мили." : "");
        return true;
    }

    public void SetDecorationVariant(int variant) =>
        EditDecoration("Вид украшения", commit: true, style => style with { Variant = Math.Clamp(variant, 0, 7) });

    public void SetDecorationTitle(string title, bool commit = true) =>
        EditDecoration("Заглавие украшения", commit, style => style with { Title = (title ?? "").Trim() });

    public void SetDecorationSubtitle(string subtitle, bool commit = true) =>
        EditDecoration("Подзаголовок украшения", commit, style => style with { Subtitle = (subtitle ?? "").Trim() });

    public void SetDecorationInk(string color) =>
        EditDecoration("Цвет чернил", commit: true, style => AppearanceColor.Normalize(color) is { } c ? style with { InkColor = c } : style);

    public void SetDecorationPaper(string color) =>
        EditDecoration("Цвет бумаги", commit: true, style => AppearanceColor.Normalize(color) is { } c ? style with { PaperColor = c } : style);

    private void EditDecoration(string name, bool commit, Func<DecorationStyle, DecorationStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Decoration is not { } style) return before;
            var next = change(style);
            return next.Equals(style) ? before : CopyObject(before) with { Decoration = next };
        });

    // ------------------------------------------------------------------ measuring

    /// <summary>The line being measured, in world units; shown on the canvas while the tool is active.</summary>
    public (double X1, double Y1, double X2, double Y2)? MeasureLine { get; private set; }

    public void BeginMeasure(double worldX, double worldY)
    {
        MeasureLine = (worldX, worldY, worldX, worldY);
        Measure(worldX, worldY, worldX, worldY);
    }

    public void UpdateMeasure(double worldX, double worldY)
    {
        if (MeasureLine is not { } line) return;
        MeasureLine = (line.X1, line.Y1, worldX, worldY);
        Measure(line.X1, line.Y1, worldX, worldY);
    }

    /// <summary>Last «Измерить» result, shown in the status line and the tool panel.</summary>
    public string? MeasureText { get; private set; }

    /// <summary>Distance between two world points in map units (pixels without a scale).</summary>
    public string Measure(double x1, double y1, double x2, double y2)
    {
        var pixels = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        MeasureText = MapScale is { } scale
            ? "Расстояние: " + scale.Format(pixels) + " (" + Math.Round(pixels) + " px)"
            : "Расстояние: " + Math.Round(pixels) + " px — задайте масштаб карты для км или миль";
        StatusDetail = MeasureText;
        return MeasureText;
    }
}
