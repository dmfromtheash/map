using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One reversible edit. Apply and Revert are exact inverses over immutable snapshots: both take a
/// document and return a new one, never mutating anything (AGENTS.md invariant).
/// </summary>
public interface IEditCommand
{
    string Name { get; }

    MapDocument Apply(MapDocument document);

    MapDocument Revert(MapDocument document);
}

/// <summary>
/// Several edits the user performed as one gesture, kept as ONE history entry. Apply runs the
/// steps in order; Revert unwinds them in reverse, so a placement that had to create its own
/// layer never leaves that layer behind after a single undo.
/// </summary>
public sealed class CompositeCommand : IEditCommand
{
    private readonly IReadOnlyList<IEditCommand> _steps;

    public CompositeCommand(string name, IReadOnlyList<IEditCommand> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("A composite command needs at least one step.", nameof(steps));
        }

        Name = string.IsNullOrWhiteSpace(name) ? "Composite edit" : name;
        _steps = [.. steps];
    }

    public string Name { get; }

    public int StepCount => _steps.Count;

    internal bool ContainsPlacementPath => _steps.Any(step => step is PlacementPathCommand or PlacementAreaCommand
        || step is CompositeCommand { ContainsPlacementPath: true });

    public MapDocument Apply(MapDocument document)
    {
        var current = document;
        foreach (var step in _steps)
        {
            current = step.Apply(current);
        }

        return current;
    }

    public MapDocument Revert(MapDocument document)
    {
        var current = document;
        for (var index = _steps.Count - 1; index >= 0; index--)
        {
            current = _steps[index].Revert(current);
        }

        return current;
    }
}

// ---------------------------------------------------------------- object commands

/// <summary>Adds one object to the end of one object layer. Revert removes it by id.</summary>
public sealed class AddObjectCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly SceneObject _added;

    public AddObjectCommand(LayerId layerId, SceneObject added)
    {
        ArgumentNullException.ThrowIfNull(added);
        _layerId = layerId;
        _added = added;
    }

    public string Name => $"Add {_added.Kind}";

    public ObjectId AddedObjectId => _added.Id;

    public MapDocument Apply(MapDocument document) =>
        DocumentEditor.AppendObjects(document, _layerId, [_added]);

    public MapDocument Revert(MapDocument document) =>
        DocumentEditor.RemoveAppendedObjects(document, _layerId, [_added]);
}

/// <summary>Adds many objects as ONE entry - the scatter gesture.</summary>
public sealed class AddObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<SceneObject> _added;

    public AddObjectsCommand(LayerId layerId, IReadOnlyList<SceneObject> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        if (added.Count == 0)
        {
            throw new ArgumentException("A scatter adds at least one object.", nameof(added));
        }

        _layerId = layerId;
        _added = [.. added];
    }

    public string Name => $"Scatter {_added.Count} objects";

    public int Count => _added.Count;

    public MapDocument Apply(MapDocument document) =>
        DocumentEditor.AppendObjects(document, _layerId, _added);

    public MapDocument Revert(MapDocument document) =>
        DocumentEditor.RemoveAppendedObjects(document, _layerId, _added);
}

/// <summary>
/// A stamp placement/scatter plus its project asset reference as one reversible transaction.
/// The reference is removed on undo only when this command introduced it and no remaining stamp
/// uses it; content identity therefore stays stable across redo and repeated placement.
/// </summary>
public sealed class PlaceAssetObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<MapAssetReference> _assets;
    private readonly IReadOnlyList<SceneObject> _objects;
    private readonly HashSet<string> _introduced = new(StringComparer.OrdinalIgnoreCase);

    public PlaceAssetObjectsCommand(
        LayerId layerId, MapAssetReference asset, IReadOnlyList<SceneObject> objects)
        : this(layerId, [asset ?? throw new ArgumentNullException(nameof(asset))], objects)
    {
    }

    /// <summary>
    /// One gesture may place stamps from several variants of a set. The whole plan is resolved by
    /// the caller before this command exists, so Apply never chooses anything.
    /// </summary>
    public PlaceAssetObjectsCommand(
        LayerId layerId, IReadOnlyList<MapAssetReference> assets, IReadOnlyList<SceneObject> objects)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(objects);
        if (assets.Count == 0)
        {
            throw new ArgumentException("A placement carries at least one asset reference.", nameof(assets));
        }

        var hashes = assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (objects.Count == 0
            || objects.Any(item => item.Kind != SceneObjectKind.Stamp
                || item.Stamp is null
                || !hashes.Contains(item.Stamp.AssetSha256)
                || item.Stamp.MaskAssetSha256 is { } mask && !hashes.Contains(mask)))
        {
            throw new ArgumentException("Every placed object must reference one of the supplied stamp assets.");
        }

        _layerId = layerId;
        _assets = [.. assets.GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
        _objects = [.. objects];
    }

    public string Name => _objects.Count == 1
        ? "Place stamp"
        : $"Scatter {_objects.Count} stamps ({VariantCount} variants)";

    public int Count => _objects.Count;

    /// <summary>How many distinct contents this one gesture introduced into the document.</summary>
    public int VariantCount => _objects.Select(item => item.Stamp!.AssetSha256)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public MapDocument Apply(MapDocument document)
    {
        _introduced.Clear();
        var known = document.Assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<MapAssetReference>();
        foreach (var asset in _assets)
        {
            if (known.Add(asset.Sha256))
            {
                _introduced.Add(asset.Sha256);
                added.Add(asset);
            }
        }

        var withAssets = added.Count == 0 ? document : document.WithAssets([.. document.Assets, .. added]);
        return DocumentEditor.AppendObjects(withAssets, _layerId, _objects);
    }

    public MapDocument Revert(MapDocument document)
    {
        var withoutObjects = DocumentEditor.RemoveAppendedObjects(document, _layerId, _objects);

        if (_introduced.Count == 0)
        {
            return withoutObjects;
        }

        // Only references this command actually introduced, and only those no surviving stamp
        // still points at, are withdrawn. Anything else would break another object's content.
        var stillUsed = CompositionTransfer.UsedAssetHashes(
            withoutObjects.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects));
        var withdraw = _introduced.Where(sha => !stillUsed.Contains(sha))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return withdraw.Count == 0
            ? withoutObjects
            : withoutObjects.WithAssets(
                [.. withoutObjects.Assets.Where(asset => !withdraw.Contains(asset.Sha256))]);
    }
}

