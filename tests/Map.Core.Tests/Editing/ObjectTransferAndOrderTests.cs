using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// The object buffer, the paste command, the owner-layer move and the in-layer order. These cover
/// the defect where Ctrl+C captured a single object and Ctrl+V produced one nested composition
/// instead of the objects the user had actually copied.
/// </summary>
public sealed class ObjectTransferAndOrderTests
{
    [Fact]
    public void Copy_captures_every_selected_object_in_draw_order_with_its_assets()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var lower = Stamp("tree low", 100, 100, asset.Sha256, sublayer: -2);
        var upper = Stamp("tree high", 160, 140, asset.Sha256, sublayer: 3);
        var middle = Stamp("tree mid", 220, 180, asset.Sha256, sublayer: 0);
        var document = Document(layerId, [upper, middle, lower], asset);

        var payload = ObjectTransfer.Copy(document, [upper.Id, middle.Id, lower.Id]);

        // All three, not just the primary, and in the order the renderer draws them.
        Assert.Equal(3, payload.Count);
        Assert.Equal(["tree low", "tree mid", "tree high"], payload.Objects.Select(item => item.Name));
        Assert.Single(payload.Assets);

        // Positions are captured relative to the group's own top-left corner.
        Assert.Equal(100, payload.SourceLeft);
        Assert.Equal(100, payload.SourceTop);
        Assert.Equal(0, payload.Objects.Min(item => item.X));
        Assert.Equal(0, payload.Objects.Min(item => item.Y));
    }

    [Fact]
    public void Instantiate_keeps_the_mutual_layout_and_properties_but_mints_new_identities()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var group = Guid.NewGuid();
        var first = Stamp("a", 100, 100, asset.Sha256, sublayer: 2) with
        {
            GroupId = group, GroupName = "Роща", ObjectOpacity = 0.4,
        };
        var second = Stamp("b", 340, 260, asset.Sha256, sublayer: 2) with
        {
            GroupId = group, GroupName = "Роща", ObjectOpacity = 0.4,
        };
        var document = Document(layerId, [first, second], asset);

        var payload = ObjectTransfer.Copy(document, [first.Id, second.Id]);
        var pasted = ObjectTransfer.Instantiate(payload, payload.SourceLeft + 24, payload.SourceTop + 24);

        Assert.Equal(2, pasted.Count);
        Assert.All(pasted, item => Assert.DoesNotContain(item.Id, new[] { first.Id, second.Id }));

        // The gap between the two objects is exactly what it was, and the whole group moved by one
        // shared offset rather than each object drifting on its own.
        Assert.Equal(340 - 100, pasted[1].X - pasted[0].X);
        Assert.Equal(260 - 100, pasted[1].Y - pasted[0].Y);
        Assert.Equal(124, pasted[0].X);
        Assert.Equal(124, pasted[0].Y);

        Assert.All(pasted, item => Assert.Equal(2, item.Sublayer));
        Assert.All(pasted, item => Assert.Equal(0.4, item.ObjectOpacity));
        Assert.All(pasted, item => Assert.Equal(asset.Sha256, item.Stamp!.AssetSha256));

        // The copy is grouped exactly like the original without joining the original group.
        var pastedGroup = Assert.Single(pasted.Select(item => item.GroupId).Distinct());
        Assert.NotNull(pastedGroup);
        Assert.NotEqual(group, pastedGroup);
    }

    [Fact]
    public void Pasting_a_whole_group_is_one_undoable_entry_that_also_carries_its_asset()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var first = Stamp("a", 100, 100, asset.Sha256);
        var second = Stamp("b", 200, 100, asset.Sha256);
        var third = Stamp("c", 300, 100, asset.Sha256);
        var session = new EditingSession(Document(layerId, [first, second, third], asset));

        var payload = ObjectTransfer.Copy(session.Document, [first.Id, second.Id, third.Id]);
        var objects = ObjectTransfer.Instantiate(payload, payload.SourceLeft + 24, payload.SourceTop + 24);
        var paste = new PasteObjectsCommand(layerId, objects, payload.Assets);
        session.Execute(paste);

        Assert.Equal(6, Objects(session, layerId).Count);
        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(3, paste.PastedObjectIds.Count);

        // One undo removes the entire pasted group, and one redo brings all of it back.
        Assert.True(session.Undo());
        Assert.Equal(3, Objects(session, layerId).Count);
        Assert.True(session.Redo());
        Assert.Equal(6, Objects(session, layerId).Count);
        Assert.Equal(paste.PastedObjectIds, Objects(session, layerId).TakeLast(3).Select(item => item.Id));
    }

    [Fact]
    public void Pasting_into_a_document_without_the_content_introduces_it_and_undo_withdraws_it()
    {
        var sourceLayer = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var stamp = Stamp("a", 10, 10, asset.Sha256);
        var payload = ObjectTransfer.Copy(Document(sourceLayer, [stamp], asset), [stamp.Id]);

        var targetLayer = LayerId.New();
        var session = new EditingSession(Document(targetLayer, [], asset: null));
        Assert.Empty(session.Document.Assets);

        var objects = ObjectTransfer.Instantiate(payload, 0, 0);
        session.Execute(new PasteObjectsCommand(targetLayer, objects, payload.Assets));

        Assert.Single(session.Document.Assets);
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Assets);
    }

    [Fact]
    public void Moving_objects_to_another_layer_keeps_their_own_level_and_is_undoable()
    {
        var source = LayerId.New();
        var target = LayerId.New();
        var moved = Stamp("a", 10, 10, null, sublayer: 4);
        var stays = Stamp("b", 20, 20, null, sublayer: -1);
        var document = Document(source, [moved, stays], asset: null)
            .WithLayers([
                .. Document(source, [moved, stays], asset: null).Layers,
                new ObjectLayer(target, "Объекты 2", true, false, 1.0, []),
            ]);
        var session = new EditingSession(document);

        session.Execute(new MoveObjectsToLayerCommand(session.Document, source, target, [moved.Id]));

        Assert.Equal(["b"], Objects(session, source).Select(item => item.Name));
        var arrived = Assert.Single(Objects(session, target));
        Assert.Equal("a", arrived.Name);
        Assert.Equal(4, arrived.Sublayer);

        Assert.True(session.Undo());
        Assert.Equal(["a", "b"], Objects(session, source).Select(item => item.Name));
        Assert.Empty(Objects(session, target));
    }

    [Fact]
    public void Reordering_changes_only_the_draw_order_inside_the_layer()
    {
        var layerId = LayerId.New();
        var bottom = Stamp("bottom", 0, 0, null);
        var middle = Stamp("middle", 0, 0, null);
        var top = Stamp("top", 0, 0, null);
        var session = new EditingSession(Document(layerId, [bottom, middle, top], asset: null));

        var before = Objects(session, layerId);
        IReadOnlyList<SceneObject> after = [middle, bottom, top];
        session.Execute(new ReorderObjectsCommand(layerId, before, after, "Move selection down"));

        Assert.Equal(["middle", "bottom", "top"], Objects(session, layerId).Select(item => item.Name));

        // Draw order follows the list inside one sublayer band, so the render answer changed too.
        var layer = (ObjectLayer)session.Document.FindLayer(layerId)!;
        Assert.Equal(["middle", "bottom", "top"], SceneGeometry.InDrawOrder(layer).Select(item => item.Name));

        Assert.True(session.Undo());
        Assert.Equal(["bottom", "middle", "top"], Objects(session, layerId).Select(item => item.Name));
    }

    [Fact]
    public void A_placement_that_had_to_create_its_layer_is_undone_in_one_step()
    {
        var session = new EditingSession(new MapDocument(
            DocumentId.New(), "no object layer", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]));
        Assert.Empty(session.Document.Layers.OfType<ObjectLayer>());

        var asset = Embedded(new string('a', 64), "tree");
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, []);
        var stamp = Stamp("Дерево", 100, 100, asset.Sha256);
        session.Execute(new CompositeCommand("Place stamp on a new layer",
        [
            new AddLayerCommand(layer),
            new PlaceAssetObjectsCommand(layer.Id, asset, [stamp]),
        ]));

        Assert.Single(session.Document.Layers.OfType<ObjectLayer>());
        Assert.Single(Objects(session, layer.Id));
        Assert.Equal(1, session.UndoDepth);

        // One undo takes both the object and the layer it needed - no leftover empty layer.
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Layers.OfType<ObjectLayer>());
        Assert.Empty(session.Document.Assets);
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<SceneObject> Objects(EditingSession session, LayerId layerId) =>
        ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects;

    private static SceneObject Stamp(
        string name, double x, double y, string? assetSha256, int sublayer = 0) =>
        new(ObjectId.New(), assetSha256 is null ? SceneObjectKind.Rectangle : SceneObjectKind.Stamp,
            name, x, y, 48, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Sublayer = sublayer,
            Stamp = assetSha256 is null ? null : new StampStyle(assetSha256),
        };

    private static MapDocument Document(
        LayerId layerId, IReadOnlyList<SceneObject> objects, MapAssetReference? asset)
    {
        var document = new MapDocument(
            DocumentId.New(), "objects", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(layerId, "Объекты", true, false, 1.0, objects),
            ]);
        return asset is null ? document : document.WithAssets([asset]);
    }

    private static MapAssetReference Embedded(string hash, string name) => new(
        hash, name, "image/png", "png", ProjectAssetStorage.Embedded,
        DateTimeOffset.UnixEpoch, "test", "test", [1, 2, 3]);
}
