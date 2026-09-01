using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Every user-facing word of the editor in one place. Enum members, blend keys, tool identifiers
/// and internal ids never reach the screen raw: they pass through here and come out as plain
/// Russian. Keeping this in the presentation layer makes the wording testable without a window.
/// </summary>
public static class EditorLabels
{
    // ------------------------------------------------------------------- tools

    public static string Tool(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Выбор",
        EditorTool.Line => "Линия",
        EditorTool.Shape => "Фигура",
        EditorTool.Text => "Текст",
        EditorTool.Brush => "Кисть",
        EditorTool.Mask => "Маска",
        EditorTool.Land => "Земля",
        EditorTool.Stamp => "Штамп",
        EditorTool.Notes => "Заметки",
        _ => "Инструмент",
    };

    /// <summary>One sentence telling a first-time user what this tool expects them to do.</summary>
    public static string ToolHint(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Щёлкните объект, чтобы выбрать его. Перетаскивание — переместить, углы — размер.",
        EditorTool.Line => "Протяните по холсту, чтобы нарисовать линию. Узлы можно двигать после.",
        EditorTool.Shape => "Добавьте фигуру и меняйте её контур. Несколько фигур можно объединить.",
        EditorTool.Text => "Добавьте подпись и настройте её начертание.",
        EditorTool.Brush => "Рисуйте по холсту. Размер и непрозрачность — слева.",
        EditorTool.Mask => "Рисуйте по маске активного слоя: скрывайте или возвращайте участки.",
        EditorTool.Land => "Протяните по холсту, чтобы добавить сушу. Режим «Убрать» стирает её.",
        EditorTool.Stamp => "Выберите набор в библиотеке и щёлкните по холсту. Область и плотность настраиваются слева.",
        EditorTool.Notes => "Создавайте заметки карты или привязывайте их к выделенному объекту.",
        _ => "",
    };

    // ------------------------------------------------------------------ layers

    public static string LayerKind(MapLayer layer) => layer switch
    {
        BaseColorLayer => "Фон",
        ObjectLayer => "Объекты",
        BrushLayer => "Кисть",
        LandLayer => "Земля",
        GridLayer => "Сетка",
        FilterLayer => "Фильтр",
        _ => "Слой",
    };

    /// <summary>Verb that states what the command will do to the layer's current state.</summary>
    public static string VisibilityCommand(bool visible) => visible ? "Скрыть слой" : "Показать слой";

    public static string LockCommand(bool locked) => locked ? "Снять блокировку" : "Заблокировать слой";

    // ----------------------------------------------------------------- objects

    /// <summary>
    /// How an object is announced. A freshly added object is named after its kind, and repeating
    /// the word twice ("Линия: Линия") reads like a bug, so the kind is added only when the name
    /// does not already carry it.
    /// </summary>
    public static string DescribeObject(SceneObject sceneObject)
    {
        var kind = ObjectKind(sceneObject.Kind);
        return string.Equals(kind, sceneObject.Name, StringComparison.OrdinalIgnoreCase)
            ? sceneObject.Name
            : $"{sceneObject.Name} · {kind}";
    }

    /// <summary>The kind, or null when the name already says it. Used as a list row's subtitle.</summary>
    public static string? ObjectSubtitle(SceneObject sceneObject)
    {
        var kind = ObjectKind(sceneObject.Kind);
        return string.Equals(kind, sceneObject.Name, StringComparison.OrdinalIgnoreCase) ? null : kind;
    }

    public static string ObjectVisibilityValue(bool visible) => visible ? "Показан" : "Скрыт";

    public static string ObjectLockValue(bool locked) => locked ? "Заблокирован" : "Свободен";

    public static string ObjectKind(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Rectangle => "Прямоугольник",
        SceneObjectKind.Ellipse => "Эллипс",
        SceneObjectKind.Stamp => "Штамп",
        SceneObjectKind.Line => "Линия",
        SceneObjectKind.Shape => "Фигура",
        SceneObjectKind.Text => "Текст",
        SceneObjectKind.Composition => "Композиция",
        SceneObjectKind.Placeholder => "Заменитель",
        _ => "Объект",
    };

    // ------------------------------------------------------------------ values