/// <summary>
/// Pastes a whole copied selection back into one object layer as ONE history entry: every object
/// of any kind, plus the asset references those objects need. Unlike
/// <see cref="PlaceAssetObjectsCommand"/> the objects are not restricted to stamps, and unlike
/// <see cref="AddObjectsCommand"/> the content the pasted objects point at travels with them, so
/// a paste into a document that never saw those bytes still renders.
/// The caller resolves new ids, offsets and order before the command exists; Apply chooses nothing.
/// </summary>
public sealed class PasteObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<MapAssetReference> _assets;
    private readonly IReadOnlyList<SceneObject> _objects;
    private readonly HashSet<string> _introduced = new(StringComparer.OrdinalIgnoreCase);

    public PasteObjectsCommand(
        LayerId layerId, IReadOnlyList<SceneObject> objects, IReadOnlyList<MapAssetReference>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0)
        {
            throw new ArgumentException("A paste restores at least one object.", nameof(objects));
        }

        _layerId = layerId;
        _objects = [.. objects];
        _assets = assets is null
            ? []
            : [.. assets.GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())];
    }

    public string Name => _objects.Count == 1 ? "Paste object" : $"Paste {_objects.Count} objects";

    public int Count => _objects.Count;

    /// <summary>Ids of the pasted objects, so the caller can select exactly the new group.</summary>
    public IReadOnlyList<ObjectId> PastedObjectIds => [.. _objects.Select(item => item.Id)];

    public MapDocument Apply(MapDocument document)
    {
        _introduced.Clear();
        var known = document.Assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<MapAssetReference>();
        foreach (var asset in _assets)
        {
            if (known.Add(asset.Sha256))
            {
                _introduced.Add(asset.Sha256);
                added.Add(asset);
            }
        }

        var withAssets = added.Count == 0 ? document : document.WithAssets([.. document.Assets, .. added]);
        return DocumentEditor.AppendObjects(withAssets, _layerId, _objects);
    }

    public MapDocument Revert(MapDocument document)
    {
        var ids = _objects.Select(item => item.Id).ToHashSet();
        var withoutObjects = document.WithLayer(_layerId, layer =>
        {
            var objects = (ObjectLayer)layer;
            return objects with { Objects = [.. objects.Objects.Where(item => !ids.Contains(item.Id))] };
        });

        if (_introduced.Count == 0)
        {
            return withoutObjects;
        }

        // Same rule as a placement: only references this paste introduced, and only those no
        // surviving object still points at, are withdrawn.
        var stillUsed = CompositionTransfer.UsedAssetHashes(
            withoutObjects.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects));
        var withdraw = _introduced.Where(sha => !stillUsed.Contains(sha))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return withdraw.Count == 0
            ? withoutObjects
            : withoutObjects.WithAssets(
                [.. withoutObjects.Assets.Where(asset => !withdraw.Contains(asset.Sha256))]);
    }
}

/// <summary>Explicit missing-asset replacement: changes the hash and adds metadata in one undo step.</summary>
public sealed class ReplaceStampAssetCommand : IEditCommand
{
    private readonly SceneObject _before;
    private readonly SceneObject _after;
    private readonly IReadOnlyList<MapAssetReference> _assets;
    private readonly HashSet<string> _introduced = new(StringComparer.OrdinalIgnoreCase);

