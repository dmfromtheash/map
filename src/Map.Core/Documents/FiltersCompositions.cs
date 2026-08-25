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
