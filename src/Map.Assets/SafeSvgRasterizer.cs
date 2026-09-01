using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace Map.Assets;

/// <summary>
/// Bounded clean-room SVG admission for user imports. This is deliberately not a general SVG
/// engine: accepted static geometry is rendered to PNG before it can enter the catalog.
/// </summary>
public static partial class SafeSvgRasterizer
{
    public const int MaxEncodedBytes = 1 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public const long MaxPixels = 16L * 1024 * 1024;
    public const int MaxElements = 4096;
    public const int MaxDepth = 32;
    public const int MaxPathCharacters = 256 * 1024;

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "xmlns", "width", "height", "viewBox", "fill", "stroke", "stroke-width", "opacity",
        "fill-opacity", "stroke-opacity", "transform", "d", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "points",
    };

    [GeneratedRegex(@"(?<name>translate|scale|rotate)\s*\((?<args>[^)]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TransformRegex();

    public static byte[] RasterizeFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaxEncodedBytes)
        {
            throw new InvalidDataException("SVG превышает допустимый размер 1 MiB.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxEncodedBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        return Rasterize(document);
    }

    public static byte[] Rasterize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg")
        {
            throw new InvalidDataException("Ожидается корневой элемент svg.");
        }

        Admit(root);
        var viewBox = ReadViewBox(root);
        var width = ReadDimension(root, "width", viewBox.Width);
        var height = ReadDimension(root, "height", viewBox.Height);
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension
            || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException("SVG имеет неподдерживаемый размер растра.");
        }

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Не удалось создать поверхность для SVG.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(width / viewBox.Width, height / viewBox.Height);
        surface.Canvas.Translate(-viewBox.Left, -viewBox.Top);
        DrawChildren(surface.Canvas, root, new PaintStyle("#FF000000", "none", 1, 1, 1, 1));
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Не удалось кодировать SVG как PNG.");
        return png.ToArray();
    }

    private static void Admit(XElement root)
    {
        var elements = root.DescendantsAndSelf().ToList();
        if (elements.Count > MaxElements)
        {
            throw new InvalidDataException("SVG содержит слишком много элементов.");
        }

        foreach (var element in elements)
        {
            if (!AllowedElements.Contains(element.Name.LocalName)
                || element.Ancestors().Count() >= MaxDepth)
            {
                throw new InvalidDataException($"SVG-элемент {element.Name.LocalName} не поддерживается.");
            }

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    if (!string.Equals(
                            attribute.Value, "http://www.w3.org/2000/svg",
                            StringComparison.Ordinal))
                        throw new InvalidDataException("SVG namespace не поддерживается.");
                    continue;
                }

                var name = attribute.Name.LocalName;
                var value = attribute.Value.Trim();
                if (!AllowedAttributes.Contains(name)
                    || name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("url(", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("data:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("file:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("http:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("https:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('#') && (name is "href" or "xlink:href"))
                {
                    throw new InvalidDataException($"SVG-атрибут {name} не поддерживается.");
                }
            }

            if ((string?)element.Attribute("d") is { Length: > MaxPathCharacters })
            {
                throw new InvalidDataException("SVG path превышает допустимую длину.");
            }
        }
    }

    private static void DrawChildren(SKCanvas canvas, XElement parent, PaintStyle inherited)
    {
        foreach (var element in parent.Elements())
        {
            var style = ReadStyle(element, inherited);
            canvas.Save();
            if ((string?)element.Attribute("transform") is { Length: > 0 } transform)
            {
                ApplyTransform(canvas, transform);
            }

            if (element.Name.LocalName == "g")
            {
                DrawChildren(canvas, element, style);
            }
            else
            {
                using var path = BuildPath(element);
                DrawPath(canvas, path, style);
            }
            canvas.Restore();
        }
    }

    private static SKPath BuildPath(XElement element)
    {
        var path = new SKPath();
        switch (element.Name.LocalName)
        {
            case "path":
                return SKPath.ParseSvgPathData(Required(element, "d"))
                    ?? throw new InvalidDataException("SVG path не распознан.");
            case "rect":
            {
                var x = Number(element, "x"); var y = Number(element, "y");
                var width = Positive(element, "width"); var height = Positive(element, "height");
                path.AddRect(new SKRect(x, y, x + width, y + height));
                break;
            }
            case "circle":
                path.AddCircle(Number(element, "cx"), Number(element, "cy"), Positive(element, "r"));
                break;
            case "ellipse":
                path.AddOval(new SKRect(
                    Number(element, "cx") - Positive(element, "rx"),
                    Number(element, "cy") - Positive(element, "ry"),
                    Number(element, "cx") + Positive(element, "rx"),
                    Number(element, "cy") + Positive(element, "ry")));
                break;
            case "line":
                path.MoveTo(Number(element, "x1"), Number(element, "y1"));
                path.LineTo(Number(element, "x2"), Number(element, "y2"));
                break;
            case "polyline":
            case "polygon":
            {
                var points = Points(Required(element, "points"));
                if (points.Count < 2 || element.Name.LocalName == "polygon" && points.Count < 3)
                    throw new InvalidDataException("SVG polygon/polyline содержит недостаточно точек.");
                path.MoveTo(points[0]);
                for (var index = 1; index < points.Count; index++) path.LineTo(points[index]);
                if (element.Name.LocalName == "polygon") path.Close();
                break;
            }
            default:
                throw new InvalidDataException($"SVG-элемент {element.Name.LocalName} не поддерживается.");
        }
        return path;
    }

    private static void DrawPath(SKCanvas canvas, SKPath path, PaintStyle style)
    {
        if (!string.Equals(style.Fill, "none", StringComparison.OrdinalIgnoreCase))
        {
            using var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = Color(style.Fill).WithAlpha((byte)Math.Round(255 * style.Opacity * style.FillOpacity)),
            };
            canvas.DrawPath(path, fill);
        }
        if (!string.Equals(style.Stroke, "none", StringComparison.OrdinalIgnoreCase)
            && style.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = style.StrokeWidth,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = Color(style.Stroke).WithAlpha((byte)Math.Round(255 * style.Opacity * style.StrokeOpacity)),
            };
            canvas.DrawPath(path, stroke);
        }
    }

    private static PaintStyle ReadStyle(XElement element, PaintStyle inherited) => new(
        (string?)element.Attribute("fill") ?? inherited.Fill,
        (string?)element.Attribute("stroke") ?? inherited.Stroke,
        OptionalNumber(element, "stroke-width", inherited.StrokeWidth, 0, MaxDimension),
        OptionalNumber(element, "opacity", inherited.Opacity, 0, 1),
        OptionalNumber(element, "fill-opacity", inherited.FillOpacity, 0, 1),
        OptionalNumber(element, "stroke-opacity", inherited.StrokeOpacity, 0, 1));

    private static void ApplyTransform(SKCanvas canvas, string value)
    {
        var matches = TransformRegex().Matches(value);
        if (matches.Count == 0 || string.Concat(matches.Select(match => match.Value))
                .Replace(" ", "", StringComparison.Ordinal) != value.Replace(" ", "", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SVG transform поддерживает только translate, scale и rotate.");
        }
        foreach (Match match in matches)
        {
            var args = Numbers(match.Groups["args"].Value);
            switch (match.Groups["name"].Value.ToLowerInvariant())
            {
                case "translate" when args.Count is 1 or 2:
                    canvas.Translate(args[0], args.Count == 2 ? args[1] : 0);
                    break;
                case "scale" when args.Count is 1 or 2:
                    canvas.Scale(args[0], args.Count == 2 ? args[1] : args[0]);
                    break;
                case "rotate" when args.Count is 1 or 3:
                    canvas.RotateDegrees(args[0], args.Count == 3 ? args[1] : 0, args.Count == 3 ? args[2] : 0);
                    break;
                default:
                    throw new InvalidDataException("SVG transform имеет неподдерживаемые параметры.");
            }
        }
    }

    private static (float Left, float Top, float Width, float Height) ReadViewBox(XElement root)
    {
        if ((string?)root.Attribute("viewBox") is { Length: > 0 } raw)
        {
            var values = Numbers(raw);
            if (values.Count != 4 || values[2] <= 0 || values[3] <= 0
                || values[2] > MaxDimension || values[3] > MaxDimension)
                throw new InvalidDataException("SVG viewBox не поддерживается.");
            return (values[0], values[1], values[2], values[3]);
        }
        var width = Positive(root, "width"); var height = Positive(root, "height");
        return (0, 0, width, height);
    }

    private static int ReadDimension(XElement root, string name, float fallback)
    {
        var raw = (string?)root.Attribute(name);
        var value = raw is null ? fallback : ParseNumber(raw, name);
        return checked((int)Math.Ceiling(value));
    }

    private static string Required(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value : throw new InvalidDataException($"SVG-атрибут {name} обязателен.");

    private static float Number(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value ? ParseNumber(value, name) : 0;

    private static float Positive(XElement element, string name)
    {
        var value = ParseNumber(Required(element, name), name);
        return value > 0 ? value : throw new InvalidDataException($"SVG-атрибут {name} должен быть положительным.");
    }

    private static float OptionalNumber(XElement element, string name, float fallback, float min, float max)
    {
        if ((string?)element.Attribute(name) is not { Length: > 0 } raw) return fallback;
        var value = ParseNumber(raw, name);
        return value >= min && value <= max ? value
            : throw new InvalidDataException($"SVG-атрибут {name} вне допустимого диапазона.");
    }

    private static float ParseNumber(string value, string name)
    {
        value = value.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !float.IsFinite(number) || Math.Abs(number) > 1_000_000)
            throw new InvalidDataException($"SVG-атрибут {name} не является допустимым числом.");
        return number;
    }

    private static List<float> Numbers(string raw) => raw
        .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(value => ParseNumber(value, "список чисел")).ToList();

    private static List<SKPoint> Points(string raw)
    {
        var values = Numbers(raw);
        if (values.Count > 8192 || values.Count % 2 != 0)
            throw new InvalidDataException("SVG points не поддерживается.");
        var points = new List<SKPoint>(values.Count / 2);
        for (var index = 0; index < values.Count; index += 2)
            points.Add(new SKPoint(values[index], values[index + 1]));
        return points;
    }

    private static SKColor Color(string value)
    {
        try { return SKColor.Parse(value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidDataException("SVG использует неподдерживаемый solid color.", ex);
        }
    }

    private sealed record PaintStyle(
        string Fill, string Stroke, float StrokeWidth, float Opacity, float FillOpacity, float StrokeOpacity);
}