    public ReplaceStampAssetCommand(
        SceneObject before,
        MapAssetReference replacement,
        double? replacementAspectRatio = null,
        string? replacementMaskSha256 = null,
        MapAssetReference? replacementMask = null)
    {
        if (before.Stamp is null)
        {
            throw new ArgumentException("The selected object is not a stamp.", nameof(before));
        }

        _before = before;
        if (replacementMaskSha256 is not null
            && (replacementMask is null
                || !string.Equals(replacementMask.Sha256, replacementMaskSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The replacement mask reference does not match its identity.", nameof(replacementMask));
        }
        _assets = replacementMask is null ? [replacement] : [replacement, replacementMask];
        var stamp = before.Stamp with
        {
            AssetSha256 = replacement.Sha256,
            MaskAssetSha256 = replacementMaskSha256,
            ClipMaskEnabled = before.Stamp.ClipMaskEnabled && replacementMaskSha256 is not null,
        };
        if (replacementAspectRatio is not { } aspect || !double.IsFinite(aspect) || aspect <= 0)
        {
            _after = before with { Stamp = stamp, Name = replacement.DisplayName };
            return;
        }

        var referenceWidth = before.Stamp.ReferenceWidth is > 0
            ? before.Stamp.ReferenceWidth.Value
            : before.Width;
        var scale = before.Width / Math.Max(referenceWidth, 0.000001);
        var referenceHeight = referenceWidth * aspect;
        var height = referenceHeight * scale;
        _after = new SceneObject(
            before.Id, before.Kind, replacement.DisplayName,
            before.X, before.Y + ((before.Height - height) / 2),
            before.Width, height, before.RotationDegrees,
            before.FillColor, before.StrokeColor, before.StrokeWidth,
            before.Visible, before.Locked)
        {
            Sublayer = before.Sublayer,
            GroupId = before.GroupId,
            GroupName = before.GroupName,
            ObjectOpacity = before.ObjectOpacity,
            Stamp = stamp with
            {
                ReferenceWidth = referenceWidth,
                ReferenceHeight = referenceHeight,
            },
            Line = before.Line,
            Shape = before.Shape,
            FillGradient = before.FillGradient,
            Text = before.Text,
            Composition = before.Composition,
            PlaceholderReason = before.PlaceholderReason,
            OpaquePayload = before.OpaquePayload,
        };
    }

    public string Name => "Replace stamp asset";

    public MapDocument Apply(MapDocument document)
    {
        _introduced.Clear();
        var known = document.Assets.Select(asset => asset.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = _assets.Where(asset => known.Add(asset.Sha256)).ToList();
        foreach (var asset in added) _introduced.Add(asset.Sha256);
        var withAssets = added.Count == 0
            ? document
            : document.WithAssets([.. document.Assets, .. added]);
        return DocumentEditor.ReplaceObject(withAssets, _after);
    }

    public MapDocument Revert(MapDocument document)
    {
        var reverted = DocumentEditor.ReplaceObject(document, _before);
        if (_introduced.Count == 0) return reverted;
        var stillUsed = CompositionTransfer.UsedAssetHashes(
            reverted.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects));
        var withdraw = _introduced.Where(hash => !stillUsed.Contains(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return withdraw.Count == 0 ? reverted : reverted.WithAssets(
            [.. reverted.Assets.Where(asset => !withdraw.Contains(asset.Sha256))]);
    }
}

/// <summary>
/// Changes one placed stamp style and, when needed, introduces its resolved mask payload in the
/// same undoable command. Undo removes only an asset this command introduced and nobody still uses.
/// </summary>
public sealed class UpdateStampStyleWithAssetCommand : IEditCommand
{
    private readonly SceneObject _before;
    private readonly SceneObject _after;
    private readonly MapAssetReference _asset;
    private bool _introduced;

    public UpdateStampStyleWithAssetCommand(
        SceneObject before, StampStyle afterStyle, MapAssetReference asset)
    {
        if (before.Stamp is null)
            throw new ArgumentException("The selected object is not a stamp.", nameof(before));
        if (afterStyle.MaskAssetSha256 is not { } mask
            || !string.Equals(mask, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The supplied asset is not the style's resolved mask.", nameof(asset));
        _before = before;
        _after = before with { Stamp = afterStyle };
        _asset = asset;
    }

    public string Name => "Edit stamp clip mask";

    public MapDocument Apply(MapDocument document)
    {
        _introduced = document.Assets.All(item =>
            !string.Equals(item.Sha256, _asset.Sha256, StringComparison.OrdinalIgnoreCase));
        var withAsset = _introduced
            ? document.WithAssets([.. document.Assets, _asset])
            : document;
        return DocumentEditor.ReplaceObject(withAsset, _after);
    }

    public MapDocument Revert(MapDocument document)
    {
        var reverted = DocumentEditor.ReplaceObject(document, _before);
        if (!_introduced) return reverted;
        var used = CompositionTransfer.UsedAssetHashes(
            reverted.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects));
        return used.Contains(_asset.Sha256)
            ? reverted
            : reverted.WithAssets([.. reverted.Assets.Where(item =>
                !string.Equals(item.Sha256, _asset.Sha256, StringComparison.OrdinalIgnoreCase))]);
    }
}

/// <summary>
/// Replaces the captured objects with their edited counterparts, wherever they live. Covers one
/// completed transform/style gesture over one object or a whole group - one history entry.
/// </summary>
public sealed class ReplaceObjectsCommand : IEditCommand
{
    private readonly IReadOnlyList<SceneObject> _before;
    private readonly IReadOnlyList<SceneObject> _after;

    public ReplaceObjectsCommand(IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count == 0 || before.Count != after.Count
            || before.Zip(after).Any(p => p.First.Id != p.Second.Id))
        {
            throw new ArgumentException("Before/after must describe the same objects.", nameof(after));
        }

        _before = [.. before];
        _after = [.. after];
        Name = name;
    }

    public string Name { get; }

    public MapDocument Apply(MapDocument document) => Replace(document, _after);

    public MapDocument Revert(MapDocument document) => Replace(document, _before);

    private static MapDocument Replace(MapDocument document, IReadOnlyList<SceneObject> replacements) =>
        DocumentEditor.ReplaceObjects(document, replacements);
}

/// <summary>
/// Changes only the order of one object layer's list, which is what decides draw order inside a
/// sublayer band (see SceneGeometry.InDrawOrder). The same objects come out; nothing about them
/// changes except where they sit relative to their neighbours.
/// </summary>
public sealed class ReorderObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<SceneObject> _before;
    private readonly IReadOnlyList<SceneObject> _after;

    public ReorderObjectsCommand(
        LayerId layerId, IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count != after.Count
            || before.Select(item => item.Id).ToHashSet().SetEquals(after.Select(item => item.Id)) is false)
        {
            throw new ArgumentException("A reorder keeps exactly the same objects.", nameof(after));
        }

        _layerId = layerId;
        _before = [.. before];
        _after = [.. after];
        Name = string.IsNullOrWhiteSpace(name) ? "Reorder objects" : name;
    }

    public string Name { get; }

    public MapDocument Apply(MapDocument document) => Order(document, _after);

    public MapDocument Revert(MapDocument document) => Order(document, _before);

    private MapDocument Order(MapDocument document, IReadOnlyList<SceneObject> order) =>
        document.WithLayer(_layerId, layer =>
        {
            var objects = layer as ObjectLayer
                ?? throw new InvalidOperationException($"Layer {_layerId} does not hold objects.");

            // The stored snapshots only carry identity here; the live objects may have been edited
            // since, so the current instances are reordered rather than written back wholesale.
            var current = objects.Objects.ToDictionary(item => item.Id);
            var reordered = new List<SceneObject>(objects.Objects.Count);
            foreach (var item in order)
            {
                if (current.TryGetValue(item.Id, out var live))
                {
                    reordered.Add(live);
                }
            }

            return reordered.Count == objects.Objects.Count
                ? objects with { Objects = reordered }
                : objects;
        });
}

/// <summary>Legacy single-object transform wrapper kept for the editor's drag/inspector path.</summary>
public sealed class TransformObjectCommand : IEditCommand
{
    private readonly ReplaceObjectsCommand _inner;

    public TransformObjectCommand(SceneObject before, SceneObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        _inner = new ReplaceObjectsCommand([before], [after], "Transform object");
        ObjectId = before.Id;
    }

    public string Name => _inner.Name;

    public ObjectId ObjectId { get; }

    public MapDocument Apply(MapDocument document) => _inner.Apply(document);

    public MapDocument Revert(MapDocument document) => _inner.Revert(document);
}

/// <summary>Removes objects (delete / move-to-layer source side) as one entry.</summary>
public sealed class RemoveObjectsCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyList<(int Index, SceneObject Object)> _removed;

    public RemoveObjectsCommand(MapDocument document, LayerId layerId, IReadOnlyCollection<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _layerId = layerId;

        var layer = document.FindLayer(layerId) as ObjectLayer
            ?? throw new InvalidOperationException($"Layer {layerId} does not hold objects.");

        _removed = [.. layer.Objects
            .Select((o, i) => (Index: i, Object: o))
            .Where(p => ids.Contains(p.Object.Id))];

        if (_removed.Count != ids.Count)
        {
            throw new InvalidOperationException("Some objects to remove are not on the layer.");
        }
    }

    public string Name => $"Delete {_removed.Count} object(s)";

    public MapDocument Apply(MapDocument document)
    {
        var ids = _removed.Select(r => r.Object.Id).ToHashSet();
        return document.WithLayer(_layerId, layer =>
        {
            var objectLayer = (ObjectLayer)layer;
            return objectLayer with { Objects = [.. objectLayer.Objects.Where(o => !ids.Contains(o.Id))] };
        });
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(_layerId, layer =>
        {
            var objects = ((ObjectLayer)layer).Objects.ToList();
            foreach (var (index, sceneObject) in _removed.OrderBy(r => r.Index))
            {
                objects.Insert(Math.Min(index, objects.Count), sceneObject);
            }

            return ((ObjectLayer)layer) with { Objects = objects };
        });
}

/// <summary>Moves objects from one object layer to the end of another, preserving relative order.</summary>
public sealed class MoveObjectsToLayerCommand : IEditCommand
{
    private readonly RemoveObjectsCommand _remove;
    private readonly LayerId _targetLayerId;
    private readonly IReadOnlyList<SceneObject> _moved;

    public MoveObjectsToLayerCommand(
        MapDocument document, LayerId sourceLayerId, LayerId targetLayerId, IReadOnlyCollection<ObjectId> ids)
    {
        if (sourceLayerId == targetLayerId)
        {
            throw new ArgumentException("Source and target layers are the same.", nameof(targetLayerId));
        }

        if (document.FindLayer(targetLayerId) is not ObjectLayer)
        {
            throw new InvalidOperationException($"Layer {targetLayerId} does not hold objects.");
        }

        _remove = new RemoveObjectsCommand(document, sourceLayerId, ids);
        _targetLayerId = targetLayerId;

        var source = (ObjectLayer)document.FindLayer(sourceLayerId)!;
        _moved = [.. source.Objects.Where(o => ids.Contains(o.Id))];
    }

    public string Name => $"Move {_moved.Count} object(s) to another layer";

    public MapDocument Apply(MapDocument document)
    {
        var removed = _remove.Apply(document);
        return DocumentEditor.AppendObjects(removed, _targetLayerId, _moved);
    }

    public MapDocument Revert(MapDocument document)
    {
        var withoutTarget = DocumentEditor.RemoveAppendedObjects(
            document, _targetLayerId, _moved);

        return _remove.Revert(withoutTarget);
    }
}

/// <summary>
/// Assigns (group), renames or clears (ungroup) the shared group identity of the captured
/// objects. The group id and the sanitized name are fixed when the command is created, so redo
/// restores exactly the same group instead of minting a new one.
/// </summary>
public sealed class SetGroupCommand : IEditCommand
{
    /// <summary>Longest group name the editor accepts, in characters after trimming.</summary>
    public const int MaxNameLength = 80;

    private readonly ReplaceObjectsCommand _inner;

    private SetGroupCommand(ReplaceObjectsCommand inner, Guid? groupId)
    {
        _inner = inner;
        GroupId = groupId;
    }

    /// <summary>The identity this command creates, for selection follow-up. Null for ungroup.</summary>
    public Guid? GroupId { get; }

    public static SetGroupCommand Group(MapDocument document, IReadOnlyCollection<ObjectId> ids, string name)
    {
        var groupName = SanitizeName(name);
        var before = Resolve(document, ids);
        if (before.Count < 2)
        {
            throw new ArgumentException("Для группы нужно минимум два объекта.", nameof(ids));
        }

        if (before.FirstOrDefault(o => o.GroupId is not null) is { } member)
        {
            throw new InvalidOperationException(
                $"Объект «{member.Name}» уже входит в группу. Сначала разгруппируйте его.");
        }

        var groupId = Guid.NewGuid();
        return new SetGroupCommand(new ReplaceObjectsCommand(
            before,
            [.. before.Select(o => o with { GroupId = groupId, GroupName = groupName })],
            $"Group {before.Count} objects"), groupId);
    }

    public static SetGroupCommand Ungroup(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        var before = Resolve(document, ids);
        return new SetGroupCommand(new ReplaceObjectsCommand(
            before,
            [.. before.Select(o => o with { GroupId = null, GroupName = null })],
            "Ungroup objects"), groupId: null);
    }

    public static SetGroupCommand Rename(MapDocument document, Guid groupId, string name)
    {
        var groupName = SanitizeName(name);
        var before = SceneGeometry.GroupMembers(document, groupId);
        if (before.Count == 0)
        {
            throw new InvalidOperationException($"Group {groupId} is not part of this document.");
        }

        return new SetGroupCommand(new ReplaceObjectsCommand(
            before,
            [.. before.Select(o => o with { GroupName = groupName })],
            "Rename group"), groupId);
    }

    /// <summary>Trims the edges, rejects empty and caps the length - the one naming rule.</summary>
    public static string SanitizeName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Имя группы не может быть пустым.", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Имя группы не длиннее {MaxNameLength} символов.", nameof(name));
        }

