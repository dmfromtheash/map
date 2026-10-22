using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage12BCanvasWorkflowTests
{
    [Theory]
    [InlineData(CanvasAnchor.TopLeft, 0, 0)]
    [InlineData(CanvasAnchor.TopCenter, -10, 0)]
    [InlineData(CanvasAnchor.TopRight, -21, 0)]
    [InlineData(CanvasAnchor.CenterLeft, 0, -14)]
    [InlineData(CanvasAnchor.Center, -10, -14)]
    [InlineData(CanvasAnchor.CenterRight, -21, -14)]
    [InlineData(CanvasAnchor.BottomLeft, 0, -29)]
    [InlineData(CanvasAnchor.BottomCenter, -10, -29)]
    [InlineData(CanvasAnchor.BottomRight, -21, -29)]
    public void Stage12B_all_nine_anchors_share_the_integer_preview_and_apply_offset(
        CanvasAnchor anchor, int expectedX, int expectedY)
    {
        var item = Object("anchor", 12, 18, 20, 20);
        var document = Document(101, 99, [item]);

        var plan = CanvasTransformService.PlanResize(document, 80, 70, anchor);
        var preview = CanvasTransformService.ApplyResize(document, plan);
        var moved = Objects(preview).Single();

        Assert.Equal(expectedX, plan.OffsetX);
        Assert.Equal(expectedY, plan.OffsetY);
        Assert.Equal(12 + expectedX, moved.X);
        Assert.Equal(18 + expectedY, moved.Y);
        Assert.Equal(80, preview.Canvas.Width);
        Assert.Equal(70, preview.Canvas.Height);
        Assert.Equal(new WorldBounds(expectedX, expectedY, expectedX + 101, expectedY + 99),
            plan.OldBoundsInResult);
    }

    [Fact]
    public async Task Stage12B_preview_and_cancel_are_derived_then_apply_is_one_undo_redo_entry()
    {
        var source = Document(600, 500, [Object("inside", 100, 100, 40, 40)]);
        var (editor, store, picker) = Editor(source, startDirty: false, path: "source.mapproj");

        editor.BeginCanvasWorkflow();
        editor.CanvasWidthText = "621";
        editor.CanvasHeightText = "521";
        editor.SetCanvasAnchor(CanvasAnchor.BottomRight);

        var preview = editor.RenderDocument;
        Assert.NotSame(source, preview);
        Assert.Equal(21, Objects(preview).Single().X - Objects(source).Single().X);
        Assert.Same(source, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.False(editor.Session.IsDirty);

        editor.CancelCanvasWorkflow();
        Assert.Same(source, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.False(editor.Session.IsDirty);
        Assert.Empty(store.Saved);

        editor.BeginCanvasWorkflow();
        editor.CanvasWidthText = "621";
        editor.CanvasHeightText = "521";
        editor.SetCanvasAnchor(CanvasAnchor.BottomRight);
        var exactPreview = editor.RenderDocument;
        Assert.True(editor.ApplyCanvasResize());

        Assert.Same(exactPreview, editor.Session.Document);
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.True(editor.Session.IsDirty);
        editor.Undo();
        Assert.Same(source, editor.Session.Document);
        Assert.True(editor.Session.CanRedo);
        editor.Redo();
        Assert.Same(exactPreview, editor.Session.Document);

        editor.BeginCanvasWorkflow();
        editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
        picker.NextSavePath = null;
        var beforeCancel = editor.Session.Document;
        var depthBeforeCancel = editor.Session.UndoDepth;
        Assert.False(await editor.CreateMapFromRegionAsync());
        Assert.Same(beforeCancel, editor.Session.Document);
        Assert.Equal(depthBeforeCancel, editor.Session.UndoDepth);
    }

    [Fact]
    public void Stage12B_resize_moves_vectors_grids_notes_rgba_land_and_masks_then_round_trips()
    {
        var document = RichRasterDocument();
        var session = new EditingSession(document);
        var plan = CanvasTransformService.PlanResize(document, 620, 520, CanvasAnchor.BottomRight);
        var after = CanvasTransformService.ApplyResize(document, plan);
        session.Execute(new ResizeCanvasCommand(document, after));

        Assert.Equal((20, 20), (plan.OffsetX, plan.OffsetY));
        Assert.Equal(120d, Objects(session.Document).First(item => item.Name == "inside").X);
        var grid = session.Document.Layers.OfType<GridLayer>().Single().Grids.Single();
        Assert.Equal(33d, grid.OffsetX);
        Assert.Equal(27d, grid.OffsetY);
        Assert.Equal(40d, session.Document.Notes.Single(note => note.AnchorKind == NoteAnchorKind.World).WorldX);
        Assert.Equal(0d, session.Document.Notes.Single(note => note.AnchorKind == NoteAnchorKind.Object).WorldX);

        var brush = session.Document.Layers.OfType<BrushLayer>().Single();
        var land = session.Document.Layers.OfType<LandLayer>().Single();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Pixel(brush.Tiles, 270, 270));
        Assert.Equal(new byte[] { 211 }, Pixel(brush.Mask!.Tiles, 275, 25));
        Assert.Equal(new byte[] { 177 }, Pixel(land.GeometryTiles, 280, 40));
        Assert.Equal(new byte[] { 99 }, Pixel(land.Mask!.Tiles, 281, 41));
        Assert.Equal(2, brush.Tiles.TileCount); // the other retained pixel remains sparse and materialized

        Assert.True(session.Undo());
        Assert.Same(document, session.Document);
        Assert.True(session.Redo());
        Assert.Same(after, session.Document);

        var temp = TempDirectory();
        try
        {
            var path = Path.Combine(temp, "resized.mapproj");
            MapProjectStore.Save(after, path, new SaveMetadata("0.2.0", DateTimeOffset.UnixEpoch),
                ThumbnailRenderer.RenderPng(after));
            var reopened = MapProjectStore.Open(path).Document;
            Assert.Equal((620, 520), (reopened.Canvas.Width, reopened.Canvas.Height));
            Assert.Equal(new byte[] { 1, 2, 3, 4 },
                Pixel(reopened.Layers.OfType<BrushLayer>().Single().Tiles, 270, 270));
            Assert.Equal(new byte[] { 99 },
                Pixel(reopened.Layers.OfType<LandLayer>().Single().Mask!.Tiles, 281, 41));
            Assert.Equal(33d, reopened.Layers.OfType<GridLayer>().Single().Grids.Single().OffsetX);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }

        var cropPlan = CanvasTransformService.PlanResize(document, 300, 300, CanvasAnchor.TopLeft);
        var cropped = CanvasTransformService.ApplyResize(document, cropPlan);
        Assert.Contains(Objects(cropped), item => item.Name == "outside");
        Assert.Null(SceneGeometry.HitTest(cropped, 550, 120));
        Assert.NotNull(cropped.Layers.OfType<BrushLayer>().Single().Tiles.TryGet(new TileKey(2, 0)));

        var expanded = CanvasTransformService.ApplyResize(cropped,
            CanvasTransformService.PlanResize(cropped, 600, 500, CanvasAnchor.TopLeft));
        Assert.Equal("outside", SceneGeometry.HitTest(expanded, 550, 120)!.Name);
    }

    [Fact]
    public void Stage12B_boundary_snap_uses_shared_isometric_and_hex_geometry_not_square_rounding()
    {
        var region = new CanvasRegion(17, 19, 123, 91);
        var isometric = new GridDefinition(GridId.New(), GridGeometry.IsometricSquares, 40)
        {
            SnapEnabled = true,
        };
        var square = isometric with { Id = GridId.New(), Geometry = GridGeometry.Square };

        var snapped = CanvasTransformService.SnapRegion(region, isometric);
        var squareResult = CanvasTransformService.SnapRegion(region, square);
        var first = GridGeometryContract.Snap(isometric, new VectorPoint(region.X, region.Y));
        var second = GridGeometryContract.Snap(isometric, new VectorPoint(region.Right, region.Bottom));

        Assert.Equal((int)Math.Round(Math.Min(first.X, second.X), MidpointRounding.AwayFromZero), snapped.X);
        Assert.Equal((int)Math.Round(Math.Min(first.Y, second.Y), MidpointRounding.AwayFromZero), snapped.Y);
        Assert.NotEqual(squareResult, snapped);

        foreach (var geometry in new[] { GridGeometry.HexFlat, GridGeometry.HexPointy })
        {
            var hex = isometric with { Id = GridId.New(), Geometry = geometry };
            var expectedFirst = GridGeometryContract.Snap(hex, new VectorPoint(region.X, region.Y));
            var expectedSecond = GridGeometryContract.Snap(hex, new VectorPoint(region.Right, region.Bottom));
            var expected = CanvasRegion.FromCorners(
                expectedFirst.X, expectedFirst.Y, expectedSecond.X, expectedSecond.Y);
            var actual = CanvasTransformService.SnapRegion(region, hex);
            Assert.Equal(expected, actual);
            Assert.NotEqual(squareResult, actual);
        }

        var noGrid = Editor(Document(600, 500, []), startDirty: false).Editor;
        noGrid.BeginCanvasWorkflow();
        noGrid.SetCanvasBoundarySnap(true);
        Assert.Contains("Нет видимой активной сетки", noGrid.CanvasWorkflowError);
        Assert.False(noGrid.ApplyCanvasResize());

        var coarseGrid = new GridDefinition(GridId.New(), GridGeometry.Square, 100)
        {
            SnapEnabled = true,
        };
        var collapsed = Assert.Throws<InvalidOperationException>(() =>
            CanvasTransformService.SnapRegion(new CanvasRegion(1, 1, 16, 16), coarseGrid));
        Assert.Contains("границы совпали", collapsed.Message);
    }

    [Fact]
    public void Stage12B_region_extraction_keeps_intersections_groups_composition_notes_assets_and_sparse_pixels()
    {
        var source = RegionSourceDocument();
        var sourceId = source.Id;
        var sourceLayers = source.Layers;
        var region = new CanvasRegion(250, 50, 200, 200);
        var created = DateTimeOffset.Parse("2026-08-31T10:00:00Z");
        var newId = DocumentId.New();

        var extracted = CanvasTransformService.ExtractRegion(source, region, "  Регион  ", newId, created);

        Assert.Equal(newId, extracted.Id);
        Assert.NotEqual(sourceId, extracted.Id);
        Assert.Equal("Регион", extracted.Title);
        Assert.Equal(created, extracted.CreatedUtc);
        Assert.Equal((200, 200), (extracted.Canvas.Width, extracted.Canvas.Height));
        Assert.Empty(extracted.Checkpoints);
        Assert.Equal(source.Layers.Select(layer => layer.Id), extracted.Layers.Select(layer => layer.Id));

        var objects = Objects(extracted);
        Assert.Contains(objects, item => item.Name == "rotated-intersection");
        Assert.Contains(objects, item => item.Name == "hidden-locked");
        Assert.Contains(objects, item => item.Name == "composition");
        Assert.DoesNotContain(objects, item => item.Name == "outside-ungrouped");
        var grouped = objects.Where(item => item.GroupId is not null).ToList();
        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, item => Assert.Equal("Связанные", item.GroupName));
        Assert.Contains(grouped, item => item.Name == "group-outside" && item.X > extracted.Canvas.Width);
        Assert.True(objects.Single(item => item.Name == "hidden-locked").Locked);
        Assert.False(objects.Single(item => item.Name == "hidden-locked").Visible);
        Assert.NotNull(objects.Single(item => item.Name == "composition").Composition);

        var worldNote = extracted.Notes.Single(note => note.AnchorKind == NoteAnchorKind.World);
        Assert.Equal((10d, 10d), (worldNote.WorldX, worldNote.WorldY));
        var objectNote = extracted.Notes.Single(note => note.AnchorKind == NoteAnchorKind.Object);
        Assert.Contains(objects, item => item.Id == objectNote.ObjectId);

        var brush = extracted.Layers.OfType<BrushLayer>().Single();
        var land = extracted.Layers.OfType<LandLayer>().Single();
        Assert.Equal(new byte[] { 8, 7, 6, 5 }, Pixel(brush.Tiles, 5, 10));
        Assert.Equal(new byte[] { 144 }, Pixel(brush.Mask!.Tiles, 6, 11));
        Assert.Equal(new byte[] { 188 }, Pixel(land.GeometryTiles, 7, 12));
        Assert.Equal(new byte[] { 77 }, Pixel(land.Mask!.Tiles, 8, 13));
        Assert.Empty(Pixel(brush.Tiles, 0, 0));

        var used = extracted.Assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, used.Count);
        Assert.DoesNotContain(source.Assets.Last().Sha256, used);
        Assert.Contains(extracted.Layers.OfType<FilterLayer>().Single().Settings.TextureAssetSha256!, used);

        var temp = TempDirectory();
        try
        {
            var path = Path.Combine(temp, "region.mapproj");
            MapProjectStore.Save(extracted, path, new SaveMetadata("0.2.0", created),
                ThumbnailRenderer.RenderPng(extracted));
            var reopened = MapProjectStore.Open(path).Document;
            Assert.Equal(newId, reopened.Id);
            Assert.Empty(reopened.Checkpoints);
            Assert.Equal(new byte[] { 8, 7, 6, 5 },
                Pixel(reopened.Layers.OfType<BrushLayer>().Single().Tiles, 5, 10));
            Assert.Equal(objectNote.ObjectId,
                reopened.Notes.Single(note => note.AnchorKind == NoteAnchorKind.Object).ObjectId);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }

        var extractedPayload = extracted.Assets.First().EmbeddedContent!;
        var sourcePayload = source.Assets.Single(asset => asset.Sha256 == extracted.Assets.First().Sha256)
            .EmbeddedContent!;
        var sourceFirstByte = sourcePayload[0];
        extractedPayload[0] ^= 0xff;
        Assert.Equal(sourceFirstByte, sourcePayload[0]);
        Assert.Equal(sourceId, source.Id);
        Assert.Same(sourceLayers, source.Layers);
        Assert.Single(source.Checkpoints);
        Assert.Equal(new byte[] { 8, 7, 6, 5 },
            Pixel(source.Layers.OfType<BrushLayer>().Single().Tiles, 255, 60));
    }

    [Fact]
    public async Task Stage12B_region_save_uses_current_unsaved_snapshot_and_never_changes_source_session()
    {
        var source = WithCheckpoint(Document(600, 500, []));
        var setup = Editor(source, startDirty: false, path: "C:\\maps\\source.mapproj");
        setup.Editor.AddObject(SceneObjectKind.Rectangle);
        var current = setup.Editor.Session.Document;
        var dirty = setup.Editor.Session.IsDirty;
        var undoDepth = setup.Editor.Session.UndoDepth;
        setup.Picker.NextSavePath = "C:\\maps\\region.mapproj";

        setup.Editor.BeginCanvasWorkflow();
        setup.Editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
        setup.Editor.RegionXText = "0";
        setup.Editor.RegionYText = "0";
        setup.Editor.RegionWidthText = "600";
        setup.Editor.RegionHeightText = "500";
        setup.Editor.RegionTitleText = "  Независимая  ";
        Assert.True(await setup.Editor.CreateMapFromRegionAsync());

        var saved = setup.Store.Saved["C:\\maps\\region.mapproj"].Document;
        Assert.Equal("Независимая", saved.Title);
        Assert.NotEqual(current.Id, saved.Id);
        Assert.Equal(Objects(current).Count, Objects(saved).Count);
        Assert.Empty(saved.Checkpoints);
        Assert.Same(current, setup.Editor.Session.Document);
        Assert.Equal("C:\\maps\\source.mapproj", setup.Editor.DocumentPath);
        Assert.Equal(dirty, setup.Editor.Session.IsDirty);
        Assert.Equal(undoDepth, setup.Editor.Session.UndoDepth);
        Assert.Single(setup.Editor.Session.Document.Checkpoints);

        var previousDestination = Document(64, 64, []);
        setup.Store.Saved["C:\\maps\\protected.mapproj"] =
            (previousDestination, new SaveMetadata("0.2.0", DateTimeOffset.UnixEpoch), [1, 2, 3]);
        setup.Store.NextSaveFailures.Enqueue(new MapProjectSaveException(
            "injected", "C:\\maps\\protected.mapproj", null));
        setup.Picker.NextSavePath = "C:\\maps\\protected.mapproj";
        setup.Editor.BeginCanvasWorkflow();
        setup.Editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
        var beforeFailure = setup.Editor.Session.Document;
        var depthBeforeFailure = setup.Editor.Session.UndoDepth;
        Assert.False(await setup.Editor.CreateMapFromRegionAsync());
        Assert.Same(beforeFailure, setup.Editor.Session.Document);
        Assert.Equal(depthBeforeFailure, setup.Editor.Session.UndoDepth);
        Assert.Same(previousDestination, setup.Store.Saved["C:\\maps\\protected.mapproj"].Document);
        Assert.Contains("Не удалось завершить сохранение атласа", setup.Editor.StatusDetail);
    }

    [Fact]
    public void Stage12B_atomic_destination_failure_preserves_previous_file_and_source()
    {
        var source = RegionSourceDocument();
        var extracted = CanvasTransformService.ExtractRegion(
            source, new CanvasRegion(250, 50, 200, 200), "region", DocumentId.New(), DateTimeOffset.UtcNow);
        var previous = Document(64, 64, []);
        var temp = TempDirectory();
        try
        {
            var path = Path.Combine(temp, "destination.mapproj");
            MapProjectStore.Save(previous, path, new SaveMetadata("0.2.0", DateTimeOffset.UnixEpoch),
                ThumbnailRenderer.RenderPng(previous));
            var before = File.ReadAllBytes(path);

            Assert.Throws<MapProjectSaveException>(() => MapProjectStore.Save(
                extracted,
                path,
                new SaveMetadata("0.2.0", DateTimeOffset.UtcNow),
                ThumbnailRenderer.RenderPng(extracted),
                faults: new SaveFaultInjection
                {
                    AfterDurableTempBeforeReplace = () => throw new IOException("Stage12B injected"),
                }));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(previous.Id, MapProjectStore.Open(path).Document.Id);
            Assert.Single(source.Checkpoints);
            Assert.Equal(600, source.Canvas.Width);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Stage12B_scene_renderer_and_thumbnail_clip_everything_to_canvas()
    {
        var outside = Object(
            "outside", 120, 20, 30, 30,
            fill: "#FFFF0000", stroke: "#FFFF0000");
        var raster = SetPixel(TileMap.Empty(4), 130, 30, 255, 0, 0, 255);
        var document = new MapDocument(
            DocumentId.New(), "clip", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(100, 100, "px", "#00000000"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#00000000"),
                new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [outside]),
                new BrushLayer(LayerId.New(), "Brush", true, false, 1, raster),
            ]);

        using var surface = SKSurface.Create(new SKImageInfo(200, 100))!;
        surface.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(surface.Canvas, document,
            new RenderOptions { VisibleWorld = new WorldBounds(0, 0, 200, 100) });
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(0, bitmap.GetPixel(130, 30).Alpha);
        Assert.Null(SceneGeometry.HitTest(document, 130, 30));

        using var thumbnail = SKBitmap.Decode(ThumbnailRenderer.RenderPng(document, 200, 100));
        Assert.Equal(0, thumbnail.GetPixel(180, 30).Alpha);
    }

    private static (EditorViewModel Editor, FakeProjectStore Store, FakeFilePicker Picker) Editor(
        MapDocument document, bool startDirty, string? path = null)
    {
        var store = new FakeProjectStore();
        var picker = new FakeFilePicker();
        return (new EditorViewModel(
            document, path, startDirty, store, new FakeRecentMaps(), picker,
            new FakePrompts(), new FakeLog(), () => DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
            "0.2.0"), store, picker);
    }

    private static MapDocument Document(
        int width, int height, IReadOnlyList<SceneObject> objects, IReadOnlyList<MapNote>? notes = null) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(width, height, "px", "#00000000"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#00000000"),
            new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects),
        ], notes: notes);

    private static MapDocument RichRasterDocument()
    {
        var inside = Object("inside", 100, 100, 40, 40);
        var outside = Object("outside", 530, 100, 40, 40);
        var brushTiles = SetPixel(TileMap.Empty(4), 250, 250, 1, 2, 3, 4);
        brushTiles = SetPixel(brushTiles, 530, 40, 4, 3, 2, 1);
        var brushMask = new LayerMask(SetPixel(TileMap.Empty(1), 255, 5, 211), true);
        var landTiles = SetPixel(TileMap.Empty(1), 260, 20, 177);
        var landMask = new LayerMask(SetPixel(TileMap.Empty(1), 261, 21, 99), true);
        var grid = new GridDefinition(GridId.New(), GridGeometry.HexFlat, 40)
        {
            OffsetX = 13,
            OffsetY = 7,
            SnapEnabled = true,
        };
        return new MapDocument(
            DocumentId.New(), "rich", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(600, 500, "px", "#00000000"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#00000000"),
                new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [inside, outside]),
                new BrushLayer(LayerId.New(), "Brush", true, false, 1, brushTiles, brushMask),
                new LandLayer(LayerId.New(), "Land", true, false, 1, landTiles, new LandEdgeStyle(), landMask),
                new GridLayer(LayerId.New(), "Grid", true, false, 1, [grid]),
            ],
            notes:
            [
                new MapNote(NoteId.New(), "World", "note", NoteAnchorKind.World, 20, 30),
                new MapNote(NoteId.New(), "Object", "note", NoteAnchorKind.Object, objectId: inside.Id),
            ]);
    }

    private static MapDocument RegionSourceDocument()
    {
        var groupId = Guid.NewGuid();
        var rotated = Object("rotated-intersection", 420, 80, 70, 50, rotation: 35);
        var groupedHit = Object("group-hit", 430, 120, 40, 40) with
        {
            GroupId = groupId,
            GroupName = "Связанные",
        };
        var groupedOutside = Object("group-outside", 520, 130, 40, 40) with
        {
            GroupId = groupId,
            GroupName = "Связанные",
        };
        var outside = Object("outside-ungrouped", 530, 300, 30, 30);
        var hiddenLocked = Object(
            "hidden-locked", 300, 100, 30, 30, visible: false, locked: true);

        var asset1 = Asset("stamp", "#FF112233");
        var asset2 = Asset("composition", "#FF223344");
        var asset3 = Asset("filter", "#FF334455");
        var unused = Asset("unused", "#FF445566");
        var stamp = Object("stamp", 280, 80, 30, 30) with
        {
            Kind = SceneObjectKind.Stamp,
            Stamp = new StampStyle(asset1.Sha256),
        };
        var child = Object("composition-child", 0, 0, 20, 20) with
        {
            Kind = SceneObjectKind.Stamp,
            Stamp = new StampStyle(asset2.Sha256),
        };
        var composition = Object("composition", 260, 170, 60, 60) with
        {
            Kind = SceneObjectKind.Composition,
            Composition = new EditableComposition(
                CompositionId.New(), DocumentId.New(), "composition", 60, 60, [child]),
        };

        var brushTiles = SetPixel(TileMap.Empty(4), 255, 60, 8, 7, 6, 5);
        brushTiles = SetPixel(brushTiles, 100, 100, 1, 1, 1, 1);
        var brushMask = new LayerMask(SetPixel(TileMap.Empty(1), 256, 61, 144), true);
        var landTiles = SetPixel(TileMap.Empty(1), 257, 62, 188);
        var landMask = new LayerMask(SetPixel(TileMap.Empty(1), 258, 63, 77), true);
        var objectLayer = new ObjectLayer(
            LayerId.New(), "Objects", visible: false, locked: true, opacity: 0.65,
            [rotated, groupedHit, groupedOutside, outside, hiddenLocked, stamp, composition])
        { Blend = "Multiply" };
        var filter = new FilterLayer(
            LayerId.New(), "Filter", true, false, 0.8,
            new FilterSettings { TextureAssetSha256 = asset3.Sha256 });
        var baseline = new MapDocument(
            DocumentId.New(), "source", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(600, 500, "px", "#00000000"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#00000000"),
                objectLayer,
                new BrushLayer(LayerId.New(), "Brush", true, false, 1, brushTiles, brushMask),
                new LandLayer(LayerId.New(), "Land", true, false, 1, landTiles, new LandEdgeStyle(), landMask),
                new GridLayer(LayerId.New(), "Grid", true, false, 1,
                    [new GridDefinition(GridId.New(), GridGeometry.HexPointy, 30) { OffsetX = 5, OffsetY = 9 }]),
                filter,
            ],
            [asset1, asset2, asset3, unused],
            [
                new MapNote(NoteId.New(), "World", "note", NoteAnchorKind.World, 260, 60),
                new MapNote(NoteId.New(), "Object", "note", NoteAnchorKind.Object, objectId: groupedOutside.Id),
                new MapNote(NoteId.New(), "Excluded", "note", NoteAnchorKind.Object, objectId: outside.Id),
            ]);
        return WithCheckpoint(baseline);
    }

    private static MapDocument WithCheckpoint(MapDocument document)
    {
        var checkpoint = new MapCheckpoint(
            CheckpointId.New(), "До", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(document), ThumbnailRenderer.RenderPng(document, 64, 64));
        return document.WithCheckpoints([checkpoint]);
    }

    private static MapAssetReference Asset(string name, string color)
    {
        var previewDocument = new MapDocument(
            DocumentId.New(), name, DateTimeOffset.UnixEpoch,
            new CanvasDefinition(16, 16, "px", color),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, color)]);
        var bytes = ThumbnailRenderer.RenderPng(previewDocument, 16, 16);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new MapAssetReference(
            hash, name, "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test", "owner", bytes);
    }

    private static SceneObject Object(
        string name,
        double x,
        double y,
        double width,
        double height,
        double rotation = 0,
        bool visible = true,
        bool locked = false,
        string fill = "#FFFFFFFF",
        string stroke = "#FF000000") => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name, x, y, width, height, rotation,
        fill, stroke, 0, visible, locked);

    private static IReadOnlyList<SceneObject> Objects(MapDocument document) =>
        [.. document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)];

    private static TileMap SetPixel(TileMap map, int x, int y, params byte[] value)
    {
        Assert.Equal(map.BytesPerPixel, value.Length);
        var key = TileMap.KeyAt(x, y);
        var tile = map.TryGet(key) is { } existing
            ? (byte[])existing.Clone()
            : new byte[map.TileByteLength];
        var localX = x - key.X * TileMap.TileSize;
        var localY = y - key.Y * TileMap.TileSize;
        value.CopyTo(tile, ((localY * TileMap.TileSize) + localX) * map.BytesPerPixel);
        return map.With(key, tile);
    }

    private static byte[] Pixel(TileMap map, int x, int y)
    {
        var key = TileMap.KeyAt(x, y);
        var tile = map.TryGet(key);
        if (tile is null)
        {
            return [];
        }

        var localX = x - key.X * TileMap.TileSize;
        var localY = y - key.Y * TileMap.TileSize;
        var index = ((localY * TileMap.TileSize) + localX) * map.BytesPerPixel;
        var result = new byte[map.BytesPerPixel];
        tile.AsSpan(index, map.BytesPerPixel).CopyTo(result);
        return result.All(value => value == 0) ? [] : result;
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Map-Stage12B-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