    /// <summary>Blend keys are stored in English in the document; users never see them.</summary>
    public static string Blend(string blend) => blend switch
    {
        "Normal" => "Обычное",
        "Multiply" => "Умножение",
        "Screen" => "Осветление",
        "Overlay" => "Перекрытие",
        "Darken" => "Затемнение",
        "Lighten" => "Замена светлым",
        "SoftLight" => "Мягкий свет",
        "HardLight" => "Жёсткий свет",
        "Difference" => "Разница",
        "Add" => "Сложение",
        _ => blend,
    };

    public static string Pattern(StrokePattern pattern) => pattern switch
    {
        Core.Documents.StrokePattern.Dotted => "Точками",
        Core.Documents.StrokePattern.Dashed => "Штрихами",
        _ => "Сплошная",
    };

    public static string Cap(VectorCap cap) => cap switch
    {
        VectorCap.Round => "Скруглённый",
        VectorCap.Square => "Квадратный",
        _ => "Срез",
    };

    public static string Alignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "По центру",
        TextAlignment.Right => "Справа",
        _ => "Слева",
    };

    /// <summary>The named state of a drop shadow: off, the standard preset, or custom values.</summary>
    public static string ShadowState(double offsetX, double offsetY, double blur, double opacity) =>
        opacity <= 0
            ? "Нет"
            : ShadowPreset.IsStandard(offsetX, offsetY, blur, opacity) ? "Стандартная" : "Настроенная";

    public static string MaskMode(MaskPaintMode mode) =>
        mode == MaskPaintMode.Hide ? "Скрывать" : "Возвращать";

    public static string LandShape(LandShapeKind kind) => kind switch
    {
        LandShapeKind.Circle => "Круг",
        LandShapeKind.Rectangle => "Прямоугольник",
        LandShapeKind.Ellipse => "Эллипс",
        LandShapeKind.Polygon => "Многоугольник",
        LandShapeKind.GridBlock => "Блок по сетке",
        LandShapeKind.BoundedFill => "Заливка области",
        _ => "Форма",
    };

    public static string GridGeometry(GridGeometry geometry) => geometry switch
    {
        Core.Documents.GridGeometry.Square => "Квадратная",
        Core.Documents.GridGeometry.IsometricSquares => "Изометрическая",
        Core.Documents.GridGeometry.HexFlat => "Шестиугольная, плоский верх",
        Core.Documents.GridGeometry.HexPointy => "Шестиугольная, острый верх",
        _ => "Сетка",
    };

    public static string ShapeBoolean(ShapeBooleanOperation operation) => operation switch
    {
        ShapeBooleanOperation.Add => "Объединить",
        ShapeBooleanOperation.Subtract => "Вычесть",
        ShapeBooleanOperation.Merge => "Слить контуры",
        _ => "Операция",
    };

    // ------------------------------------------- current value of a layer setting

    /// <summary>What the layer's mask is doing right now, for a value-showing control.</summary>
    public static string MaskState(MapLayer layer) => layer.Mask is null
        ? "Нет"
        : layer.Mask.Enabled ? "Включена" : "Выключена";

    /// <summary>The land layer's current edge treatment, named rather than numbered.</summary>
    public static string LandEdge(LandLayer layer) =>
        layer.EdgeStyle.RippleCount == 0 ? "Обрыв" : "Берег с прибоем";

    /// <summary>The grid layer's current cell shape; empty layers report honestly.</summary>
    public static string GridCells(GridLayer layer) =>
        layer.Grids.Count == 0 ? "Нет сетки" : GridGeometry(layer.Grids[0].Geometry);

    public static string GridExport(GridLayer layer) =>
        layer.Grids.Count != 0 && layer.Grids[0].IncludeInExport ? "Да" : "Нет";

    /// <summary>
    /// The filter preset in words. Temperature is the axis the editor actually toggles, so it is
    /// the honest thing to name; the user never sees the numeric settings behind it.
    /// </summary>
    public static string FilterPreset(FilterLayer layer) =>
        layer.Settings.Temperature > 0 ? "Тёплый" : "Холодный";

    // ------------------------------------------------------------------ counts

    /// <summary>Russian plural agreement, so the status bar never says «3 изменения» wrongly.</summary>
    public static string Plural(long count, string one, string few, string many)
    {
        var absolute = Math.Abs(count);
        var lastTwo = absolute % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return many;
        }

        return (absolute % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    public static string Changes(long count) =>
        $"{count} {Plural(count, "изменение", "изменения", "изменений")}";

    public static string Objects(int count) =>
        $"{count} {Plural(count, "объект", "объекта", "объектов")}";
}