        return trimmed;
    }

    private static IReadOnlyList<SceneObject> Resolve(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        var objects = ids
            .Select(id => DocumentEditor.FindObject(document, id)
                ?? throw new InvalidOperationException($"Object {id} is not part of this document."))
            .ToList();

        if (objects.Count < 1)
        {
            throw new ArgumentException("Grouping needs at least one object.", nameof(ids));
        }

        return objects;
    }

    public string Name => _inner.Name;

    public MapDocument Apply(MapDocument document) => _inner.Apply(document);

    public MapDocument Revert(MapDocument document) => _inner.Revert(document);
}

/// <summary>
/// Deletes objects that may live on several object layers as ONE history entry. When the
/// deletion leaves a group with exactly one member, that remainder loses its group identity
/// inside the same transaction - a one-object "group" is not a thing the user can see or use.
/// </summary>
public sealed class DeleteObjectsCommand : IEditCommand
{
    private readonly List<RemoveObjectsCommand> _removals;
    private readonly ReplaceObjectsCommand? _dissolveSingletons;
    private readonly int _count;

    public DeleteObjectsCommand(MapDocument document, IReadOnlyCollection<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            throw new ArgumentException("Deletion needs at least one object.", nameof(ids));
        }

        var deleting = ids.ToHashSet();
        var byLayer = new List<RemoveObjectsCommand>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            var onLayer = layer.Objects.Where(o => deleting.Contains(o.Id)).Select(o => o.Id).ToList();
            if (onLayer.Count > 0)
            {
                byLayer.Add(new RemoveObjectsCommand(document, layer.Id, onLayer));
            }
        }

        var located = document.Layers.OfType<ObjectLayer>()
            .SelectMany(l => l.Objects).Count(o => deleting.Contains(o.Id));
        if (located != deleting.Count)
        {
            throw new InvalidOperationException("Some objects to delete are not part of this document.");
        }

        // Survivors of touched groups: exactly one left means its group dissolves with the delete.
        var touchedGroups = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Where(o => deleting.Contains(o.Id) && o.GroupId is not null)
            .Select(o => o.GroupId!.Value)
            .Distinct();
        var singletons = new List<SceneObject>();
        foreach (var groupId in touchedGroups)
        {
            var remaining = SceneGeometry.GroupMembers(document, groupId)
                .Where(o => !deleting.Contains(o.Id))
                .ToList();
            if (remaining.Count == 1)
            {
                singletons.Add(remaining[0]);
            }
        }

        _removals = byLayer;
        _dissolveSingletons = singletons.Count == 0
            ? null
            : new ReplaceObjectsCommand(
                singletons,
                [.. singletons.Select(o => o with { GroupId = null, GroupName = null })],
                "Dissolve singleton group");
        _count = deleting.Count;
    }

    public string Name => $"Delete {_count} object(s)";

    public MapDocument Apply(MapDocument document)
    {
        foreach (var removal in _removals)
        {
            document = removal.Apply(document);
        }

        return _dissolveSingletons is null ? document : _dissolveSingletons.Apply(document);
    }

    public MapDocument Revert(MapDocument document)
    {
        if (_dissolveSingletons is not null)
        {
            document = _dissolveSingletons.Revert(document);
        }

        for (var i = _removals.Count - 1; i >= 0; i--)
        {
            document = _removals[i].Revert(document);
        }

        return document;
    }
}

