using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Map.Core.Documents;

public enum TextAlignment { Left, Center, Right }
public enum NoteAnchorKind { World, Object }

/// <summary>What a note is about (R26), with its own icon in the list.</summary>
public enum NoteCategory { Other, Place, Encounter, Lore, Quest, Secret }
public enum GridGeometry { Square, IsometricSquares, HexFlat, HexPointy }

public sealed record TextStyle
{
    public string Content { get; init; } = "Текст";
    public string RequestedFontFamily { get; init; } = "Arial";
    public string FallbackFontFamily { get; init; } = "Arial";
    public int FontWeight { get; init; } = 400;
    public double FontSize { get; init; } = 32;
    public string FillColor { get; init; } = "#FF20242B";
    public string OutlineColor { get; init; } = "#FFFFFFFF";
    public double OutlineWidth { get; init; }
    public double ShadowOffsetX { get; init; } = 3;
    public double ShadowOffsetY { get; init; } = 3;
    public double ShadowBlur { get; init; } = 4;
    public double ShadowOpacity { get; init; }
    public double Curve { get; init; }
    public double LetterSpacing { get; init; }
    public double LineHeight { get; init; } = 1.2;
    public TextAlignment Alignment { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public double Blur { get; init; }
    public string Blend { get; init; } = "Normal";

    /// <summary>Set when the label follows a river, road or hex path (R07); null for free text.</summary>
    public TextPathBinding? Path { get; init; }
}

public sealed record MapNote
{
    public MapNote(
        NoteId id, string title, string markdown, NoteAnchorKind anchorKind,
        double worldX = 0, double worldY = 0, ObjectId? objectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        NoteMarkdown.Validate(markdown);
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY)
            || anchorKind == NoteAnchorKind.Object && objectId is null)
        {
            throw new ArgumentException("A note anchor must be finite and an object anchor needs an id.");
        }

        Id = id; Title = title; Markdown = markdown; AnchorKind = anchorKind;
        WorldX = worldX; WorldY = worldY; ObjectId = objectId;
    }

    public NoteId Id { get; init; }
    public string Title { get; init; }
    public string Markdown { get; init; }
    public NoteAnchorKind AnchorKind { get; init; }
    public double WorldX { get; init; }
    public double WorldY { get; init; }
    public ObjectId? ObjectId { get; init; }

    /// <summary>A map of the atlas the note leads to (R26, schema v36); null for none.</summary>
    public Guid? LinkedMap { get; init; }

    public NoteCategory Category { get; init; }

    /// <summary>The players may see the note (exports for players include it); otherwise it is the game master's.</summary>
    public bool PlayerVisible { get; init; }

    /// <summary>True when the note carries something of R26 (schema v36).</summary>
    public bool NeedsWikiSchema => LinkedMap is not null || Category != NoteCategory.Other || PlayerVisible;

    public bool IsDetached(MapDocument document) => AnchorKind == NoteAnchorKind.Object
        && (ObjectId is null || document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).All(item => item.Id != ObjectId));
}

public static partial class NoteMarkdown
{
    [GeneratedRegex(@"\[[^\]]*\]\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    public static void Validate(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Contains('<') || markdown.Contains('>'))
        {
            throw new ArgumentException("Notes use a local Markdown subset; raw HTML is not allowed.", nameof(markdown));
        }

        foreach (Match match in LinkRegex().Matches(markdown))
        {
            var target = match.Groups[1].Value.Trim();
            if (!Uri.TryCreate(target, UriKind.RelativeOrAbsolute, out var uri)
                || uri.IsAbsoluteUri && uri.Scheme is not ("http" or "https" or "mailto"))
            {
                throw new ArgumentException("Note links use relative, http, https or mailto targets.", nameof(markdown));
            }
        }
    }

    /// <summary>
    /// Renders the allowed local subset to deterministic plain text for the native preview.
    /// It never resolves a link, loads a resource, interprets HTML, or executes script.
    /// </summary>
    public static string RenderPlain(string markdown)
    {
        Validate(markdown);
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s+", "");
        text = Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "• ");
        text = Regex.Replace(text, @"(?m)^\s*\d+[.)]\s+", "• ");
        text = LinkRegex().Replace(text, match =>
        {
            var label = match.Value[1..match.Value.IndexOf(']')];
            var target = match.Groups[1].Value.Trim();
            return string.IsNullOrWhiteSpace(label) ? target : $"{label} ({target})";
        });
        text = Regex.Replace(text, @"(?<!\\)(\*\*|__)(.+?)\1", "$2");
        text = Regex.Replace(text, @"(?<!\\)(\*|_)(.+?)\1", "$2");
        return text.Replace("\\*", "*", StringComparison.Ordinal)
            .Replace("\\_", "_", StringComparison.Ordinal);
    }
}

