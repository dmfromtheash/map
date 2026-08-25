using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Map.Core.Documents;

public enum TextAlignment { Left, Center, Right }
public enum NoteAnchorKind { World, Object }
public enum GridGeometry { Square, HexFlat, HexPointy }

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
}

public sealed record GridDefinition
{
    public GridDefinition(GridId id, GridGeometry geometry, double cellSize)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        Id = id; Geometry = geometry; CellSize = cellSize;
    }

    public GridId Id { get; init; }
    public GridGeometry Geometry { get; init; }
    public double CellSize { get; init; }
    public int Subdivisions { get; init; } = 1;
    public double LineWidth { get; init; } = 1;
    public string Color { get; init; } = "#668FA7C1";
    public double Opacity { get; init; } = 0.7;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double RotationDegrees { get; init; }
    public bool SnapEnabled { get; init; } = true;
    public bool IncludeInExport { get; init; }
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