// ---------------------------------------------------------------- layer commands

/// <summary>Inserts a new layer above the given anchor (or on top when null).</summary>
public sealed class AddLayerCommand : IEditCommand
{
    private readonly MapLayer _layer;
    private readonly LayerId? _aboveLayerId;

    public AddLayerCommand(MapLayer layer, LayerId? aboveLayerId = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer is BaseColorLayer)
        {
            throw new ArgumentException("The base colour layer is unique and cannot be added.", nameof(layer));
        }

        _layer = layer;
        _aboveLayerId = aboveLayerId;
    }

    public string Name => $"Add layer \"{_layer.Name}\"";

    public LayerId LayerId => _layer.Id;

    public MapDocument Apply(MapDocument document)
    {
        var layers = document.Layers.ToList();
        var index = _aboveLayerId is { } anchor
            ? layers.FindIndex(l => l.Id == anchor) + 1
            : layers.Count;

        if (index <= 0)
        {
            index = layers.Count;
        }

        layers.Insert(index, _layer);
        return document.WithLayers(layers);
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _layer.Id)]);
}

/// <summary>Deletes a layer. The base colour layer refuses. Revert restores content and position.</summary>
public sealed class DeleteLayerCommand : IEditCommand
{
    private readonly MapLayer _deleted;
    private readonly int _index;

    public DeleteLayerCommand(MapDocument document, LayerId layerId)
    {
        var index = document.Layers.ToList().FindIndex(l => l.Id == layerId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Layer {layerId} is not part of this document.");
        }

        if (document.Layers[index] is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer cannot be deleted.");
        }

        _deleted = document.Layers[index];
        _index = index;
    }

    public string Name => $"Delete layer \"{_deleted.Name}\"";

    public MapDocument Apply(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _deleted.Id)]);

    public MapDocument Revert(MapDocument document)
    {
        var layers = document.Layers.ToList();
        layers.Insert(Math.Min(_index, layers.Count), _deleted);
        return document.WithLayers(layers);
    }
}

/// <summary>Moves a layer one step up or down. The base colour layer stays at the bottom.</summary>
public sealed class ReorderLayerCommand(LayerId layerId, bool up) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly int _delta = up ? 1 : -1;
    private readonly bool _up = up;
    private bool _applied;

    public string Name => "Reorder layer";

    public MapDocument Apply(MapDocument document)
    {
        if (!LayerOrderPolicy.CanMove(document, _layerId, _up))
        {
            _applied = false;
            return document;
        }

        _applied = true;
        return Shift(document, _delta);
    }

    public MapDocument Revert(MapDocument document) => _applied ? Shift(document, -_delta) : document;

    private MapDocument Shift(MapDocument document, int delta)
    {
        var layers = document.Layers.ToList();
        var index = layers.FindIndex(l => l.Id == _layerId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Layer {_layerId} is not part of this document.");
        }

        if (layers[index] is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer keeps the bottom position.");
        }

        var target = index + delta;

        (layers[index], layers[target]) = (layers[target], layers[index]);
        return document.WithLayers(layers);
    }
}

