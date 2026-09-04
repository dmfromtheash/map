using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// A whole copied selection, ready to be pasted any number of times. Positions are stored relative
/// to the top-left of the selection's combined bounds, so the mutual layout of the group survives
/// a paste anywhere on the canvas, and the content those objects point at travels with them.
/// </summary>
public sealed record ObjectClipboardPayload(
    IReadOnlyList<SceneObject> Objects,
    IReadOnlyList<MapAssetReference> Assets,
    double Width,
    double Height)
{
    public int Count => Objects.Count;

    /// <summary>Where the captured group sat when it was copied, so a paste can cascade off it.</summary>
    public double SourceLeft { get; init; }

    public double SourceTop { get; init; }
}

/// <summary>
/// Copying and pasting whole selections as real objects.
/// This is deliberately not <see cref="CompositionTransfer"/>: a composition collapses the
/// selection into one nested object, while a paste has to give the user back the same N editable
/// objects they copied, with their own sublayers, groups and z-order.
/// </summary>
public static class ObjectTransfer
{
    /// <summary>
    /// Captures the given objects in deterministic draw order: layers bottom-up, and inside each
    /// layer the renderer's own order (sublayer band first, list position inside a band). Ids are
    /// carried over untouched; <see cref="Instantiate"/> is what mints new ones.
    /// </summary>
    public static ObjectClipboardPayload Copy(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.ToHashSet();
        var selected = new List<SceneObject>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            selected.AddRange(SceneGeometry.InDrawOrder(layer).Where(item => wanted.Contains(item.Id)));
        }

        if (selected.Count == 0)
        {
            throw new ArgumentException("A copy needs at least one selected object.", nameof(ids));
        }

        var bounds = selected.Select(SceneGeometry.BoundsOf).Aggregate((a, b) => new WorldBounds(
            Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));

        var relative = selected
            .Select(item => SceneObjectClone.At(item, item.X - bounds.Left, item.Y - bounds.Top))
            .ToList();

        var hashes = CompositionTransfer.UsedAssetHashes(relative);
        var assets = document.Assets.Where(asset => hashes.Contains(asset.Sha256)).ToList();

        return new ObjectClipboardPayload(
            relative, assets,
            Math.Max(0, bounds.Right - bounds.Left),
            Math.Max(0, bounds.Bottom - bounds.Top))
        {
            SourceLeft = bounds.Left,
            SourceTop = bounds.Top,
        };
    }

    /// <summary>
    /// Rebuilds the captured objects at a new top-left corner. Every object and every vector node
    /// inside it gets a fresh id, and each distinct source group becomes one distinct new group, so
    /// the pasted copy is grouped exactly like the original without joining it. Everything else -
    /// relative offsets, size, rotation, flips, opacity, sublayer, style and asset identity -
    /// is preserved, and the returned order is the captured draw order.
    /// </summary>
    public static IReadOnlyList<SceneObject> Instantiate(
        ObjectClipboardPayload payload, double left, double top)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var groups = new Dictionary<Guid, Guid>();
        var result = new List<SceneObject>(payload.Objects.Count);
        foreach (var source in payload.Objects)
        {
            var moved = VectorGeometry.WithNewIds(
                SceneObjectClone.At(source, source.X + left, source.Y + top));
            if (source.GroupId is { } group)
            {
                if (!groups.TryGetValue(group, out var replacement))
                {
                    replacement = Guid.NewGuid();
                    groups[group] = replacement;
                }

                moved = moved with { GroupId = replacement };
            }

            result.Add(moved);
        }

        return result;
    }
}
