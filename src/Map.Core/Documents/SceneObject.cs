namespace Map.Core.Documents;

/// <summary>Object kinds of the current model.</summary>
public enum SceneObjectKind
{
    Rectangle = 0,
    Ellipse = 1,
    Stamp = 2,
    Line = 3,
    Shape = 4,
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

    /// <summary>Set only for kind == Stamp: how the referenced asset is drawn.</summary>
    public StampStyle? Stamp { get; init; }

    /// <summary>Editable line geometry and appearance when <see cref="Kind"/> is Line.</summary>
    public LineStyle? Line { get; init; }

    /// <summary>Nondestructive vector expression when <see cref="Kind"/> is Shape.</summary>
    public ShapeStyle? Shape { get; init; }

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
