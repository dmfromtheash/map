namespace Map.Core.Documents;

/// <summary>
/// The lookups that the first click, the first placement and the selection outline need:
/// identity slots, the revision index, the spatial grid of each object layer, the bound-label
/// sets of the follow passes and name occupancy. Each is built lazily on first use and then derived edit by edit from the
/// previous snapshot. On a map of a million objects the first use visits every object, so a
/// host prepares them right after open, away from the input thread; later snapshots inherit
/// them, so a click or a placement costs only what it changes. All of them are thread-safe
/// derived data: preparing changes neither the document nor its serialization.
/// </summary>
public static class DocumentDerivedIndexes
{
    /// <summary>True when every lookup of <paramref name="document"/> is already built.</summary>
    public static bool IsPrepared(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!DocumentRevisionIndex.IsPrepared(document) || !ObjectNameIndex.IsPrepared(document)) return false;
        foreach (var layer in document.Layers)
            if (layer is ObjectLayer objects && objects.Objects.Count > 0
                && (!objects.ObjectStorage.HasIdentityTable || !ObjectSpatialIndex.IsPrepared(objects)
                    || !Editing.BoundObjectIndex.IsPrepared(objects)))
                return false;
        return true;
    }

    /// <summary>Builds what is missing; cancellation stops between lookups, never inside one.</summary>
    public static void Prepare(MapDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        // The revision index also builds the identity slots of every object layer.
        _ = DocumentRevisionIndex.For(document);
        // Between lookups a background caller steps aside while a gesture runs (no-op on other threads).
        foreach (var layer in document.Layers)
        {
            if (layer is not ObjectLayer objects || objects.Objects.Count == 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            Resources.BackgroundWork.YieldToInteraction(cancellationToken);
            _ = objects.ObjectStorage.IndexOfId(objects.Objects[0].Id);
            Resources.BackgroundWork.YieldToInteraction(cancellationToken);
            _ = ObjectSpatialIndex.For(objects);
            Resources.BackgroundWork.YieldToInteraction(cancellationToken);
            // Labels on paths and regions: the follow passes of the first edit.
            _ = Editing.BoundObjectIndex.For(objects);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Resources.BackgroundWork.YieldToInteraction(cancellationToken);
        _ = ObjectNameIndex.For(document);
    }

    /// <summary>Total top-level objects of all object layers, from the stored counts.</summary>
    public static int ObjectCount(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var count = 0;
        foreach (var layer in document.Layers)
            if (layer is ObjectLayer objects) count += objects.Objects.Count;
        return count;
    }
}
