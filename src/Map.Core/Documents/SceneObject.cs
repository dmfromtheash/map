namespace Map.Core.Documents;

/// <summary>Minimal object kinds for the foundation slice. Extended in later slices.</summary>
public enum SceneObjectKind
{
    Rectangle = 0,
    Ellipse = 1,
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

    public ObjectId Id { get; }
    public SceneObjectKind Kind { get; }
    public string Name { get; }

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