/// <summary>Rename / visibility / lock / opacity / colour: one small reversible property change.</summary>
public sealed class UpdateLayerCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Func<MapLayer, MapLayer> _forward;
    private readonly Func<MapLayer, MapLayer> _backward;

    private UpdateLayerCommand(
        string name, LayerId layerId, Func<MapLayer, MapLayer> forward, Func<MapLayer, MapLayer> backward)
    {
        Name = name;
        _layerId = layerId;
        _forward = forward;
        _backward = backward;
    }

    public string Name { get; }

    public static UpdateLayerCommand Rename(MapLayer layer, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var oldName = layer.Name;
        return new UpdateLayerCommand(
            "Rename layer", layer.Id,
            l => Mutate(l, name: newName),
            l => Mutate(l, name: oldName));
    }

    public static UpdateLayerCommand SetVisible(MapLayer layer, bool visible) => new(
        visible ? "Show layer" : "Hide layer", layer.Id,
        l => Mutate(l, visible: visible),
        l => Mutate(l, visible: layer.Visible));

    public static UpdateLayerCommand SetLocked(MapLayer layer, bool locked) => new(
        locked ? "Lock layer" : "Unlock layer", layer.Id,
        l => Mutate(l, locked: locked),
        l => Mutate(l, locked: layer.Locked));

    public static UpdateLayerCommand SetOpacity(MapLayer layer, double opacity) => new(
        "Layer opacity", layer.Id,
        l => Mutate(l, opacity: opacity),
        l => Mutate(l, opacity: layer.Opacity));

    public static UpdateLayerCommand SetBlend(MapLayer layer, string blend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blend);
        return new UpdateLayerCommand(
            "Layer blend", layer.Id,
            l => Mutate(l, blend: blend),
            l => Mutate(l, blend: layer.Blend));
    }

    public static UpdateLayerCommand SetBaseColor(BaseColorLayer layer, string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        var oldColor = layer.Color;
        return new UpdateLayerCommand(
            "Background colour", layer.Id,
            l => ((BaseColorLayer)l) with { Color = color },
            l => ((BaseColorLayer)l) with { Color = oldColor });
    }

    public static UpdateLayerCommand Replace(MapLayer before, MapLayer after, string name)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (before.Id != after.Id || before.GetType() != after.GetType())
        {
            throw new ArgumentException("A layer replacement must preserve identity and kind.", nameof(after));
        }

        return new UpdateLayerCommand(name, before.Id, _ => after, _ => before);
    }

    private static MapLayer Mutate(
        MapLayer layer,
        string? name = null,
        bool? visible = null,
        bool? locked = null,
        double? opacity = null,
        string? blend = null)
        => layer switch
        {
            BaseColorLayer b => b with
            {
                Name = name ?? b.Name,
                Visible = visible ?? b.Visible,
                Locked = locked ?? b.Locked,
                Opacity = opacity ?? b.Opacity,
                Blend = blend ?? b.Blend,
            },
            ObjectLayer o => o with
            {
                Name = name ?? o.Name,
                Visible = visible ?? o.Visible,
                Locked = locked ?? o.Locked,
                Opacity = opacity ?? o.Opacity,
                Blend = blend ?? o.Blend,
            },
            BrushLayer br => br with
            {
                Name = name ?? br.Name,
                Visible = visible ?? br.Visible,
                Locked = locked ?? br.Locked,
                Opacity = opacity ?? br.Opacity,
                Blend = blend ?? br.Blend,
            },
            LandLayer land => land with
            {
                Name = name ?? land.Name,
                Visible = visible ?? land.Visible,
                Locked = locked ?? land.Locked,
                Opacity = opacity ?? land.Opacity,
                Blend = blend ?? land.Blend,
            },
            GridLayer grid => grid with
            {
                Name = name ?? grid.Name,
                Visible = visible ?? grid.Visible,
                Locked = locked ?? grid.Locked,
                Opacity = opacity ?? grid.Opacity,
                Blend = blend ?? grid.Blend,
            },
            FilterLayer filter => filter with
            {
                Name = name ?? filter.Name,
                Visible = visible ?? filter.Visible,
                Locked = locked ?? filter.Locked,
                Opacity = opacity ?? filter.Opacity,
                Blend = blend ?? filter.Blend,
            },
            _ => throw new InvalidOperationException($"Unknown layer type {layer.GetType().Name}."),
        };

    public MapDocument Apply(MapDocument document) => document.WithLayer(_layerId, _forward);

    public MapDocument Revert(MapDocument document) => document.WithLayer(_layerId, _backward);
}

/// <summary>Duplicates a layer (fresh ids) right above the original.</summary>
public sealed class DuplicateLayerCommand(LayerId sourceId) : IEditCommand
{
    private readonly LayerId _sourceId = sourceId;
    private MapLayer? _copy;

    public string Name => "Duplicate layer";

    public LayerId? CopyLayerId => _copy?.Id;

    public MapDocument Apply(MapDocument document)
    {
        var source = document.FindLayer(_sourceId)
            ?? throw new InvalidOperationException($"Layer {_sourceId} is not part of this document.");

        if (source is BaseColorLayer)
        {
            throw new InvalidOperationException("The base colour layer is unique.");
        }

        // The copy's ids are minted once so redo restores the same identities.
        _copy ??= source.WithNewIds() with { Name = source.Name + " (копия)" };

        var layers = document.Layers.ToList();
        layers.Insert(layers.FindIndex(l => l.Id == _sourceId) + 1, _copy);
        return document.WithLayers(layers);
    }

    public MapDocument Revert(MapDocument document) =>
        document.WithLayers([.. document.Layers.Where(l => l.Id != _copy!.Id)]);
}

// ---------------------------------------------------------------- tile commands

/// <summary>
/// One completed raster gesture over one brush layer: before/after of ONLY the touched tiles
/// (docs/05 raster rule) - never a full-canvas snapshot.
/// </summary>
public sealed class BrushStrokeCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after,
    string name) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before = before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after = after;

    public string Name { get; } = name;

    public int TouchedTileCount => _before.Count;

    public long ApproximateMemoryBytes =>
        _before.Values.Sum(v => (long)(v?.Length ?? 0)) + _after.Values.Sum(v => (long)(v?.Length ?? 0));

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(_layerId, layer => layer is IRasterContentLayer raster
            ? raster.WithContentTiles(raster.ContentTiles.WithMany(tiles))
            : throw new InvalidOperationException($"Layer {_layerId} holds no raster tiles."));
}

/// <summary>One completed mask gesture: before/after of the touched mask tiles only.</summary>
public sealed class MaskStrokeCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after) : IEditCommand
{
    private readonly LayerId _layerId = layerId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before = before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after = after;

    public string Name => "Mask stroke";

    public int TouchedTileCount => _before.Count;

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(_layerId, layer =>
        {
            var mask = layer.Mask
                ?? throw new InvalidOperationException($"Layer {_layerId} has no mask.");
            return WithMask(layer, mask with { Tiles = mask.Tiles.WithMany(tiles) });
        });

    internal static MapLayer WithMask(MapLayer layer, LayerMask? mask) => layer.WithMask(mask);
}

