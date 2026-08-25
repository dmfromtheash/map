using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>
/// One ordered layer. Object order in <see cref="Objects"/> is z-order: later is drawn on top.
/// Immutable, like every part of the document snapshot.
/// </summary>
public sealed record Layer
{
    public Layer(
        LayerId id,
        string name,
        bool visible,
        bool locked,
        double opacity,
        IReadOnlyList<SceneObject> objects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(objects);

        if (opacity is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be within [0, 1].");
        }

        Id = id;
        Name = name;
        Visible = visible;
        Locked = locked;
        Opacity = opacity;
        Objects = new ReadOnlyCollection<SceneObject>([.. objects]);
    }

    public LayerId Id { get; }
    public string Name { get; }
    public bool Visible { get; }
    public bool Locked { get; }
    public double Opacity { get; }

    /// <summary>Objects in draw order. The stored copy cannot be mutated by the caller's list.</summary>
    public IReadOnlyList<SceneObject> Objects { get; }
}
