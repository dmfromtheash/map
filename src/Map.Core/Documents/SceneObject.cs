namespace Map.Core.Documents;

/// <summary>Object kinds of the current model.</summary>
public enum SceneObjectKind
{
    Rectangle = 0,
    Ellipse = 1,
    Stamp = 2,
    Line = 3,
    Shape = 4,
    Text = 5,
    Composition = 6,
    Placeholder = 7,
}

/// <summary>
/// Appearance of a placed asset (kind == Stamp). The asset itself lives in the catalog or the
/// container, addressed by content hash - the object never embeds pixels.
/// </summary>
public sealed record StampStyle
{
    public StampStyle(string assetSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetSha256);
        AssetSha256 = assetSha256.ToLowerInvariant();
    }

    public string AssetSha256 { get; init; }

    /// <summary>
    /// World size the asset had at 100 % scale, captured at placement. Null on files written
    /// before Stage 10D; the inspector then adopts the current size as the 100 % baseline on the
    /// first scale edit, which is stable across reopen because nothing else moves it.
    /// </summary>
    public double? ReferenceWidth { get; init; }

    public double? ReferenceHeight { get; init; }

    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    /// <summary>Blend mode name from a fixed supported set; "Normal" is source-over.</summary>
    public string Blend { get; init; } = "Normal";

    /// <summary>Gaussian blur sigma in canvas units; 0 = none.</summary>
    public double Blur { get; init; }

    /// <summary>Drop shadow; disabled when opacity is 0.</summary>
    public double ShadowOffsetX { get; init; } = 4;
    public double ShadowOffsetY { get; init; } = 4;
    public double ShadowBlur { get; init; } = 6;
    public double ShadowOpacity { get; init; }

    /// <summary>Colour adjustments: hue in degrees, the rest as -1..1 offsets.</summary>
    public double Hue { get; init; }
    public double Saturation { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
}

/// <summary>
/// One scene object. Immutable: an edit produces a new instance via a `with` expression, so a
/// document snapshot handed to persistence can never change underneath the save.
/// </summary>
public sealed record SceneObject
{
    public SceneObject(
        ObjectId id,
        SceneObjectKind kind,
        string name,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        string fillColor,
        string strokeColor,
        double strokeWidth,
        bool visible,
        bool locked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(strokeWidth);
        ArgumentException.ThrowIfNullOrWhiteSpace(fillColor);
        ArgumentException.ThrowIfNullOrWhiteSpace(strokeColor);

        Id = id;
        Kind = kind;
        Name = name;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        FillColor = fillColor;
        StrokeColor = strokeColor;
        StrokeWidth = strokeWidth;
        Visible = visible;
        Locked = locked;
    }

    public ObjectId Id { get; init; }
    public SceneObjectKind Kind { get; init; }
    public string Name { get; init; }

    /// <summary>Coarse draw order band within the layer (-10..10); ties break by list order.</summary>
    public int Sublayer { get; init; }

    /// <summary>Objects sharing a group id select and move together. Null = ungrouped.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// User-visible name of the group. Carried by every member: same GroupId - same GroupName
    /// (the document constructor enforces it), and a name never exists without a group. Null on
    /// every object of a pre-10C file, so old documents load unchanged.
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>Set only for kind == Stamp: how the referenced asset is drawn.</summary>
    public StampStyle? Stamp { get; init; }

    /// <summary>Editable line geometry and appearance when <see cref="Kind"/> is Line.</summary>
    public LineStyle? Line { get; init; }

    /// <summary>Nondestructive vector expression when <see cref="Kind"/> is Shape.</summary>
    public ShapeStyle? Shape { get; init; }

    /// <summary>Editable text request and appearance; resolved fonts are machine-local derived state.</summary>
    public TextStyle? Text { get; init; }

    /// <summary>Editable nested object structure when <see cref="Kind"/> is Composition.</summary>
    public EditableComposition? Composition { get; init; }

    /// <summary>User-facing reason retained for a missing/incompatible pasted item.</summary>
    public string? PlaceholderReason { get; init; }

    /// <summary>Whole-object opacity 0..1 (stamps expose it in the editor).</summary>
    public double ObjectOpacity { get; init; } = 1.0;

    /// <summary>World position of the top-left corner, in canvas units.</summary>
    public double X { get; }

    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    /// <summary>Rotation around the object's centre, clockwise, degrees.</summary>
    public double RotationDegrees { get; }

    /// <summary>Fill colour as "#AARRGGBB". Stored verbatim; compared exactly.</summary>
    public string FillColor { get; }

    public string StrokeColor { get; }
    public double StrokeWidth { get; }
    public bool Visible { get; }
    public bool Locked { get; }
}