public sealed record GridDefinition
{
    public const int MaximumSubdivisions = 64;
    public const double MaximumCellSize = 1_000_000_000;
    public const double MaximumLineWidth = 4096;
    public const double MaximumAbsoluteOffset = 1_000_000_000_000;

    private GridGeometry _geometry;
    private double _cellSize;
    private int _subdivisions = 1;
    private double _lineWidth = 1;
    private string _color = "#668FA7C1";
    private double _opacity = 0.7;
    private double _offsetX;
    private double _offsetY;
    private double _rotationDegrees;

    public GridDefinition(GridId id, GridGeometry geometry, double cellSize)
    {
        Id = id; Geometry = geometry; CellSize = cellSize;
    }

    public GridId Id { get; init; }
    public GridGeometry Geometry
    {
        get => _geometry;
        init
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _geometry = value;
        }
    }

    public double CellSize
    {
        get => _cellSize;
        init
        {
            if (!double.IsFinite(value) || value <= 0 || value > MaximumCellSize)
                throw new ArgumentOutOfRangeException(nameof(value));
            _cellSize = value;
        }
    }

    public int Subdivisions
    {
        get => _subdivisions;
        init
        {
            if (value is < 1 or > MaximumSubdivisions)
                throw new ArgumentOutOfRangeException(nameof(value));
            _subdivisions = value;
        }
    }

    public double LineWidth
    {
        get => _lineWidth;
        init
        {
            if (!double.IsFinite(value) || value <= 0 || value > MaximumLineWidth)
                throw new ArgumentOutOfRangeException(nameof(value));
            _lineWidth = value;
        }
    }

    public string Color
    {
        get => _color;
        init
        {
            if (!IsColor(value)) throw new ArgumentException("A grid colour must be #RRGGBB or #AARRGGBB.", nameof(value));
            _color = value.ToUpperInvariant();
        }
    }

    public double Opacity
    {
        get => _opacity;
        init
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            _opacity = value;
        }
    }

    public double OffsetX
    {
        get => _offsetX;
        init => _offsetX = ValidateOffset(value, nameof(value));
    }

    public double OffsetY
    {
        get => _offsetY;
        init => _offsetY = ValidateOffset(value, nameof(value));
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        init
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _rotationDegrees = value;
        }
    }

    public bool SnapEnabled { get; init; } = true;
    public bool IncludeInExport { get; init; }

    private static bool IsColor(string? value) => value is { Length: 7 or 9 }
        && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static double ValidateOffset(double value, string parameterName)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteOffset)
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

public sealed record GridLayer : MapLayer
{
    public GridLayer(
        LayerId id, string name, bool visible, bool locked, double opacity,
        IReadOnlyList<GridDefinition> grids, LayerMask? mask = null)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentNullException.ThrowIfNull(grids);
        if (grids.Select(grid => grid.Id).Distinct().Count() != grids.Count)
            throw new ArgumentException("Grid ids are unique within a layer.", nameof(grids));
        Grids = new ReadOnlyCollection<GridDefinition>([.. grids]);
        Mask = mask;
    }

    public IReadOnlyList<GridDefinition> Grids { get; init; }
    public override LayerMask? Mask { get; init; }
    public override MapLayer WithNewIds() => this with
    {
        Id = LayerId.New(),
        Grids = new ReadOnlyCollection<GridDefinition>([.. Grids.Select(grid => grid with { Id = GridId.New() })]),
    };
    public override MapLayer WithMask(LayerMask? mask) => this with { Mask = mask };
}