/// <summary>Add, remove, enable/disable or copy a layer mask - one reversible step each.</summary>
public sealed class UpdateMaskCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly LayerMask? _before;
    private readonly LayerMask? _after;

    private UpdateMaskCommand(string name, LayerId layerId, LayerMask? before, LayerMask? after)
    {
        Name = name;
        _layerId = layerId;
        _before = before;
        _after = after;
    }

    public string Name { get; }

    public static UpdateMaskCommand AddMask(MapLayer layer) =>
        new("Add mask", layer.Id, layer.Mask, LayerMask.CreateEmpty());

    public static UpdateMaskCommand RemoveMask(MapLayer layer) =>
        new("Remove mask", layer.Id, layer.Mask, null);

    public static UpdateMaskCommand SetEnabled(MapLayer layer, bool enabled)
    {
        var mask = layer.Mask ?? throw new InvalidOperationException("The layer has no mask.");
        return new UpdateMaskCommand(
            enabled ? "Enable mask" : "Disable mask", layer.Id, mask, mask with { Enabled = enabled });
    }

    /// <summary>Copies the source layer's mask onto the target layer as the target's own mask.</summary>
    public static UpdateMaskCommand CopyMask(MapLayer source, MapLayer target)
    {
        var mask = source.Mask ?? throw new InvalidOperationException("The source layer has no mask.");
        var independentTiles = TileMap.Empty(1).WithMany(mask.Tiles.Keys.Select(key =>
            new KeyValuePair<TileKey, byte[]?>(key, (byte[])mask.Tiles.TryGet(key)!.Clone())));
        return new UpdateMaskCommand("Copy mask", target.Id, target.Mask,
            new LayerMask(independentTiles, mask.Enabled));
    }

    public MapDocument Apply(MapDocument document) =>
        document.WithLayer(_layerId, layer => MaskStrokeCommand.WithMask(layer, _after));

    public MapDocument Revert(MapDocument document) =>
        document.WithLayer(_layerId, layer => MaskStrokeCommand.WithMask(layer, _before));
}

/// <summary>Changes evaluated coast effects without touching the source coverage tiles.</summary>
public sealed class UpdateLandStyleCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly LandEdgeStyle _before;
    private readonly LandEdgeStyle _after;

    public UpdateLandStyleCommand(LandLayer layer, LandEdgeStyle after)
    {
        _layerId = layer.Id;
        _before = layer.EdgeStyle;
        _after = after;
    }

    public string Name => "Land edge style";

    public MapDocument Apply(MapDocument document) => Write(document, _after);
    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, LandEdgeStyle style) =>
        document.WithLayer(_layerId, layer => layer is LandLayer land
            ? land with { EdgeStyle = style }
            : throw new InvalidOperationException($"Layer {_layerId} is not land."));
}

// ---------------------------------------------------------------- helpers

/// <summary>Snapshot lookups shared by commands and the editor.</summary>
public static class DocumentEditor
{
    internal static MapDocument RemovePathObjects(MapDocument document, LayerId layerId, IReadOnlySet<ObjectId> ids)
    {
        var source = (ObjectLayer)document.FindLayer(layerId)!;
        var removed = ids.Select(source.ObjectStorage.IndexOfId).Where(i => i >= 0).Order().ToArray();
        var storage = source.ObjectStorage.RemoveIds(ids);
        var targetLayer = source with { Objects = storage };
        var layers = document.Layers.ToArray();
        layers[Array.FindIndex(layers, l => l.Id == layerId)] = targetLayer;
        var target = document.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterPathRemoval(document, target, targetLayer, removed.Length);
        ObjectNameIndex.Register(document, target, removed.Select(i => source.Objects[i]), []);
        ObjectLayerChanges.Register(source, targetLayer, removed, [], orderCompacted: true);
        return target;
    }

    public static SceneObject? FindObject(MapDocument document, ObjectId id) =>
        DocumentRevisionIndex.For(document).FindObject(id);

    public static ObjectLayer? FindLayerOf(MapDocument document, ObjectId id) =>
        DocumentRevisionIndex.For(document).FindLayerOf(id);

    /// <summary>
    /// Shared append boundary for every editor command that adds top-level objects. It validates
    /// only the new identities and carries the persistent storage/index delta into the revision.
    /// </summary>
    internal static MapDocument AppendObjects(
        MapDocument document, LayerId layerId, IReadOnlyList<SceneObject> additions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(additions);
        if (additions.Count == 0)
        {
            throw new ArgumentException("At least one appended object is required.", nameof(additions));
        }

        var revisionIndex = DocumentRevisionIndex.For(document);
        revisionIndex.ValidateAppends(additions);
        var layers = document.Layers.ToArray();
        var layerIndex = Array.FindIndex(layers, layer => layer.Id == layerId);
        if (layerIndex < 0 || layers[layerIndex] is not ObjectLayer objectLayer)
        {
            throw new InvalidOperationException($"Layer {layerId} does not hold objects.");
        }

        var firstObjectIndex = objectLayer.Objects.Count;
        layers[layerIndex] = objectLayer with
        {
            Objects = objectLayer.ObjectStorage.AppendRange(additions),
        };
        var target = document.WithRevisedLayers(layers);
        var locations = additions.Select((sceneObject, offset) => new DocumentObjectLocation(
            sceneObject, layerId, layerIndex, firstObjectIndex + offset)).ToArray();
        DocumentRevisionIndex.RegisterAppends(document, target, locations);
        ObjectNameIndex.Register(document, target, [], additions);
        ObjectLayerChanges.Register(objectLayer, (ObjectLayer)layers[layerIndex], [],
            locations.Select(l => l.ObjectIndex).ToArray());
        return target;
    }

