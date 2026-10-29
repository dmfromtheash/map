using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Fonts.Inter;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Assets;
using Map.Persistence;
using Map.App.Services;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

internal sealed record NativeEditorIntegrityReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    string WideScreenshotPath,
    string CompactScreenshotPath,
    IReadOnlyDictionary<string, bool> Scenarios,
    IReadOnlyList<string> Notes,
    bool GatePassed);

/// <summary>
/// The repair smoke for the reported editor defects. Unlike the earlier profiles it drives the
/// real <see cref="MapCanvasControl"/> routing for every canvas gesture, so a press with a drawing
/// tool active is delivered exactly the way a pointer delivers it.
/// </summary>
internal static class NativeEditorIntegritySmoke
{
    public static NativeEditorIntegrityReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The editor integrity smoke targets Windows.");

        var completion = new TaskCompletionSource<NativeEditorIntegrityReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeEditorIntegrityApplication.Configure(options, completion);
                AppBuilder.Configure<NativeEditorIntegrityApplication>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Map editor integrity smoke",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class NativeEditorIntegrityApplication : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeEditorIntegrityReport>? _completion;

    internal static void Configure(
        PerformanceOptions options, TaskCompletionSource<NativeEditorIntegrityReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize()
    {
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null || _completion is null)
            throw new InvalidOperationException("The editor integrity smoke was not configured.");

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-editor-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var window = new Window
        {
            Title = "Map editor integrity smoke",
            Width = 1_416,
            Height = 939,
            CanResize = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
        };

        window.Opened += async (_, _) =>
        {
            EditorView? editorView = null;
            try
            {
                var result = await ExecuteAsync(_options, workspace, window, view => editorView = view);
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                if (editorView is not null)
                {
                    editorView.CloseAuxiliaryWindowsForSmoke();
                    editorView.Canvas.DisposeRenderCaches();
                }

                window.Close();
                desktop.Shutdown();
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeEditorIntegrityReport> ExecuteAsync(
        PerformanceOptions options, string workspace, Window window, Action<EditorView> showView)
    {
        var scenarios = new Dictionary<string, bool>();
        var notes = new List<string>();

        // ------------------------------------------------------------ open a map
        var mapPath = Path.Combine(workspace, "integrity.mapproj");
        var document = Document();
        MapProjectStore.Save(document, mapPath,
            new SaveMetadata("integrity-smoke", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(document, 160, 100));
        var opened = MapProjectStore.Open(mapPath);

        // A synthetic stamp asset of our own making, imported into a throwaway catalog: the ghost
        // has to show the real picture the user picked, not a placeholder rectangle.
        var catalog = new AssetCatalog(Path.Combine(workspace, "asset-catalog"));
        var stampPngPath = Path.Combine(workspace, "synthetic-stamp.png");
        CreateStampPng(stampPngPath);
        var stampRecord = catalog.Import(stampPngPath, new AssetImportOptions(), DateTimeOffset.UnixEpoch);

        var recent = new SmokeRecentMaps();
        recent.Touch(mapPath, document.Id.Value, document.Title, DateTimeOffset.UnixEpoch);
        var editor = new EditorViewModel(
            opened.Document, mapPath, false, new NativeStore(), recent,
            new NoFilePicker(), new SmokePrompts(), new SmokeLog(),
            clock: () => new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            appVersion: "integrity-smoke", assetCatalog: catalog,
            availableFontFamilies: ["Arial"]);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        showView(view);
        window.Content = view;
        await SettleAsync();
        view.Canvas.FitToCanvas();
        await SettleAsync();
        scenarios["open_temporary_map"] = editor.Session.Document.Layers.Count == 5
            && view.Canvas.Bounds.Width > 200 && view.Canvas.Bounds.Height > 200;

        // -------------------------------------------------- every tool activates
        var toolButtons = new (string Button, EditorTool Tool)[]
        {
            ("SelectToolButton", EditorTool.Select),
            ("LineToolButton", EditorTool.Line),
            ("ShapeToolButton", EditorTool.Shape),
            ("TextToolButton", EditorTool.Text),
            ("AssetsLibraryButton", EditorTool.Stamp),
            ("NotesToolButton", EditorTool.Notes),
            ("BrushToolButton", EditorTool.Brush),
            ("MaskToolButton", EditorTool.Mask),
            ("LandToolButton", EditorTool.Land),
        };
        var toolsActivate = true;
        foreach (var (button, tool) in toolButtons)
        {
            Find<Button>(view, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            toolsActivate &= editor.ActiveTool == tool
                && Find<Control>(view, "ContextPanelHost").IsVisible;
        }

        scenarios["every_tool_activates_with_its_panel"] = toolsActivate;

        // ------------------------------- shape created through the real pointer path
        var objectLayer = editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        editor.SelectLayer(objectLayer.Id);
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;
        await SettleAsync();
        var beforeShapes = ObjectCount(editor.Session.Document);
        var (drawFromX, drawFromY) = Screen(editor, 140, 140);
        var (drawToX, drawToY) = Screen(editor, 420, 340);
        view.Canvas.PressMoveReleaseForSmoke(drawFromX, drawFromY, drawToX, drawToY);
        await SettleAsync();
        var shape = editor.Session.SelectedObject;
        scenarios["shape_drawn_by_pointer"] = shape is { Shape: not null }
            && ObjectCount(editor.Session.Document) == beforeShapes + 1
            && editor.Session.UndoDepth == 1;

        // ------------------------- a click selects it as an object, panel follows
        editor.Session.ClearSelection();
        await SettleAsync();
        view.Canvas.ClickForSmoke(Screen(editor, 280, 240).X, Screen(editor, 280, 240).Y);
        await SettleAsync();
        var reselected = editor.Session.SelectedObjectId == shape?.Id;
        var inspectorShown = Find<Control>(view, "SelectionKindSlot").IsVisible
            && VisibleInside(Find<Control>(view, "SelectionKindSlot"), view)
            && editor.ActiveTool == EditorTool.Shape;
        var toolPathStillThere = VisibleInside(Find<Button>(view, "ContextScopeTool"), view);
        scenarios["click_selects_the_shape_as_an_object"] = reselected;
        scenarios["object_inspector_opens_while_shape_tool_is_active"] = inspectorShown;
        scenarios["way_back_to_tool_settings_stays_visible"] = toolPathStillThere;

        // ------------------------------------------- move, resize and rotate it
        editor.ActiveTool = EditorTool.Select;
        await SettleAsync();
        var beforeMove = editor.Session.SelectedObject!;
        var (bodyX, bodyY) = Screen(editor, 280, 240);
        var (bodyToX, bodyToY) = Screen(editor, 330, 290);
        view.Canvas.PressMoveReleaseForSmoke(bodyX, bodyY, bodyToX, bodyToY);
        await SettleAsync();
        var moved = editor.Session.SelectedObject!;
        var movedOk = Math.Abs(moved.X - (beforeMove.X + 50)) < 2
            && editor.Session.UndoDepth == 2;

        var (handleX, handleY) = Screen(editor, moved.X + moved.Width, moved.Y + moved.Height);
        var (handleToX, handleToY) = Screen(editor, moved.X + moved.Width + 60, moved.Y + moved.Height + 40);
        view.Canvas.PressMoveReleaseForSmoke(handleX, handleY, handleToX, handleToY);
        await SettleAsync();
        var resized = editor.Session.SelectedObject!;
        var resizedOk = resized.Width > moved.Width && resized.Height > moved.Height
            && editor.Session.UndoDepth == 3;

        var rotateOffset = EditorViewModel.RotationHandleOffset(editor.Session.Viewport.Scale);
        var (rotX, rotY) = Screen(editor,
            resized.X + (resized.Width / 2), resized.Y - rotateOffset);
        var (rotToX, rotToY) = Screen(editor,
            resized.X + resized.Width, resized.Y + (resized.Height / 2));
        view.Canvas.PressMoveReleaseForSmoke(rotX, rotY, rotToX, rotToY);
        await SettleAsync();
        var rotated = editor.Session.SelectedObject!;
        var rotatedOk = Math.Abs(rotated.RotationDegrees - resized.RotationDegrees) > 1
            && editor.Session.UndoDepth == 4;
        scenarios["selected_shape_moves"] = movedOk;
        scenarios["selected_shape_resizes"] = resizedOk;
        scenarios["selected_shape_rotates"] = rotatedOk;
        notes.Add($"move: {beforeMove.X:0.##} -> {moved.X:0.##} (want {beforeMove.X + 50:0.##}), depth={editor.Session.UndoDepth}");
        notes.Add($"resize: {moved.Width:0.##}x{moved.Height:0.##} -> {resized.Width:0.##}x{resized.Height:0.##}");
        notes.Add($"rotate: {resized.RotationDegrees:0.##} -> {rotated.RotationDegrees:0.##}");

        // ------------------------------------------------------- line and text
        editor.ActiveTool = EditorTool.Line;
        await SettleAsync();
        var beforeLine = ObjectCount(editor.Session.Document);
        var (lineFromX, lineFromY) = Screen(editor, 520, 140);
        var (lineToX, lineToY) = Screen(editor, 760, 320);
        view.Canvas.PressMoveReleaseForSmoke(lineFromX, lineFromY, lineToX, lineToY);
        await SettleAsync();
        var lineOk = ObjectCount(editor.Session.Document) == beforeLine + 1
            && editor.Session.SelectedObject?.Line is not null;

        editor.ActiveTool = EditorTool.Text;
        await SettleAsync();
        var beforeText = ObjectCount(editor.Session.Document);
        var (textFromX, textFromY) = Screen(editor, 200, 520);
        var (textToX, textToY) = Screen(editor, 460, 600);
        view.Canvas.PressMoveReleaseForSmoke(textFromX, textFromY, textToX, textToY);
        await SettleAsync();
        var textOk = ObjectCount(editor.Session.Document) == beforeText + 1
            && editor.Session.SelectedObject?.Text is not null;
        scenarios["line_and_text_placed_by_pointer"] = lineOk && textOk;

        // ----------------------------------------------------- primitive placement
        editor.ActiveTool = EditorTool.Select;
        var beforePrimitive = ObjectCount(editor.Session.Document);
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        await SettleAsync();
        var armedWithoutMutation = editor.HasPendingPlacement
            && ObjectCount(editor.Session.Document) == beforePrimitive;
        var (ghostX, ghostY) = Screen(editor, 640, 520);
        view.Canvas.MovePlacementPointerForSmoke(ghostX, ghostY);
        await SettleAsync();
        var ghostVisible = editor.PlacementPreviewObjects.Count == 1
            && ObjectCount(editor.Session.Document) == beforePrimitive;
        view.Canvas.ClickForSmoke(ghostX, ghostY);
        await SettleAsync();
        var primitiveOk = armedWithoutMutation && ghostVisible
            && ObjectCount(editor.Session.Document) == beforePrimitive + 1
            && !editor.HasPendingPlacement
            && editor.Session.SelectedObject?.Kind == SceneObjectKind.Ellipse
            && editor.PlacementPreviewObjects.Count == 0;

        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        view.Canvas.MovePlacementPointerForSmoke(ghostX, ghostY);
        var cancelled = view.Canvas.CancelPlacementWithSecondaryPointerForSmoke();
        await SettleAsync();
        var cancelOk = cancelled && !editor.HasPendingPlacement
            && ObjectCount(editor.Session.Document) == beforePrimitive + 1;
        scenarios["primitive_placement_ghost_commit_and_cancel"] = primitiveOk && cancelOk;

        // ------------------------ stamp ghost: follows the pointer and turns before landing
        editor.SelectLayer(objectLayer.Id);
        editor.ActiveTool = EditorTool.Stamp;
        var stampSet = catalog.FindSetForContent(stampRecord.Sha256)!;
        editor.SelectAssetSet(stampSet.Id);
        editor.StampPlacementMode = StampPlacementMode.One;
        await SettleAsync();
        var beforeStampDocument = editor.Session.Document;
        var beforeStampCount = ObjectCount(beforeStampDocument);
        var undoBeforeStamp = editor.Session.UndoDepth;
        var stampArmed = editor.HasPendingPlacement
            && editor.PendingPlacement?.Target == PlacementTargetKind.Stamp;

        var (stampFirstX, stampFirstY) = Screen(editor, 300, 620);
        view.Canvas.MovePlacementPointerForSmoke(stampFirstX, stampFirstY);
        await SettleAsync();
        var firstGhost = editor.PlacementPreviewObjects.SingleOrDefault();
        var (stampSecondX, stampSecondY) = Screen(editor, 520, 660);
        view.Canvas.MovePlacementPointerForSmoke(stampSecondX, stampSecondY);
        await SettleAsync();
        var secondGhost = editor.PlacementPreviewObjects.SingleOrDefault();
        var ghostFollowsPointer = firstGhost is not null && secondGhost is not null
            && Math.Abs(secondGhost.X - firstGhost.X) > 100
            && ReferenceEquals(beforeStampDocument, editor.Session.Document);
        var ghostShowsTheChosenAsset = secondGhost?.Stamp?.AssetSha256 == stampRecord.Sha256
            && secondGhost is { ObjectOpacity: > 0 and < 1 };

        var rotationBefore = secondGhost!.RotationDegrees;
        editor.RotatePendingPlacement(1, largerStep: true);
        view.Canvas.MovePlacementPointerForSmoke(stampSecondX, stampSecondY);
        await SettleAsync();
        var turnedGhost = editor.PlacementPreviewObjects.Single();
        var ghostTurnsBeforePlacement =
            Math.Abs(turnedGhost.RotationDegrees - rotationBefore) > 0.5
            && ReferenceEquals(beforeStampDocument, editor.Session.Document)
            && editor.Session.UndoDepth == undoBeforeStamp;

        view.Canvas.ClickForSmoke(stampSecondX, stampSecondY);
        await SettleAsync();
        // Stage 15 keeps Stamp armed and preserves the previous selection in Auto mode.
        // Find the actual added document object instead of mistaking that selection for it.
        var originalStampIds = beforeStampDocument.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).Select(item => item.Id).ToHashSet();
        var placedStamp = editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).Single(item => !originalStampIds.Contains(item.Id));
        var ghostCommitsWhereItStood = placedStamp is { Stamp: not null }
            && ObjectCount(editor.Session.Document) == beforeStampCount + 1
            && Math.Abs(placedStamp.X - turnedGhost.X) < 0.5
            && Math.Abs(placedStamp.Y - turnedGhost.Y) < 0.5
            && Math.Abs(placedStamp.RotationDegrees - turnedGhost.RotationDegrees) < 0.5
            && editor.PendingPlacement is { Target: PlacementTargetKind.Stamp, Mode: StampPlacementMode.One }
            && editor.Session.UndoDepth == undoBeforeStamp + 1;

        scenarios["stamp_ghost_follows_the_pointer_with_the_chosen_asset"] =
            stampArmed && ghostFollowsPointer && ghostShowsTheChosenAsset;
        scenarios["stamp_ghost_turns_before_placement_without_touching_the_document"] =
            ghostTurnsBeforePlacement;
        scenarios["stamp_lands_exactly_where_the_ghost_stood"] = ghostCommitsWhereItStood;
        notes.Add($"stamp ghost x: {firstGhost?.X:0.#} -> {secondGhost.X:0.#}; "
            + $"rotation {rotationBefore:0.#} -> {turnedGhost.RotationDegrees:0.#}; "
            + $"placed at {placedStamp?.X:0.#}/{placedStamp?.Y:0.#}");

        // Series keeps the ghost armed for the next click; Escape ends the whole thing.
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.SelectAssetSet(stampSet.Id);
        var beforeSeries = ObjectCount(editor.Session.Document);
        var (seriesX, seriesY) = Screen(editor, 620, 700);
        view.Canvas.MovePlacementPointerForSmoke(seriesX, seriesY);
        view.Canvas.ClickForSmoke(seriesX, seriesY);
        await SettleAsync();
        var seriesStaysArmed = editor.HasPendingPlacement
            && ObjectCount(editor.Session.Document) == beforeSeries + 1;
        var escapeEndedSeries = editor.HandleEscape();
        await SettleAsync();
        scenarios["series_keeps_the_ghost_and_escape_ends_it"] =
            seriesStaysArmed && escapeEndedSeries && !editor.HasPendingPlacement
            && ObjectCount(editor.Session.Document) == beforeSeries + 1;

        // ------------------------------ escape returns to plain mouse control
        editor.ActiveTool = EditorTool.Shape;
        editor.Session.ClearSelection();
        await SettleAsync();
        var beforeEscapeDocument = editor.Session.Document;
        var beforeEscapeUndo = editor.Session.UndoDepth;
        var (escFromX, escFromY) = Screen(editor, 150, 150);
        var input = view.Canvas.InputForSmoke;
        input.Press(CanvasPointerButton.Left, escFromX, escFromY);
        input.Move(escFromX + 90, escFromY + 70);
        var gestureWasRunning = input.ActiveGesture == CanvasInputGesture.VectorDraw;
        var escapeCancelledGesture = editor.HandleEscape();
        await SettleAsync();
        var gestureGone = input.ActiveGesture == CanvasInputGesture.None;
        input.Release(escFromX + 90, escFromY + 70);
        await SettleAsync();
        var nothingCommitted = ReferenceEquals(beforeEscapeDocument, editor.Session.Document)
            && editor.Session.UndoDepth == beforeEscapeUndo;

        // Further Escapes walk back to plain selection; then the pointer picks objects again.
        editor.HandleEscape();
        editor.HandleEscape();
        editor.HandleEscape();
        await SettleAsync();
        var backToSelect = editor.ActiveTool == EditorTool.Select;

        // The pointer must work normally again: a plain click picks whatever the document says is
        // topmost there. Comparing against the model's own hit-test keeps this honest even though
        // several objects overlap in this scene by now.
        var pickWorldX = placedStamp!.X + (placedStamp.Width / 2);
        var pickWorldY = placedStamp.Y + (placedStamp.Height / 2);
        var expected = SceneGeometry.HitTest(editor.Session.Document, pickWorldX, pickWorldY);
        var (pickX, pickY) = Screen(editor, pickWorldX, pickWorldY);
        view.Canvas.ClickForSmoke(pickX, pickY);
        await SettleAsync();
        var pickedTopmost = expected is not null
            && editor.Session.SelectedObjectId == expected.Id;
        scenarios["escape_cancels_any_action_and_restores_normal_pointer_control"] =
            gestureWasRunning && escapeCancelledGesture && gestureGone && nothingCommitted
            && backToSelect && pickedTopmost;
        notes.Add($"escape: running={gestureWasRunning} cancelled={escapeCancelledGesture} "
            + $"gone={gestureGone} clean={nothingCommitted} backToSelect={backToSelect} "
            + $"picked topmost {expected?.Name}={pickedTopmost}");

        // ----------------------------------------------------------------- brush
        // Deliberately NOT selecting the brush layer first: the object layer stays active, which
        // is where every real session starts. The tool has to find its own surface.
        var brushLayer = editor.Session.Document.Layers.OfType<BrushLayer>().Single();
        editor.SelectLayer(objectLayer.Id);
        editor.ActiveTool = EditorTool.Brush;
        await SettleAsync();
        var (brushFromX, brushFromY) = Screen(editor, 700, 620);
        var (brushToX, brushToY) = Screen(editor, 820, 700);
        view.Canvas.PressMoveReleaseForSmoke(brushFromX, brushFromY, brushToX, brushToY);
        await SettleAsync();
        scenarios["brush_paints_through_the_pointer"] =
            ((BrushLayer)editor.Session.Document.FindLayer(brushLayer.Id)!).Tiles.TileCount > 0
            && editor.RasterToolReady
            && editor.ActiveLayerId == brushLayer.Id;

        // ------------------------------------------------------- brush tip chooser
        // The tip used to be seven identical buttons with no selected state; the list has to show
        // which tip is live, switch it, and fit the panel it sits in.
        // The tips live on the brush panel's «Отпечаток» tab: open it the way a user would.
        NativeEditorScenarioApplication.RevealTab(view, "BrushTipList");
        await SettleAsync();
        var tipList = Find<ListBox>(view, "BrushTipList");
        await StabiliseLayoutAsync(view);
        var tipRows = tipList.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        var tipPanel = Find<Control>(view, "ContextPanelHost");
        var tipListFits = tipRows.Count > 0 && tipRows.All(row => HorizontallyInside(row, tipPanel));
        var tipShowsActive = tipList.SelectedItem is ListBoxItem { Tag: BrushPresetKind selected }
            && selected == editor.ActiveBrushPreset;

        var edgeBlockRow = tipRows.FirstOrDefault(
            row => row.Tag is BrushPresetKind kind && kind == BrushPresetKind.EdgeBlock);
        tipList.SelectedItem = edgeBlockRow;
        await SettleAsync();
        var tipSwitched = editor.ActiveBrushPreset == BrushPresetKind.EdgeBlock
            && editor.BrushSettings.Tip is not null;

        scenarios["brush_tip_list_shows_and_switches_the_active_tip"] =
            tipList.ItemCount == 7 && tipRows.Count == 7
            && tipListFits && tipShowsActive && tipSwitched;
        notes.Add($"tips: items={tipList.ItemCount} realized={tipRows.Count} fits={tipListFits} "
            + $"showsActive={tipShowsActive} switched={tipSwitched}");

        editor.ApplyBrushPreset(BrushPresetKind.RoundSoft);
        await SettleAsync();

        // A picture of the panel next to the report, so the chooser can be judged by eye.
        SaveVisual(
            tipPanel,
            Path.Combine(Path.GetDirectoryName(options.OutputPath) ?? ".", "brush-panel.png"),
            (int)Math.Round(tipPanel.Bounds.Width),
            (int)Math.Round(tipPanel.Bounds.Height));

        // ------------------------------------------------------------------ mask
        editor.ToggleActiveMask();
        var maskAdded = editor.Session.Document.FindLayer(brushLayer.Id)?.Mask is not null;
        editor.ActiveTool = EditorTool.Mask;
        editor.MaskMode = MaskPaintMode.Hide;
        await SettleAsync();
        var beforeMaskTiles = MaskTiles(editor, brushLayer.Id);
        view.Canvas.PressMoveReleaseForSmoke(brushFromX, brushFromY, brushToX, brushToY);
        await SettleAsync();
        var maskHid = MaskTiles(editor, brushLayer.Id) > beforeMaskTiles;
        var maskUndone = editor.Session.Undo();
        await SettleAsync();
        scenarios["mask_hides_an_area_and_gives_it_back"] =
            maskAdded && maskHid && maskUndone
            && MaskTiles(editor, brushLayer.Id) == beforeMaskTiles;

        // ------------------------------------------------------------------ land
        var landLayer = editor.Session.Document.Layers.OfType<LandLayer>().Single();
        editor.SelectLayer(objectLayer.Id);
        editor.ActiveTool = EditorTool.Land;
        await SettleAsync();
        var otherLandCoverage = editor.Session.Document.Layers.OfType<LandLayer>()
            .Where(layer => layer.Id != landLayer.Id).Sum(layer => layer.GeometryTiles.TileCount);
        var (landFromX, landFromY) = Screen(editor, 120, 640);
        var (landToX, landToY) = Screen(editor, 300, 740);
        view.Canvas.PressMoveReleaseForSmoke(landFromX, landFromY, landToX, landToY);
        await SettleAsync();
        var landAfter = (LandLayer)editor.Session.Document.FindLayer(landLayer.Id)!;
        scenarios["land_changes_only_its_own_layer"] = landAfter.GeometryTiles.TileCount > 0
            && editor.ActiveLayerId == landLayer.Id
            && editor.Session.Document.Layers.OfType<LandLayer>()
                .Where(layer => layer.Id != landLayer.Id).Sum(layer => layer.GeometryTiles.TileCount)
                == otherLandCoverage;

        // ------------------------------------------------------------ land shapes
        // Clicking a shape in the Land panel has to change what the tool then makes. The six icons
        // used to write a local variable nothing else read, so the choice had no effect anywhere.
        editor.SelectLayer(landLayer.Id);
        editor.ActiveTool = EditorTool.Land;
        await SettleAsync();

        Find<Button>(view, "LandRectangleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var shapeChoiceVisible =
            Find<Button>(view, "LandRectangleButton").Classes.Contains("active");

        var coverageBeforeShape = ((LandLayer)editor.Session.Document.FindLayer(landLayer.Id)!)
            .GeometryTiles.TileCount;
        var undoBeforeShape = editor.Session.UndoDepth;
        var (shapeFromX, shapeFromY) = Screen(editor, 640, 120);
        var (shapeToX, shapeToY) = Screen(editor, 900, 300);
        view.Canvas.PressForSmoke(shapeFromX, shapeFromY);
        view.Canvas.MoveForSmoke(shapeToX, shapeToY);
        await SettleAsync();
        var shapePreviewShown = editor.LandShapePreview is not null;
        view.Canvas.ReleaseForSmoke(shapeToX, shapeToY);
        await SettleAsync();

        var landAfterShape = (LandLayer)editor.Session.Document.FindLayer(landLayer.Id)!;
        var shapeDrawn = landAfterShape.GeometryTiles.TileCount > coverageBeforeShape
            && editor.Session.UndoDepth == undoBeforeShape + 1;

        // The round tip keeps its freehand behaviour; it is not a region gesture.
        Find<Button>(view, "LandCircleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var backToFreehand = editor.ActiveLandShape == LandShapeKind.Circle;

        scenarios["land_shape_choice_changes_what_the_drag_makes"] =
            shapeChoiceVisible && shapePreviewShown && shapeDrawn && backToFreehand;
        notes.Add($"land shapes: selected={shapeChoiceVisible} preview={shapePreviewShown} "
            + $"drawn={shapeDrawn} freehand={backToFreehand}");

        // ----------------------------------------------------------------- notes
        editor.ActiveTool = EditorTool.Notes;
        await SettleAsync();
        var (noteX, noteY) = Screen(editor, 880, 200);
        view.Canvas.ClickForSmoke(noteX, noteY);
        await SettleAsync();
        var note = editor.Notes.LastOrDefault();
        var noteCreated = note is not null
            && Math.Abs(note.WorldX - 880) < 2 && Math.Abs(note.WorldY - 200) < 2;
        if (note is not null)
        {
            editor.EditNote(note.Id, "Проверка заметки", "## Заголовок\n\nТекст заметки.");
        }

        scenarios["note_is_created_where_the_user_pointed_and_edited"] = noteCreated
            && editor.Notes.Any(item => item.Title == "Проверка заметки");

        // --------------------------------------------------------------- palette
        editor.ActiveTool = EditorTool.Select;
        editor.SelectObject(shape!.Id);
        await SettleAsync();
        var fill = view.ColorFieldForSmoke("ShapeFillColorBox");
        var popup = fill.OpenForSmoke();
        popup.Measure(new Size(320, 900));
        var popupFits = popup.DesiredSize.Width <= 300 && popup.DesiredSize.Height > 200;

        var undoBeforeColour = editor.Session.UndoDepth;
        fill.PickForSmoke("#FF884422", commit: false);
        fill.PickForSmoke("#FF224488", commit: false);
        var previewWroteNoHistory = editor.Session.UndoDepth == undoBeforeColour
            && editor.DragPreview is not null;
        fill.CancelForSmoke();
        var cancelRestored = editor.Session.UndoDepth == undoBeforeColour
            && editor.Session.SelectedObject!.Shape!.Fill.Color == shape.Shape!.Fill.Color;

        fill.OpenForSmoke();
        fill.PickForSmoke("#FF224488", commit: true);
        await SettleAsync();
        var fillCommitted = editor.Session.SelectedObject!.Shape!.Fill.Color == "#FF224488"
            && editor.Session.UndoDepth == undoBeforeColour + 1
            && fill.DisplayedValue.Contains("224488", StringComparison.OrdinalIgnoreCase);

        var stroke = view.ColorFieldForSmoke("ShapeStrokeColorBox");
        stroke.OpenForSmoke();
        stroke.PickForSmoke("#FF10203F", commit: true);
        await SettleAsync();
        var strokeCommitted = editor.Session.SelectedObject!.Shape!.Stroke.Color == "#FF10203F";

        editor.SelectLayer(editor.Session.Document.BaseColor.Id);
        await SettleAsync();
        var background = view.ColorFieldForSmoke("BaseColorBox");
        background.OpenForSmoke();
        background.PickForSmoke("#FF203040", commit: true);
        await SettleAsync();
        var backgroundCommitted = editor.Session.Document.BaseColor.Color == "#FF203040";

        scenarios["palette_opens_and_changes_three_colour_properties"] =
            popupFits && previewWroteNoHistory && cancelRestored
            && fillCommitted && strokeCommitted && backgroundCommitted;

        // ------------------------------------------------------------ pen path
        // The Pen modes used to ignore every click but the first and the last, so this drives the
        // real control routing: four separate presses have to become four separate nodes.
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Pen;
        await SettleAsync();
        var penUndoBefore = editor.Session.UndoDepth;
        foreach (var (penWorldX, penWorldY) in new[] { (620d, 420d), (760d, 440d), (740d, 560d), (600d, 540d) })
        {
            var (penScreenX, penScreenY) = Screen(editor, penWorldX, penWorldY);
            view.Canvas.ClickForSmoke(penScreenX, penScreenY);
            await SettleAsync();
        }

        var penNodesPlaced = editor.PenNodeCount == 4
            && editor.PenPathActive
            && editor.Session.UndoDepth == penUndoBefore;
        var penFinished = editor.FinishPenPath();
        await SettleAsync();
        var penShape = editor.Session.SelectedObject;
        scenarios["pen_mode_draws_the_path_the_pointer_clicked"] =
            penNodesPlaced && penFinished
            && editor.Session.UndoDepth == penUndoBefore + 1
            && penShape?.Shape?.Regions[0].Nodes.Count == 4;

        // ----------------------------------------------------------- gradient fill
        // Switching tool deliberately points the panel back at the tool, so the smoke re-selects
        // the object the way a user's click would before reading its inspector.
        editor.ActiveTool = EditorTool.Select;
        editor.Session.ClearSelection();
        await SettleAsync();
        editor.SelectObject(penShape!.Id);
        await SettleAsync();
        editor.SetGradientMode(GradientKind.Linear);
        await SettleAsync();
        var gradientCreated = editor.SelectedGradient is { Stops.Count: 2 };

        // The block is collapsed until the user opens it, and a collapsed Expander has no realized
        // content, so the smoke opens it the way a pointer would before measuring what is inside.
        Find<Expander>(view, "ShapeGradient").IsExpanded = true;
        await SettleAsync();

        // The laid-out size is what the user can actually aim at, so the gate reads the arranged
        // bounds rather than a desired size measured outside the panel.
        var ramp = Find<GradientRamp>(view, "ShapeGradientRamp");
        var rampIsUsable = ramp.Bounds.Width > 100 && ramp.Bounds.Height >= 30
            && ramp.Stops.Count == 2;

        var stopColour = view.ColorFieldForSmoke("ShapeGradientStopColorBox");
        stopColour.OpenForSmoke();
        stopColour.PickForSmoke("#FFCC3300", commit: true);
        await SettleAsync();
        var stopRecoloured = editor.SelectedGradient?.Stops[0].Color == "#FFCC3300";

        editor.AddGradientStop();
        editor.SetGradientAngle(30);
        await SettleAsync();
        var gradientEdited = editor.SelectedGradient is { Stops.Count: 3, AngleDegrees: 30 };

        scenarios["gradient_is_created_and_edited_from_the_panel"] =
            gradientCreated && rampIsUsable && stopRecoloured && gradientEdited;

        // -------------------------------------------- layers, masks and objects
        editor.SelectLayer(objectLayer.Id);
        var hidden = editor.Session.Document.Layers.OfType<ObjectLayer>().First().Objects[0];
        editor.SelectObject(hidden.Id);
        editor.ToggleSelectedObjectVisibility();
        await SettleAsync();
        editor.Session.ClearSelection();
        editor.ActiveTool = EditorTool.Select;
        var hiddenWorld = (hidden.X + (hidden.Width / 2), hidden.Y + (hidden.Height / 2));
        var (hiddenX, hiddenY) = Screen(editor, hiddenWorld.Item1, hiddenWorld.Item2);
        view.Canvas.ClickForSmoke(hiddenX, hiddenY);
        await SettleAsync();
        var hiddenNotSelectable = editor.Session.SelectedObjectId != hidden.Id;
        editor.SelectObject(hidden.Id);
        editor.ToggleSelectedObjectVisibility();
        await SettleAsync();
        scenarios["layers_masks_and_objects_work_together"] = hiddenNotSelectable
            && editor.Session.Document.Layers.OfType<BrushLayer>().Single().Mask is not null
            && landAfter.GeometryTiles.TileCount > 0;

        // ------------------------------------------------------------ undo/redo
        var beforeUndoCount = ObjectCount(editor.Session.Document);
        var undoSteps = 0;
        while (editor.Session.UndoDepth > 0 && undoSteps < 64 && editor.Session.Undo()) undoSteps++;
        var emptiedBack = ObjectCount(editor.Session.Document);
        var redoSteps = 0;
        while (editor.Session.Redo() && redoSteps < 64) redoSteps++;
        await SettleAsync();
        scenarios["undo_and_redo_return_the_expected_state"] =
            undoSteps > 0 && redoSteps == undoSteps
            && emptiedBack < beforeUndoCount
            && ObjectCount(editor.Session.Document) == beforeUndoCount;

        // ---------------------------------------------------------- save/reopen
        var savedOk = await editor.SaveAsync();
        var reopened = MapProjectStore.Open(mapPath).Document;
        scenarios["save_and_reopen_keep_the_result"] = savedOk
            && ObjectCount(reopened) == ObjectCount(editor.Session.Document)
            && reopened.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
                .Any(item => item.Shape?.Fill.Gradient is { Stops.Count: 3 })
            && reopened.BaseColor.Color == "#FF203040"
            && reopened.Notes.Any(item => item.Title == "Проверка заметки")
            && reopened.Layers.OfType<ObjectLayer>()
                .SelectMany(layer => layer.Objects)
                .Any(item => item.Shape?.Fill.Color == "#FF224488");

        // ---------------------------------------------- adaptive tool panel modes
        var shapeModeNames = new[]
        {
            "ShapeDrawModeFreeDraw", "ShapeDrawModePen", "ShapeDrawModeBrush",
            "ShapeDrawModeRectangle", "ShapeDrawModeEllipse", "ShapeDrawModePolygon",
        };
        editor.ActiveTool = EditorTool.Shape;
        editor.Session.ClearSelection();
        await SettleAsync();

        var wideScreenshot = Path.ChangeExtension(options.OutputPath, ".wide.png");
        var compactScreenshot = Path.ChangeExtension(options.OutputPath, ".compact.png");
        window.Width = 1_416; window.Height = 939;
        await SettleAsync();
        var wideSettled = await StabiliseLayoutAsync(view);
        var wideModes = AllModesReachable(view, shapeModeNames);
        var wideLayers = LayerRowsReachable(view, out var wideRowDetail);
        var wideCanvas = view.Canvas.Bounds.Width;
        SaveVisual(view, wideScreenshot, 1_416, 939);
        window.Width = 960; window.Height = 640;
        var compactSettled = await StabiliseLayoutAsync(view);
        var compactModes = AllModesReachable(view, shapeModeNames);
        var compactLayers = LayerRowsReachable(view, out var compactRowDetail);
        var compactCanvas = view.Canvas.Bounds.Width;
        SaveVisual(view, compactScreenshot, 960, 640);
        scenarios["shape_modes_stay_reachable_at_both_sizes"] = wideModes && compactModes;
        scenarios["layer_rows_stay_reachable_at_both_sizes"] = wideLayers && compactLayers;
        notes.Add($"canvas width: wide={wideCanvas:0.#}, compact={compactCanvas:0.#}");
        notes.Add($"layout settled: wide={wideSettled}, compact={compactSettled}");
        notes.Add($"layer rows wide: {wideRowDetail}");
        notes.Add($"layer rows compact: {compactRowDetail}");

        // --------------------------------------------------- ghost dashboard card
        var ghostPath = Path.Combine(workspace, "ghost.mapproj");
        var ghostDocument = Document();
        MapProjectStore.Save(ghostDocument, ghostPath,
            new SaveMetadata("integrity-smoke", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(ghostDocument, 160, 100));
        var dashboardRecent = new SmokeRecentMaps();
        dashboardRecent.Touch(mapPath, document.Id.Value, "Существующая", DateTimeOffset.UnixEpoch);
        dashboardRecent.Touch(ghostPath, ghostDocument.Id.Value, "Призрак", DateTimeOffset.UnixEpoch);
        File.Delete(ghostPath);

        var dashboard = new DashboardViewModel(
            new NativeStore(), dashboardRecent, new SmokeLog(), new NoFilePicker(),
            new AcceptingPrompts(), new WindowsProjectTrash(),
            clock: () => new DateTimeOffset(2026, 9, 2, 12, 30, 0, TimeSpan.Zero));
        dashboard.Refresh();
        var ghostCard = dashboard.RecentCards.Single(card => card.Path == ghostPath);
        var ghostRemoved = await dashboard.DeleteAsync(ghostCard);
        dashboard.Refresh();
        scenarios["ghost_dashboard_card_is_removed_and_the_real_file_survives"] =
            ghostRemoved && !ghostCard.IsAvailable
            && dashboard.RecentCards.All(card => card.Path != ghostPath)
            && dashboard.LastError.Length == 0
            && dashboard.LastNotice.Contains("Удалена только запись", StringComparison.Ordinal)
            && File.Exists(mapPath);

        // ------------------------------------------------------------- teardown
        var caches = view.Canvas.ObjectCacheCount;
        view.Canvas.DisposeRenderCaches();
        var disposed = view.Canvas.ObjectCacheCount == 0 && view.Canvas.TileCacheCount == 0;
        notes.Add($"object cache before dispose: {caches}");
        notes.Add("Every map, profile, asset and log path lives inside one unique temporary workspace.");
        notes.Add("All canvas gestures went through MapCanvasControl's production routing, not the view model.");
        scenarios["resources_released"] = disposed;

        return new NativeEditorIntegrityReport(
            1,
            new DateTimeOffset(2026, 9, 2, 12, 45, 0, TimeSpan.Zero),
            "native off-screen own-process window; ShowActivated=false; ShowInTaskbar=false; no system input",
            wideScreenshot,
            compactScreenshot,
            scenarios,
            notes,
            scenarios.Values.All(value => value));
    }

    /// <summary>
    /// Every layer row must be visible and horizontally complete inside its panel. Vertical
    /// overflow is fine - that is what the panel's scroll is for - but a row cut off at the right
    /// edge is the same defect as a tool mode that runs past the panel.
    /// </summary>
    private static bool LayerRowsReachable(EditorView view, out string detail)
    {
        var panel = Find<Control>(view, "EditorSidePanel");
        var rows = view.GetVisualDescendants().OfType<Button>()
            .Where(item => item.Name?.StartsWith("Layer_", StringComparison.Ordinal) == true)
            .ToList();
        var bad = rows.FirstOrDefault(row => !HorizontallyInside(row, panel));
        var offending = bad?.TranslatePoint(default, panel);
        detail = $"{rows.Count} rows, panel {panel.Bounds.Width:0.#}x{panel.Bounds.Height:0.#}"
            + (bad is null
                ? ", all inside"
                : $", BAD {bad.Name} visible={bad.IsVisible} at "
                    + $"{(offending?.X ?? double.NaN):0.#}/{(offending?.Y ?? double.NaN):0.#} "
                    + $"size {bad.Bounds.Width:0.#}x{bad.Bounds.Height:0.#}");
        return rows.Count > 0 && bad is null;
    }

    private static bool HorizontallyInside(Control control, Control host)
    {
        var origin = control.TranslatePoint(default, host);
        return control.IsVisible && origin is { } point
            && control.Bounds.Width > 4 && control.Bounds.Height > 4
            && point.X >= -0.5 && point.X + control.Bounds.Width <= host.Bounds.Width + 0.5;
    }

    /// <summary>
    /// Waits until the panel layout stops moving. A resize settles over several passes, and a
    /// measurement taken mid-pass reports zero-sized controls - which would make a layout gate
    /// flaky rather than meaningful.
    /// </summary>
    private static async Task<bool> StabiliseLayoutAsync(EditorView view)
    {
        var previous = "";
        // A resize settles over a number of passes that depends on how busy the machine is. Twelve
        // was sometimes not enough, and giving up silently made the gate report a real-looking
        // layout failure - a row measured at 0x0 mid-pass - roughly every other run. The budget is
        // now generous, and running out of it is reported as what it is rather than as a defect.
        for (var round = 0; round < 60; round++)
        {
            await SettleAsync();
            var signature = string.Join('|', view.GetVisualDescendants().OfType<Control>()
                .Where(item => item.Name is not null
                    && (item.Name.StartsWith("Layer_", StringComparison.Ordinal)
                        || item.Name.StartsWith("ShapeDrawMode", StringComparison.Ordinal)
                        || item.Name is "ContextPanel" or "EditorSidePanel"))
                .Select(item => $"{item.Name}:{item.Bounds.Width:0.#}x{item.Bounds.Height:0.#}"));
            var settled = signature.Length > 0 && signature == previous
                && !signature.Contains("0x0", StringComparison.Ordinal);
            if (settled)
            {
                return true;
            }

            previous = signature;
        }

        return false;
    }

    private static bool AllModesReachable(EditorView view, IReadOnlyList<string> names)
    {
        var panel = Find<Control>(view, "ContextPanel");
        foreach (var name in names)
        {
            var button = view.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(item => item.Name == name);
            if (button is null || !FullyInside(button, panel))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A mode the user can actually click: entirely inside the panel that holds it.</summary>
    private static bool FullyInside(Control control, Control host)
    {
        var origin = control.TranslatePoint(default, host);
        return control.IsVisible && origin is { } point
            && control.Bounds.Width > 4 && control.Bounds.Height > 4
            && point.X >= -0.5 && point.Y >= -0.5
            && point.X + control.Bounds.Width <= host.Bounds.Width + 0.5
            && point.Y + control.Bounds.Height <= host.Bounds.Height + 0.5;
    }

    private static bool VisibleInside(Control control, Control root)
    {
        var origin = control.TranslatePoint(default, root);
        return control.IsVisible && origin is { } point
            && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && point.X < root.Bounds.Width && point.Y < root.Bounds.Height
            && point.X + control.Bounds.Width > 0 && point.Y + control.Bounds.Height > 0;
    }

    private static (double X, double Y) Screen(EditorViewModel editor, double worldX, double worldY)
        => editor.Session.Viewport.WorldToScreen(worldX, worldY);

    private static int MaskTiles(EditorViewModel editor, LayerId layerId) =>
        editor.Session.Document.FindLayer(layerId)?.Mask?.Tiles.TileCount ?? 0;

    private static int ObjectCount(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count);

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(item => item.Name == name)
        ?? throw new InvalidOperationException($"The editor integrity smoke did not find {name}.");

    private static void SaveVisual(Control control, string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(control);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(20);
    }

    /// <summary>A tiny synthetic stamp of our own making; nothing is copied from anywhere.</summary>
    private static void CreateStampPng(string path)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(48, 36))!;
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        using var paint = new SkiaSharp.SKPaint
        {
            Color = new SkiaSharp.SKColor(0x8C, 0x62, 0x39),
            IsAntialias = true,
        };
        surface.Canvas.DrawRect(SkiaSharp.SKRect.Create(6, 10, 36, 20), paint);
        paint.Color = new SkiaSharp.SKColor(0x3E, 0x8E, 0x5A);
        surface.Canvas.DrawCircle(24, 12, 9, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static MapDocument Document()
    {
        var existing = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "Существующий прямоугольник",
            700, 480, 160, 120, 0, "#FF8C6239", "#FF3D566E", 2, true, false);
        return new MapDocument(
            DocumentId.New(), "Проверка целостности", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1_024, 768, "px", "#FFF3F0E8"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF3F0E8"),
                BrushLayer.CreateEmpty(LayerId.New(), "Кисть"),
                LandLayer.CreateEmpty(LayerId.New(), "Суша"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [existing]),
                new FilterLayer(LayerId.New(), "Фильтр", true, false, 1, new FilterSettings()),
            ]);
    }

    private sealed class NoFilePicker : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    private sealed class SmokePrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) =>
            Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
            Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>Only the delete confirmation is accepted, and only for the synthetic ghost card.</summary>
    private sealed class AcceptingPrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) =>
            Task.FromResult(projectTitle == "Призрак");
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
            Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class SmokeLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class SmokeRecentMaps : IRecentMaps
    {
        private readonly Dictionary<string, RecentMapItem> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RecentMapItem> Load() => _items.Values.ToList();

        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта")
        {
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            _items[path] = new RecentMapItem(
                path, title, modifiedUtc, File.Exists(path), mapType, size, thumbnailPng);
        }

        public void Remove(string path) => _items.Remove(path);
    }

    private sealed class NativeStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);
        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);
        public OpenedProject Open(string path) => MapProjectStore.Open(path);
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            MapProjectStore.Save(document, path, metadata, previewPng);
        public void SaveAutosave(
            MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) { }
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) =>
            throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
