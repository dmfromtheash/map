using System.Collections.ObjectModel;

namespace Map.Core.Documents;

public sealed record FilterSettings
{
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Saturation { get; init; }
    public double HueDegrees { get; init; }
    public double Temperature { get; init; }
    public string? TextureAssetSha256 { get; init; }
    public double TextureOpacity { get; init; } = 0.25;
    public double TextureScale { get; init; } = 1;

    /// <summary>Recolours the lower scene by brightness: shadows, midtones and highlights each get a colour.</summary>
    public FilterGradientMap GradientMap { get; init; } = new();

    /// <summary>True per-pixel saturation, brightness, contrast and sharpness of the lower scene.</summary>
    public FilterPostProcess PostProcess { get; init; } = new();

    /// <summary>0..1 - how much of the gradient map and post-processing reaches the map ("Сила эффекта").</summary>
    public double ColorStrength { get; init; } = 1;

    /// <summary>The one-click preset last applied, for the panel caption only; null after manual edits.</summary>
    public string? ColorPresetId { get; init; }
}

/// <summary>Three colours by brightness of the scene below, mixed with the original by <see cref="Strength"/> (0..1).</summary>
public sealed record FilterGradientMap
{
    public string ShadowColor { get; init; } = "#FF000000";
    public string MidtoneColor { get; init; } = "#FF808080";
    public string HighlightColor { get; init; } = "#FFFFFFFF";
    public double Strength { get; init; }
}

/// <summary>Saturation, brightness and contrast in -1..1 (0 = unchanged); sharpness 0..1.</summary>
public sealed record FilterPostProcess
{
    public double Saturation { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Sharpness { get; init; }
}

/// <summary>Asset identity/provenance retained by a composition without duplicating payload bytes.</summary>
public sealed record CompositionAssetProvenance(
    string Sha256, string DisplayName, string Provenance, string License);

/// <summary>
/// Nested editable structure. Child coordinates are relative to the source bounds in source units;
/// instance width/height and reference-grid calibration provide deterministic placement scale.
/// </summary>
public sealed record EditableComposition
{
    public EditableComposition(
        CompositionId id, DocumentId sourceDocumentId, string name,
        double sourceWidth, double sourceHeight, IReadOnlyList<SceneObject> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("A composition contains at least one object.", nameof(items));
        if (items.Select(item => item.Id).Distinct().Count() != items.Count)
            throw new ArgumentException("Internal composition object ids are unique.", nameof(items));
        Id = id; SourceDocumentId = sourceDocumentId; Name = name;
        SourceWidth = sourceWidth; SourceHeight = sourceHeight;
        Items = new ReadOnlyCollection<SceneObject>([.. items]);
    }

    public CompositionId Id { get; init; }
    public DocumentId SourceDocumentId { get; init; }
    public string Name { get; init; }
    public double SourceWidth { get; init; }
    public double SourceHeight { get; init; }
    public double AnchorX { get; init; } = 0.5;
    public double AnchorY { get; init; } = 0.5;
    public double ReferenceGridScale { get; init; } = 1;
    public IReadOnlyList<SceneObject> Items { get; init; }
    public IReadOnlyList<CompositionAssetProvenance> Assets { get; init; } = [];
    public bool IncludeBackground { get; init; }
    public string? BackgroundColor { get; init; }
    public bool IncludeGrids { get; init; }
    public IReadOnlyList<GridDefinition> Grids { get; init; } = [];
    public bool IncludeFilters { get; init; }
    public IReadOnlyList<FilterSettings> Filters { get; init; } = [];
}
