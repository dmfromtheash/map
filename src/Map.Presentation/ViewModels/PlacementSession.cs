using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public enum PlacementTargetKind
{
    Rectangle,
    Ellipse,
    Stamp,
}

public enum PlacementAutoSelectPolicy
{
    Auto,
    Always,
    Off,
}

public sealed record EditorLocalPreferences(
    PlacementAutoSelectPolicy AutoSelect = PlacementAutoSelectPolicy.Auto,
    bool PrimaryAutosaveEnabled = true);

/// <summary>
/// Editor-only description of an armed placement. It never enters the document or history. Dense
/// scatter keeps only a bounded deterministic sample; the complete plan is materialised once at
/// commit from the stored seed and request.
/// </summary>
public sealed record PlacementSession
{
    public required PlacementTargetKind Target { get; init; }
    public required StampPlacementMode Mode { get; init; }
    public required PlacementAutoSelectPolicy AutoSelect { get; init; }
    public required LayerId? LayerId { get; init; }
    public required double WorldX { get; init; }
    public required double WorldY { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double RotationDegrees { get; init; }
    public required string FillColor { get; init; }
    public required string StrokeColor { get; init; }
    public required double StrokeWidth { get; init; }
    public required double PreviewOpacity { get; init; }
    public required bool PointerVisible { get; init; }
    public required string? InvalidReason { get; init; }
    public required int Seed { get; init; }
    public required int PlannedCount { get; init; }
    public required IReadOnlyList<SceneObject> PreviewObjects { get; init; }
    public string? AssetSetId { get; init; }
    public string? VariantId { get; init; }
    public string? VariantSha256 { get; init; }

    public bool CanCommit => LayerId is not null && InvalidReason is null && PointerVisible;
}
