using System.Text.RegularExpressions;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Deterministic Russian names for newly committed entities. Callers invoke this only at commit;
/// transient pointer previews never scan a large scene.
/// </summary>
public static partial class EntityNameGenerator
{
    public static string Object(MapDocument document, SceneObjectKind kind, string? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var names = document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var basis = Normalize(preferred, "Штамп");
            return Unique(names, basis, suffixFirst: false);
        }

        var label = kind switch
        {
            SceneObjectKind.Ellipse => "Эллипс",
            SceneObjectKind.Rectangle => "Прямоугольник",
            SceneObjectKind.Line => "Линия",
            SceneObjectKind.Shape => "Фигура",
            SceneObjectKind.Text => "Текст",
            SceneObjectKind.Composition => "Композиция",
            _ => "Объект",
        };
        return Unique(names, label, suffixFirst: true);
    }

    public static IReadOnlyList<string> Objects(
        MapDocument document, SceneObjectKind kind, int count, string? preferred = null)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var names = document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var label = !string.IsNullOrWhiteSpace(preferred)
            ? Normalize(preferred, "Штамп")
            : kind switch
            {
                SceneObjectKind.Ellipse => "Эллипс",
                SceneObjectKind.Rectangle => "Прямоугольник",
                SceneObjectKind.Line => "Линия",
                SceneObjectKind.Shape => "Фигура",
                SceneObjectKind.Text => "Текст",
                _ => "Объект",
            };
        var result = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var name = Unique(names, label, suffixFirst: string.IsNullOrWhiteSpace(preferred));
            names.Add(name);
            result.Add(name);
        }

        return result;
    }

    public static string Text(MapDocument document, string? content)
    {
        var normalized = Normalize(content, "");
        if (normalized.Length == 0)
        {
            return Object(document, SceneObjectKind.Text);
        }

        var shortName = normalized.Length <= 36 ? normalized : normalized[..36].TrimEnd() + "…";
        return Object(document, SceneObjectKind.Text, shortName);
    }

    public static string Layer(MapDocument document, Type layerType)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layerType);
        var label = layerType == typeof(ObjectLayer) ? "Объекты"
            : layerType == typeof(BrushLayer) ? "Кисть"
            : layerType == typeof(LandLayer) ? "Земля"
            : layerType == typeof(GridLayer) ? "Сетка"
            : layerType == typeof(FilterLayer) ? "Фильтр"
            : "Слой";
        return Unique(
            document.Layers.Select(layer => layer.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            label,
            suffixFirst: true);
    }

    public static string Group(int objectCount) => $"Группа · {Math.Max(0, objectCount)} объектов";

    private static string Unique(HashSet<string> names, string basis, bool suffixFirst)
    {
        if (!suffixFirst && !names.Contains(basis))
        {
            return basis;
        }

        for (var number = 1; number < int.MaxValue; number++)
        {
            var candidate = $"{basis} {number}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Не удалось подобрать свободное имя.");
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = Whitespace().Replace(value?.Trim() ?? "", " ");
        return normalized.Length == 0 ? fallback : normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
