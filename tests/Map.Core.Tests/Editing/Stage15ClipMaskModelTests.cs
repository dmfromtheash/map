using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage15ClipMaskModelTests
{
    [Fact]
    public void Stage15ClipMask_variant_pairing_is_deterministic_and_never_swaps_masks()
    {
        var visualA = new string('a', 64);
        var visualB = new string('b', 64);
        var maskA = new string('c', 64);
        var maskB = new string('d', 64);
        var request = new StampPlacementRequest
        {
            CenterX = 300,
            CenterY = 200,
            AreaRadius = 120,
            Density = 80,
            BaseWidth = 48,
            Seed = 1515,
            RandomizeVariant = true,
        };
        var variants = new[]
        {
            new StampVariantSource(visualA, 64, 32, maskA),
            new StampVariantSource(visualB, 32, 64, maskB),
        };

        var first = StampPlacement.PlanArea(request, variants);
        var replay = StampPlacement.PlanArea(request, variants);

        Assert.Equal(first, replay);
        Assert.Contains(first, item => item.Sha256 == visualA);
        Assert.Contains(first, item => item.Sha256 == visualB);
        Assert.All(first, item => Assert.Equal(
            item.Sha256 == visualA ? maskA : maskB, item.MaskSha256));
    }

    [Fact]
    public void Stage15ClipMask_foreground_stroke_resize_region_and_undo_keep_sparse_rgba_tiles()
    {
        var key = new TileKey(0, 0);
        var pixels = Enumerable.Repeat((byte)77, TileMap.TileSize * TileMap.TileSize * 4).ToArray();
        var document = Document(StampForegroundSurface.CreateEmpty());
        var session = new EditingSession(document);
        var command = new StampForegroundStrokeCommand(
            new Dictionary<TileKey, byte[]?> { [key] = null },
            new Dictionary<TileKey, byte[]?> { [key] = pixels },
            "paint foreground");

        session.Execute(command);

        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(pixels, session.Document.StampForeground!.Tiles.TryGet(key));
        Assert.True(session.Undo());
        Assert.True(session.Document.StampForeground!.IsEmpty);
        Assert.True(session.Redo());

        var plan = CanvasTransformService.PlanResize(
            session.Document, 768, 512, CanvasAnchor.BottomRight);
        var resized = CanvasTransformService.ApplyResize(session.Document, plan);
        Assert.Equal(4, resized.StampForeground!.Tiles.BytesPerPixel);
        Assert.Single(resized.StampForeground.Tiles.Keys);

        var extracted = CanvasTransformService.ExtractRegion(
            session.Document,
            new CanvasRegion(0, 0, 256, 256),
            "region",
            DocumentId.New(),
            DateTimeOffset.UnixEpoch);
        Assert.NotNull(extracted.StampForeground);
        Assert.Single(extracted.StampForeground!.Tiles.Keys);
    }

    [Fact]
    public void Stage15ClipMask_defaults_and_checkpoint_snapshot_are_backward_safe()
    {
        var stamp = new StampStyle(new string('a', 64));
        Assert.Null(stamp.MaskAssetSha256);
        Assert.False(stamp.ClipMaskEnabled);

        var foreground = StampForegroundSurface.CreateEmpty();
        var document = Document(foreground);
        var materialized = MapCheckpointSnapshot.Capture(document).Materialize(document.Id);
        Assert.Same(foreground, materialized.StampForeground);
    }

    [Fact]
    public void Stage15ClipMask_replace_and_copy_preserve_the_resolved_visual_mask_pair()
    {
        var oldVisual = Asset(new string('a', 64), "old visual");
        var oldMask = Asset(new string('b', 64), "old mask");
        var newVisual = Asset(new string('c', 64), "new visual");
        var newMask = Asset(new string('d', 64), "new mask");
        var layerId = LayerId.New();
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "old", 10, 20, 80, 40, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(oldVisual.Sha256)
            {
                MaskAssetSha256 = oldMask.Sha256,
                ClipMaskEnabled = true,
            },
        };
        var document = new MapDocument(
            DocumentId.New(), "replace", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(layerId, "objects", true, false, 1, [stamp]),
            ],
            [oldVisual, oldMask]);
        var session = new EditingSession(document);

        session.Execute(new ReplaceStampAssetCommand(
            stamp, newVisual, replacementMaskSha256: newMask.Sha256, replacementMask: newMask));
        var replaced = session.SelectedObject
            ?? ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Single();
        Assert.Equal(newVisual.Sha256, replaced.Stamp!.AssetSha256);
        Assert.Equal(newMask.Sha256, replaced.Stamp.MaskAssetSha256);
        Assert.True(replaced.Stamp.ClipMaskEnabled);
        Assert.True(session.Undo());

        var package = CompositionTransfer.Copy(session.Document, [stamp.Id]);
        Assert.Equal(2, package.Assets.Count);
        var instance = CompositionTransfer.CreateInstance(package, 100, 100);
        var copied = instance.Composition!.Items.Single();
        Assert.Equal(oldVisual.Sha256, copied.Stamp!.AssetSha256);
        Assert.Equal(oldMask.Sha256, copied.Stamp.MaskAssetSha256);
        Assert.True(copied.Stamp.ClipMaskEnabled);
    }

    private static MapDocument Document(StampForegroundSurface foreground) => new(
        DocumentId.New(),
        "clip",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF")],
        stampForeground: foreground);

    private static MapAssetReference Asset(string sha, string name) => new(
        sha, name, "image/png", "png", ProjectAssetStorage.Embedded,
        DateTimeOffset.UnixEpoch, "test", "test", [1, 2, 3]);
}