    internal static MapDocument InsertObjects(MapDocument document, LayerId layerId,
        IReadOnlyDictionary<int, IReadOnlyList<SceneObject>> batches)
    {
        var additions = batches.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).ToArray();
        DocumentRevisionIndex.For(document).ValidateAppends(additions);
        var layers = document.Layers.ToArray(); var li = Array.FindIndex(layers, l => l.Id == layerId);
        var source = (ObjectLayer)layers[li];
        var revised = source with { Objects = source.ObjectStorage.InsertMany(batches) }; layers[li] = revised;
        var result = document.WithRevisedLayers(layers);
        var locations = new List<DocumentObjectLocation>(); var offset = 0;
        foreach (var batch in batches.OrderBy(kv => kv.Key))
            foreach (var item in batch.Value) locations.Add(new(item, layerId, li, batch.Key + offset++));
        DocumentRevisionIndex.RegisterAppends(document, result, locations, revised);
        ObjectNameIndex.Register(document, result, [], additions);
        ObjectLayerChanges.Register(source, revised, [], locations.Select(l => l.ObjectIndex).ToArray(), orderCompacted: true);
        return result;
    }

    /// <summary>
    /// Undo fast path for append commands. The normal LIFO history removes one tail range without
    /// flattening the prefix; an out-of-order direct Revert keeps the older id-based semantics.
    /// </summary>
    internal static MapDocument RemoveAppendedObjects(
        MapDocument document, LayerId layerId, IReadOnlyList<SceneObject> additions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(additions);
        var layers = document.Layers.ToArray();
        var layerIndex = Array.FindIndex(layers, layer => layer.Id == layerId);
        if (layerIndex < 0 || layers[layerIndex] is not ObjectLayer objectLayer)
        {
            throw new InvalidOperationException($"Layer {layerId} does not hold objects.");
        }

        var start = objectLayer.Objects.Count - additions.Count;
        var isTail = start >= 0;
        for (var offset = 0; isTail && offset < additions.Count; offset++)
        {
            isTail = objectLayer.Objects[start + offset].Id == additions[offset].Id;
        }

        if (isTail)
        {
            layers[layerIndex] = objectLayer with
            {
                Objects = objectLayer.ObjectStorage.RemoveRange(start, additions.Count),
            };
            var target = document.WithRevisedLayers(layers);
            DocumentRevisionIndex.RegisterTailRemoval(document, target, additions);
            ObjectNameIndex.Register(document, target,
                Enumerable.Range(start, additions.Count).Select(i => objectLayer.Objects[i]).ToArray(), []);
            ObjectLayerChanges.Register(objectLayer, (ObjectLayer)layers[layerIndex],
                Enumerable.Range(start, additions.Count).ToArray(), []);
            return target;
        }

        var ids = additions.Select(sceneObject => sceneObject.Id).ToHashSet();
        return document.WithLayer(layerId, layer =>
        {
            var current = (ObjectLayer)layer;
            return current with { Objects = [.. current.Objects.Where(item => !ids.Contains(item.Id))] };
        });
    }

    /// <summary>
    /// Bounded replacement-only revision: positions come from the current revision index, changed
    /// chunks are copied once, and document/spatial derived indexes share their warm roots.
    /// </summary>
    internal static MapDocument ReplaceObjects(
        MapDocument document, IReadOnlyList<SceneObject> replacements)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            throw new ArgumentException("At least one object replacement is required.", nameof(replacements));
        }

        var index = DocumentRevisionIndex.For(document);
        index.ValidateReplacements(replacements);
        var byLayer = new Dictionary<LayerId, List<(DocumentObjectLocation Location, SceneObject After)>>();
        var ids = new HashSet<ObjectId>();
        foreach (var replacement in replacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (!ids.Add(replacement.Id)
                || !index.TryFind(replacement.Id, out var location))
            {
                throw new InvalidOperationException(
                    $"Object {replacement.Id} is absent or repeated in the replacement delta.");
            }

            if (!byLayer.TryGetValue(location.LayerId, out var layerReplacements))
            {
                layerReplacements = [];
                byLayer.Add(location.LayerId, layerReplacements);
            }

            layerReplacements.Add((location, replacement));
        }

        var layers = document.Layers.ToArray();
        var revisedLocations = new List<DocumentObjectLocation>(replacements.Count);
        foreach (var layerReplacements in byLayer.Values)
        {
            var first = layerReplacements[0].Location;
            var sourceLayer = layers[first.LayerIndex] as ObjectLayer
                ?? throw new InvalidOperationException($"Layer {first.LayerId} no longer holds objects.");
            if (sourceLayer.Id != first.LayerId)
            {
                throw new InvalidOperationException("The document revision index no longer matches layer order.");
            }

            var positions = layerReplacements.ToDictionary(
                pair => pair.Location.ObjectIndex,
                pair => pair.After);
            foreach (var (position, replacement) in positions)
            {
                if (sourceLayer.Objects[position].Id != replacement.Id)
                {
                    throw new InvalidOperationException("The object revision index no longer matches object order.");
                }
            }

            var targetLayer = sourceLayer with
            {
                Objects = sourceLayer.ObjectStorage.ReplaceMany(positions),
            };
            layers[first.LayerIndex] = targetLayer;
            ObjectSpatialIndex.RegisterReplacements(sourceLayer, targetLayer, positions);
            ObjectLayerChanges.Register(sourceLayer, targetLayer, positions.Keys.ToArray(), positions.Keys.ToArray());
            revisedLocations.AddRange(layerReplacements.Select(pair =>
                pair.Location with { Object = pair.After }));
        }

        var target = document.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterReplacements(document, target, revisedLocations);
        ObjectNameIndex.Register(document, target,
            replacements.Select(item => index.FindObject(item.Id)!), replacements);
        return target;
    }

    /// <summary>Replaces one object wherever it lives - preview substitution for the canvas.</summary>
    public static MapDocument ReplaceObject(MapDocument document, SceneObject replacement)
        => ReplaceObjects(document, [replacement]);
}

/// <summary>Creates or removes the document-owned stamp foreground as one history entry.</summary>
public sealed class UpdateStampForegroundCommand(
    StampForegroundSurface? before, StampForegroundSurface? after, string name) : IEditCommand
{
    public string Name { get; } = name;
    public MapDocument Apply(MapDocument document) => document.WithStampForeground(after);
    public MapDocument Revert(MapDocument document) => document.WithStampForeground(before);
}

/// <summary>One foreground paint gesture stores only the touched sparse RGBA tiles.</summary>
public sealed class StampForegroundStrokeCommand(
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after,
    string name) : IEditCommand
{
    public string Name { get; } = name;
    public int TouchedTileCount => before.Count;
    public MapDocument Apply(MapDocument document) => Write(document, after);
    public MapDocument Revert(MapDocument document) => Write(document, before);

    private static MapDocument Write(
        MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> changes)
    {
        var foreground = document.StampForeground
            ?? throw new InvalidOperationException("The document has no stamp foreground surface.");
        return document.WithStampForeground(foreground with
        {
            Tiles = foreground.Tiles.WithMany(changes),
        });
    }
}
