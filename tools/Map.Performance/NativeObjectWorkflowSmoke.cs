using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// The object workflow end to end in a real window: catalog thumbnails, placing without preparing
/// a layer, the object's own level, moving between layers, and Ctrl+C / Ctrl+V over a whole
/// multi-selection with one undo. Every field is measured, not assumed.
/// </summary>
internal sealed record NativeObjectWorkflowReport(
    DateTimeOffset RecordedUtc,
    string WindowMode,
    string EditorScreenshotPath,
    string ObjectsPanelScreenshotPath,
    bool CatalogShowsDistinctAssetThumbnails,
    bool PlacedWithoutPreparingALayer,
    bool PlacementAndItsLayerUndoTogether,
    bool ObjectLevelChangesDrawOrder,
    bool ObjectMovedBetweenLayersByName,
    bool LockedLayerStillRefuses,
    string RailOrder,
    bool RailHasNoSeparateStampButton,
    bool LandFollowsAssetsInTheRail,
    bool ObjectPanelOpensWithLayerAndLevel,
    bool LayerSelectorIsAListOfNames,
    string LevelSliderBand,
    bool LevelSliderOnlyStopsOnWholeNumbers,
    bool OneSliderGestureIsOneUndo,
    bool MixedSelectionRefusesToInventAValue,
    bool ShapeGetsTheSharedBlockWithoutTheBitmapOne,
    bool QuickBarCarriesTheFrequentProperties,
    string QuickBarSingleSelection,
    string QuickBarMixedSelection,
    bool QuickBarSaysDifferentLayers,
    int LayerDropDownRowCount,
    bool LayerDropDownListsEveryLayerByName,
    bool LayerDropDownPicksFirstMiddleAndLast,
    bool ManualLevelEntryIsAccepted,
    bool ManualLevelEntryRejectsOutOfRange,
    bool LevelResetReturnsToZero,
    bool LevelEndsAreVisibleOnTheControl,
    bool NoUserFacingStampWord,
    string OpenMenuScreenshotPath,
    string OpenDropDownScreenshotPath,
    string QuickBarScreenshotPath,
    string CompactWindowScreenshotPath,
    int ReadableTextProbes,
    double WorstTextContrast,
    string WorstTextContrastWhere,
    double WorstTextContrastUnderALeakedDarkVariant,
    string WorstTextContrastUnderALeakedDarkVariantWhere,
    bool EveryVisibleCaptionIsReadable,
    bool ClipboardShortcutsReachedWindowRoute,
    bool UndoRedoShortcutsReachedWindowRoute,
    bool WideAndCompactLayoutsUsable,
    int CopiedObjectCount,
    int PastedObjectCount,
    bool PasteKeptRelativeLayout,
    bool PasteMintedNewIdentities,
    bool PasteSelectedTheWholeGroup,
    bool PasteIsOneUndoStep,
    bool RedoRestoredTheGroup,
    bool SurvivedSaveAndReopen,
    bool ExportMatchesTheCanvasOrder,
    bool ResourcesDisposed,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeObjectWorkflowSmoke
{
    public static NativeObjectWorkflowReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The object workflow native smoke targets Windows.");
        }

        var completion = new TaskCompletionSource<NativeObjectWorkflowReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeObjectWorkflowApplication.Configure(options, completion);
                AppBuilder.Configure<NativeObjectWorkflowApplication>()
                    .UsePlatformDetect()
                    // A dropdown is a separate top-level window by default, so a screenshot of the
                    // editor could never contain one. Drawing popups inside the window is what lets
                    // this smoke show the open layer list rather than assert about it blindly.
                    .With(new Avalonia.Win32PlatformOptions { OverlayPopups = true })
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
            Name = "Map object workflow native smoke",
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

public sealed class NativeObjectWorkflowApplication : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeObjectWorkflowReport>? _completion;

    internal static void Configure(
        PerformanceOptions options, TaskCompletionSource<NativeObjectWorkflowReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize()
    {
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources)
        {
            Resources[resource.Key] = resource.Value;
        }

        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null || _completion is null)
        {
            throw new InvalidOperationException("The object workflow smoke was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-object-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var catalog = CreateCatalog(workspace);
        var savePath = Path.Combine(workspace, "object-workflow.mapproj");
        var preferences = new EditorPreferencesStore(
            Path.Combine(workspace, "profile", "editor-preferences.json"));

        // The map starts with NO object layer at all - exactly the state the owner starts from.
        var editor = new EditorViewModel(
            EmptyDocument(), null, false, new NativeStore(), new SmokeRecentMaps(),
            new SmokeFilePicker(savePath), new SmokePrompts(), new SmokeLog(),
            clock: () => new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero),
            assetCatalog: catalog, availableFontFamilies: ["Arial"],
            localPreferences: preferences.Load(), saveLocalPreferences: preferences.Save);
        // Host the editor in the production MainWindow so routed keyboard events pass through the
        // same shortcut resolver and command switch as Ctrl+C/Ctrl+V in the shipped application.
        var window = new global::Map.App.MainWindow(
            new NativeStore(), new SmokeRecentMaps(), new SmokeLog(), catalog,
            shortcutStore: new Map.Platform.Windows.ShortcutSettingsStore(
                Path.Combine(workspace, "profile", "shortcuts.json")),
            shortcuts: editor.Shortcuts)
        {
            Title = "Map object workflow native smoke",
            Width = 1_416,
            Height = 939,
            CanResize = false,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
        };
        var showEditor = typeof(global::Map.App.MainWindow).GetMethod(
            "ShowEditor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("MainWindow.ShowEditor");
        showEditor.Invoke(window, [editor]);
        var editorViewField = typeof(global::Map.App.MainWindow).GetField(
            "_editorView", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("MainWindow._editorView");
        var view = editorViewField.GetValue(window) as EditorView
            ?? throw new InvalidOperationException("Production MainWindow did not mount its editor view.");

        window.Opened += async (_, _) =>
        {
            try
            {
                _completion.TrySetResult(
                    await ExecuteAsync(_options, workspace, savePath, catalog, editor, view, window));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                view.CloseAuxiliaryWindowsForSmoke();
                view.Canvas.DisposeRenderCaches();
                window.Close();
                desktop.Shutdown();
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeObjectWorkflowReport> ExecuteAsync(
        PerformanceOptions options,
        string workspace,
        string savePath,
        AssetCatalog catalog,
        EditorViewModel editor,
        EditorView view,
        Window window)
    {
        var limitations = new List<string>();
        editor.Session.Viewport.Resize(1_000, 700);
        editor.RefreshLibrary();
        await SettleAsync();

        // ---- 1. the catalog shows a real, distinct thumbnail per asset -----------------
        var sets = editor.LibrarySets.ToList();
        var thumbnails = sets
            .Select(editor.LibraryThumbnailPath)
            .Where(path => path is not null && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var catalogShowsDistinctThumbnails = sets.Count >= 2 && thumbnails.Count == sets.Count;

        // ---- 2. place a tree without preparing a layer first ---------------------------
        var hadNoObjectLayer = !editor.Session.Document.Layers.OfType<ObjectLayer>().Any();
        // Pick by shape, not by title: a singleton import may take the file's own display name.
        var treeSet = sets.OrderByDescending(item => item.VariantCount).First();
        editor.SelectAssetSet(treeSet.SetId);
        var undoBeforePlacement = editor.Session.UndoDepth;
        var placed = editor.PlaceCurrentVariantAt(300, 300);
        await SettleAsync();
        var placedWithoutPreparingALayer = hadNoObjectLayer && placed
            && editor.Session.Document.Layers.OfType<ObjectLayer>().Count() == 1
            && ObjectsOfFirstLayer(editor).Count == 1;

        // The layer the placement had to create belongs to the same single undo step.
        editor.Undo();
        await SettleAsync();
        var placementAndLayerUndoTogether =
            !editor.Session.Document.Layers.OfType<ObjectLayer>().Any()
            && editor.Session.UndoDepth == undoBeforePlacement;
        editor.Redo();
        await SettleAsync();

        // ---- 3. a placed object shows its own asset, not a shared tool glyph -----------
        var firstTree = ObjectsOfFirstLayer(editor).Single();
        var treeThumbnail = editor.ObjectThumbnailPath(firstTree);
        if (treeThumbnail is null || !File.Exists(treeThumbnail))
        {
            catalogShowsDistinctThumbnails = false;
            limitations.Add("Миниатюра размещённого объекта не разрешилась в файл каталога.");
        }

        // ---- 4. a few more objects, then the level actually changes draw order ---------
        var mountainSet = sets.First(item => item.SetId != treeSet.SetId);
        editor.SelectAssetSet(mountainSet.SetId);
        editor.PlaceCurrentVariantAt(420, 320);
        editor.PlaceCurrentVariantAt(540, 340);
        await SettleAsync();

        var layer = FirstObjectLayer(editor);
        var lowest = layer.Objects[0];
        editor.SelectObject(lowest.Id, additive: false);
        var orderBefore = DrawOrder(editor);
        editor.SetSelectedSublayer(5);
        await SettleAsync();
        var orderAfter = DrawOrder(editor);
        var objectLevelChangesDrawOrder =
            editor.Session.SelectedObject!.Sublayer == 5
            && !orderBefore.SequenceEqual(orderAfter)
            && orderAfter[^1] == lowest.Id;

        // ---- 5. move that object to another layer, by the layer's own name ------------
        editor.AddObjectLayer();
        await SettleAsync();
        editor.SelectObject(lowest.Id, additive: false);
        var sourceLayerName = editor.SelectedOwnerLayerName;
        var destination = editor.ObjectLayerTargets.Single(target => !target.IsCurrent);
        var moved = editor.MoveSelectionToLayer(destination.Id);
        await SettleAsync();
        var objectMovedBetweenLayersByName = moved
            && editor.SelectedOwnerLayerName == destination.Name
            && destination.Name != sourceLayerName
            && !destination.Name.Contains('-', StringComparison.Ordinal)
            && editor.Session.SelectedObject!.Sublayer == 5;

        // Put it back so the copy test works on one layer.
        editor.Undo();
        await SettleAsync();

        // ---- 6. a deliberately locked layer must still refuse -------------------------
        editor.SelectLayer(FirstObjectLayer(editor).Id);
        var secondLayerId = editor.Session.Document.Layers.OfType<ObjectLayer>().Last().Id;
        editor.SelectLayer(secondLayerId);
        editor.ToggleActiveLayerLock();
        editor.SelectLayer(FirstObjectLayer(editor).Id);
        editor.ToggleActiveLayerLock();
        var objectsBeforeRefusal = AllObjects(editor).Count;
        editor.PlaceCurrentVariantAt(700, 500);
        await SettleAsync();
        var lockedLayerStillRefuses = AllObjects(editor).Count == objectsBeforeRefusal;
        editor.ToggleActiveLayerLock();
        editor.SelectLayer(secondLayerId);
        editor.ToggleActiveLayerLock();
        editor.SelectLayer(FirstObjectLayer(editor).Id);
        await SettleAsync();

        // ---- 7. select three objects, Ctrl+C, Ctrl+V ---------------------------------
        var beforeCopy = ObjectsOfFirstLayer(editor).ToList();
        if (beforeCopy.Count < 3)
        {
            throw new InvalidOperationException(
                $"The smoke needs at least three objects on one layer, found {beforeCopy.Count}.");
        }

        editor.SelectObject(beforeCopy[0].Id, additive: false);
        editor.SelectObject(beforeCopy[1].Id, additive: true);
        editor.SelectObject(beforeCopy[2].Id, additive: true);
        await SettleAsync();

        var canvasCountBeforeCopy = AllObjects(editor).Count;
        var copyShortcutHandled = RaiseShortcut(view, Key.C, KeyModifiers.Control);
        await SettleAsync();
        var copiedObjectCount = ObjectClipboard.Count;

        // Copying must not itself put anything on the canvas.
        if (AllObjects(editor).Count != canvasCountBeforeCopy)
        {
            limitations.Add("Ctrl+C изменил документ, чего быть не должно.");
        }

        var undoBeforePaste = editor.Session.UndoDepth;
        var pasteShortcutHandled = RaiseShortcut(view, Key.V, KeyModifiers.Control);
        await SettleAsync();
        var pasted = editor.Session.SelectedObjects.ToList();
        var pastedObjectCount = AllObjects(editor).Count - canvasCountBeforeCopy;

        // The buffer keeps draw order, which is not list order once a sublayer differs, so the
        // sources are paired with their copies by name rather than by position in the list.
        var sourcesByName = beforeCopy.ToDictionary(item => item.Name);
        var pairs = pasted
            .Where(item => sourcesByName.ContainsKey(item.Name))
            .Select(item => (Source: sourcesByName[item.Name], Copy: item))
            .ToList();
        var pasteKeptRelativeLayout = pasted.Count == 3 && pairs.Count == 3
            && pairs.Skip(1).All(pair =>
                NearlyEqual(pair.Copy.X - pairs[0].Copy.X, pair.Source.X - pairs[0].Source.X)
                && NearlyEqual(pair.Copy.Y - pairs[0].Copy.Y, pair.Source.Y - pairs[0].Source.Y))
            && pairs.All(pair =>
                NearlyEqual(pair.Copy.Width, pair.Source.Width)
                && NearlyEqual(pair.Copy.Height, pair.Source.Height)
                && pair.Copy.Sublayer == pair.Source.Sublayer)
            && !NearlyEqual(pairs[0].Copy.X, pairs[0].Source.X);
        var pasteMintedNewIdentities =
            pasted.All(item => beforeCopy.All(source => source.Id != item.Id))
            && pasted.Select(item => item.Id).Distinct().Count() == pasted.Count;
        var pasteSelectedTheWholeGroup = editor.SelectionCount == 3;

        var undoShortcutHandled = RaiseShortcut(view, Key.Z, KeyModifiers.Control);
        await SettleAsync();
        var pasteIsOneUndoStep = AllObjects(editor).Count == canvasCountBeforeCopy
            && editor.Session.UndoDepth == undoBeforePaste;
        var redoShortcutHandled = RaiseShortcut(view, Key.Y, KeyModifiers.Control);
        await SettleAsync();
        var redoRestoredTheGroup = AllObjects(editor).Count == canvasCountBeforeCopy + 3;
        var clipboardShortcutsReachedWindowRoute = copyShortcutHandled && pasteShortcutHandled;
        var undoRedoShortcutsReachedWindowRoute = undoShortcutHandled && redoShortcutHandled;

        // ---- 7b. the surfaces the owner decided about, read off the real visual tree ----
        editor.ActiveTool = EditorTool.Select;
        await SettleAsync();
        await NextFrameAsync(view);

        var rail = view.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(item => item.Classes.Contains("rail"));
        var railButtons = rail is null
            ? new List<string>()
            : rail.GetVisualDescendants().OfType<Button>()
                .Where(item => !string.IsNullOrEmpty(item.Name))
                .Select(item => item.Name!)
                .ToList();
        var railOrder = string.Join(" > ", railButtons);
        var railHasNoSeparateStampButton = railButtons.Count > 0
            && railButtons.All(name => !name.Contains("Stamp", StringComparison.OrdinalIgnoreCase))
            && railButtons.Contains("AssetsLibraryButton");
        var assetsIndex = railButtons.IndexOf("AssetsLibraryButton");
        var landFollowsAssetsInTheRail = assetsIndex >= 0
            && assetsIndex + 1 < railButtons.Count
            && railButtons[assetsIndex + 1] == "LandToolButton";
        if (!railHasNoSeparateStampButton || !landFollowsAssetsInTheRail)
        {
            limitations.Add("Рейка в живом окне: " + railOrder);
        }

        var single = AllObjects(editor).First(item => item.Kind == SceneObjectKind.Stamp);
        editor.SelectObject(single.Id, additive: false);
        await SettleAsync();
        await NextFrameAsync(view);

        // The shared block is collapsed by default since the panels were regrouped; the smoke
        // opens it the way a user would, so its controls join the visual tree before they are read.
        if (Named<Expander>(view, "ObjectPrimarySection") is { IsExpanded: false } collapsed)
        {
            collapsed.IsExpanded = true;
            await SettleAsync();
            await NextFrameAsync(view);
        }

        var primary = Named<Control>(view, "ObjectPrimarySection");
        var primaryNames = primary is null
            ? new List<string>()
            : primary.GetVisualDescendants().OfType<Control>()
                .Where(item => !string.IsNullOrEmpty(item.Name))
                .Select(item => item.Name!)
                .ToList();
        int At(string name) => primaryNames.IndexOf(name);
        var objectPanelOpensWithLayerAndLevel = primary is not null
            && At("ObjectOwnerLayerSelector") >= 0
            && At("ObjectSublayerSlider") >= 0
            && At("ObjectOwnerLayerSelector") < At("ObjectSublayerSlider")
            && At("ObjectSublayerSlider") < At("ObjectOpacitySlider")
            && At("ObjectSublayerSlider") < At("ObjectRotationSlider");

        var selector = Named<Button>(view, "ObjectOwnerLayerSelector");
        var selectorCaption = Named<TextBlock>(view, "ObjectOwnerLayerCaption");
        var targets = editor.ObjectLayerTargets.ToList();
        // The control has to PAINT the layer's name, not merely hold it: an empty-looking field
        // was the defect.
        var selectorPainted = selectorCaption is { IsVisible: true }
            && selectorCaption.Bounds.Width > 4
            && selectorCaption.Text == editor.SelectedOwnerLayerName;
        if (!selectorPainted)
        {
            limitations.Add("Селектор слоя не отрисовал имя: text='"
                + selectorCaption?.Text + "' w=" + (selectorCaption?.Bounds.Width ?? -1).ToString("0.#")
                + " ожидалось '" + editor.SelectedOwnerLayerName + "'");
        }

        var layerSelectorIsAListOfNames = selector is not null && selectorPainted
            && targets.Count >= 2
            && targets.TrueForAll(target => target.Display.Length > 0)
            && targets.TrueForAll(target =>
                !target.Display.Contains(target.Id.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)
                && !target.Display.Contains(target.Id.Value.ToString("D"), StringComparison.OrdinalIgnoreCase))
            && targets.Count(target => target.IsCurrent) == 1;

        var slider = Named<Slider>(view, "ObjectSublayerSlider");
        var levelSliderBand = slider is null
            ? "(отсутствует)"
            : $"[{slider.Minimum:0}..{slider.Maximum:0}] шаг {slider.SmallChange:0} snap={slider.IsSnapToTickEnabled}";
        var levelSliderOnlyStopsOnWholeNumbers = slider is not null
            && Math.Abs(slider.Minimum - EditorViewModel.MinimumSublayer) < 0.0001
            && Math.Abs(slider.Maximum - EditorViewModel.MaximumSublayer) < 0.0001
            && slider.IsSnapToTickEnabled
            && Math.Abs(slider.TickFrequency - 1) < 0.0001;

        // Every stop the owner named, plus a deliberately fractional one in between.
        foreach (var stop in new[] { -10d, -9d, -8d, 0d, 1d, 9d, 10d, 4.37d })
        {
            slider!.Value = stop;
            // Dragging previews; the document takes the value when the gesture ends. Raising the
            // key-up the view listens for is that ending, so this measures the real commit path
            // rather than a number the control happens to be showing.
            slider.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = Key.Right,
                Source = slider,
            });
            await SettleAsync();
            var landed = editor.Session.SelectedObject!.Sublayer;
            if (landed != (int)Math.Round(stop)
                || landed < EditorViewModel.MinimumSublayer
                || landed > EditorViewModel.MaximumSublayer)
            {
                levelSliderOnlyStopsOnWholeNumbers = false;
                limitations.Add($"Ползунок уровня: {stop} дал {landed}.");
            }
        }

        // One gesture: many previews, one entry in history.
        editor.SetSelectionSublayer(0, commit: true);
        await SettleAsync();
        var undoBeforeGesture = editor.Session.UndoDepth;
        for (var value = 1; value <= 6; value++)
        {
            editor.SetSelectionSublayer(value, commit: false);
        }

        editor.SetSelectionSublayer(6, commit: true);
        await SettleAsync();
        var oneSliderGestureIsOneUndo = editor.Session.UndoDepth == undoBeforeGesture + 1
            && editor.Session.SelectedObject!.Sublayer == 6;
        editor.Undo();
        await SettleAsync();
        oneSliderGestureIsOneUndo &= editor.Session.SelectedObject!.Sublayer == 0;

        // A selection whose members disagree must not show one of them as if it were the answer.
        var pair = AllObjects(editor).Where(item => item.Kind == SceneObjectKind.Stamp).Take(2).ToList();
        editor.SelectObject(pair[0].Id, additive: false);
        editor.SetSelectionSublayer(-4);
        editor.SelectObject(pair[1].Id, additive: false);
        editor.SetSelectionSublayer(3);
        editor.SelectObject(pair[0].Id, additive: false);
        editor.SelectObject(pair[1].Id, additive: true);
        await SettleAsync();
        await NextFrameAsync(view);
        if (Named<Expander>(view, "MultiPrimarySection") is { IsExpanded: false } collapsedMulti)
        {
            collapsedMulti.IsExpanded = true;
            await SettleAsync();
            await NextFrameAsync(view);
        }
        var mixedReadout = Named<TextBlock>(view, "MultiLevelValue")?.Text
            ?? Named<TextBlock>(view, "ObjectLevelValue")?.Text;
        var mixedSelectionRefusesToInventAValue = editor.SelectionSublayerIsMixed
            && editor.SelectionSublayer is null
            && string.Equals(mixedReadout, "разные", StringComparison.Ordinal);
        var mixedUndo = editor.Session.UndoDepth;
        editor.ShiftSelectionSublayer(2);
        await SettleAsync();
        mixedSelectionRefusesToInventAValue &= editor.Session.UndoDepth == mixedUndo + 1
            && AllObjects(editor).Single(item => item.Id == pair[0].Id).Sublayer == -2
            && AllObjects(editor).Single(item => item.Id == pair[1].Id).Sublayer == 5;
        editor.Undo();
        await SettleAsync();

        // A shape gets the shared block and none of the bitmap-only one.
        editor.AddObject(SceneObjectKind.Rectangle);
        await SettleAsync();
        await NextFrameAsync(view);
        var shapePrimary = Named<Control>(view, "ObjectPrimarySection");
        var shapeSlot = Named<Map.App.Controls.RetainedSlot>(view, "SelectionKindSlot");
        var mountedForShape = (shapeSlot?.Content as Control)?.Name;
        var shapeGetsTheSharedBlockWithoutTheBitmapOne = shapePrimary is not null
            && Named<Slider>(view, "ObjectSublayerSlider") is not null
            && Named<Button>(view, "ObjectOwnerLayerSelector") is not null
            && mountedForShape != "StampObjectActions"
            && Named<Button>(view, "StampReplaceAssetButton") is not { IsEffectivelyVisible: true };
        editor.Undo();
        await SettleAsync();

        // ---- 7b2. the quick strip, the layer list and the exact level entry --------------
        // The strip hides with an empty selection by design, so it is read with one in place.
        editor.SelectObject(single.Id, additive: false);
        await SettleAsync();
        await NextFrameAsync(view);
        var quickBar = Named<Control>(view, "QuickPropertyBar");
        var quickLayer = Named<Button>(view, "QuickOwnerLayerSelector");
        var quickLayerCaption = Named<TextBlock>(view, "QuickOwnerLayerCaption");
        var quickLevel = Named<TextBox>(view, "QuickLevelBox");
        var quickOpacity = Named<TextBox>(view, "QuickOpacityBox");
        var quickRotation = Named<TextBox>(view, "QuickRotationBox");
        var quickScale = Named<TextBox>(view, "QuickScaleBox");
        var quickBarCarriesTheFrequentProperties = quickBar is not null
            && quickBar.IsVisible
            && quickLayer is not null && quickLevel is not null
            && quickOpacity is not null && quickRotation is not null && quickScale is not null
            // The strip is a band of its own above the canvas, never a floating cover over it.
            && quickBar.Bounds.Height > 0
            && view.Canvas.Bounds.Height > 0;

        editor.SelectObject(single.Id, additive: false);
        editor.SetSelectionSublayer(-3, commit: true);
        await SettleAsync();
        await NextFrameAsync(view);
        var quickBarSingleSelection =
            $"слой='{quickLayerCaption!.Text}' уровень='{quickLevel!.Text}'"
            + $" непрозрачность='{quickOpacity!.Text}' поворот='{quickRotation!.Text}'";

        // Exact entry: type a number, press Enter, the document has to hold exactly that integer.
        quickLevel.Text = "7";
        quickLevel.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = quickLevel,
        });
        await SettleAsync();
        var manualLevelEntryIsAccepted = editor.Session.SelectedObject!.Sublayer == 7
            && quickLevel.Text == "+7";

        quickLevel.Text = "999";
        quickLevel.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = quickLevel,
        });
        await SettleAsync();
        var manualLevelEntryRejectsOutOfRange = editor.Session.SelectedObject!.Sublayer
            == EditorViewModel.MaximumSublayer;

        Named<Button>(view, "QuickLevelResetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var levelResetReturnsToZero = editor.Session.SelectedObject!.Sublayer == 0
            && quickLevel.Text == "0";

        var levelEndsAreVisibleOnTheControl =
            Named<TextBlock>(view, "ObjectLevelMinimumCaption") is { IsVisible: true } low
            && low.Text == EditorViewModel.MinimumSublayer.ToString(CultureInfo.InvariantCulture)
            && Named<TextBlock>(view, "ObjectLevelMaximumCaption") is { IsVisible: true } high
            && high.Text == "+" + EditorViewModel.MaximumSublayer.ToString(CultureInfo.InvariantCulture)
            && Named<Slider>(view, "ObjectSublayerSlider")!.TickPlacement != TickPlacement.None;

        await NextFrameAsync(view);
        var quickShot = Path.ChangeExtension(options.OutputPath, ".quickbar.png");
        Directory.CreateDirectory(Path.GetDirectoryName(quickShot)!);
        Capture(view, quickShot, 1_400, 200);

        // ---- 7b3. twenty layers, and the list actually opened ----------------------------
        while (editor.Session.Document.Layers.OfType<ObjectLayer>().Count() < 20)
        {
            editor.AddObjectLayer();
        }

        // Keep unavailable destinations visible with their reasons. They are deliberately away
        // from the first/middle/last rows used below so all three navigation targets stay valid.
        var createdLayers = editor.Session.Document.Layers.OfType<ObjectLayer>().ToList();
        editor.SelectLayer(createdLayers[2].Id);
        editor.ToggleActiveLayerLock();
        editor.SelectLayer(createdLayers[3].Id);
        editor.ToggleActiveLayerVisibility();

        await SettleAsync();
        editor.SelectObject(single.Id, additive: false);
        await SettleAsync();
        await NextFrameAsync(view);

        (quickLayer!.Flyout as Flyout)?.ShowAt(quickLayer);
        await SettleAsync();
        await NextFrameAsync(view);
        var openList = view.LayerListForSmoke("Quick");
        var layerChoices = openList?.ItemsSource?.OfType<ObjectLayerTarget>().ToList() ?? [];
        // A flyout lays out over several passes, and its rows are virtualised: measuring after a
        // single frame reported "no rows" about half the time. Wait for the list to actually exist.
        var rows = new List<ListBoxItem>();
        for (var attempt = 0; attempt < 40 && rows.Count == 0; attempt++)
        {
            await SettleAsync();
            await NextFrameAsync(view);
            rows = openList is null
                ? new List<ListBoxItem>()
                : openList.GetRealizedContainers().OfType<ListBoxItem>()
                    .Where(item => item.IsVisible && item.Bounds.Height > 0)
                    .ToList();
        }
        var layerDropDownRowCount = rows.Count;
        var layerDropDownListsEveryLayerByName = layerChoices.Count >= 20
            && rows.Count > 0
            && rows.All(row => row.Content is ObjectLayerTarget)
            && layerChoices.TrueForAll(target => target.Name.Length > 0
                && !target.Display.Contains(target.Id.Value.ToString("N"), StringComparison.OrdinalIgnoreCase))
            && layerChoices.Count(target => !target.Available
                && !string.IsNullOrWhiteSpace(target.Unavailable)
                && target.Display.Contains(target.Unavailable, StringComparison.Ordinal)) >= 2;

        var dropShot = Path.ChangeExtension(options.OutputPath, ".layerlist.png");
        Capture(window, dropShot, 1_400, 900);
        (quickLayer.Flyout as Flyout)?.Hide();
        await SettleAsync();

        // First, middle and last must move the whole selected set through the real ListBox
        // SelectionChanged route. ScrollIntoView proves that the middle and last rows are actually
        // reachable in the bounded popup rather than merely present in ItemsSource.
        var layerSelectionIds = AllObjects(editor).Take(3).Select(item => item.Id).ToList();
        editor.Session.SetSelection(layerSelectionIds);
        var orderedLayerIds = editor.Session.Document.Layers.OfType<ObjectLayer>()
            .Select(item => item.Id).ToList();
        editor.MoveSelectionToLayer(orderedLayerIds[1]);
        await SettleAsync();

        var pickIndexes = layerChoices.Count >= 3
            ? new[] { 0, layerChoices.Count / 2, layerChoices.Count - 1 }
            : [];
        var layerDropDownPicksFirstMiddleAndLast = pickIndexes.Length == 3;
        foreach (var pickIndex in pickIndexes)
        {
            (quickLayer.Flyout as Flyout)?.ShowAt(quickLayer);
            await SettleAsync();
            await NextFrameAsync(view);
            openList = view.LayerListForSmoke("Quick");
            var currentChoices = openList?.ItemsSource?.OfType<ObjectLayerTarget>().ToList() ?? [];
            if (openList is null || currentChoices.Count <= pickIndex)
            {
                layerDropDownPicksFirstMiddleAndLast = false;
                break;
            }

            var pick = currentChoices[pickIndex];
            openList.ScrollIntoView(pick);
            var rowWasRealizedAfterScroll = false;
            for (var attempt = 0; attempt < 40 && !rowWasRealizedAfterScroll; attempt++)
            {
                await SettleAsync();
                await NextFrameAsync(view);
                rowWasRealizedAfterScroll = openList.GetRealizedContainers()
                    .OfType<ListBoxItem>()
                    .Any(row => Equals(row.Content, pick) || Equals(row.DataContext, pick));
            }
            var undoBeforeLayerPick = editor.Session.UndoDepth;
            openList.SelectedItem = pick;
            await SettleAsync();
            var destinationLayer = editor.Session.Document.Layers.OfType<ObjectLayer>()
                .Single(item => item.Id == pick.Id);
            layerDropDownPicksFirstMiddleAndLast &= pick.Available
                && rowWasRealizedAfterScroll
                && editor.SelectionCount == layerSelectionIds.Count
                && layerSelectionIds.All(id => destinationLayer.Objects.Any(item => item.Id == id))
                && editor.Session.UndoDepth == undoBeforeLayerPick + 1;
        }

        // ---- 7b4. a selection spread over two layers says so in words --------------------
        var spread = AllObjects(editor).Where(item => item.Kind == SceneObjectKind.Stamp).Take(2).ToList();
        editor.SelectObject(spread[0].Id, additive: false);
        var otherLayer = editor.ObjectLayerTargets.First(target => target.Available && !target.IsCurrent);
        editor.MoveSelectionToLayer(otherLayer.Id);
        editor.SelectObject(spread[0].Id, additive: false);
        editor.SetSelectionSublayer(-6, commit: true);
        editor.SelectObject(spread[1].Id, additive: false);
        editor.SetSelectionSublayer(2, commit: true);
        editor.SelectObject(spread[0].Id, additive: false);
        editor.SelectObject(spread[1].Id, additive: true);
        await SettleAsync();
        await NextFrameAsync(view);
        var quickBarMixedSelection = $"слой='{quickLayerCaption.Text}' уровень='{quickLevel.Text}'";
        var quickBarSaysDifferentLayers =
            string.Equals(quickLayerCaption.Text, "Разные слои", StringComparison.Ordinal)
            && string.Equals(quickLevel.Text, "разные", StringComparison.Ordinal)
            && quickLevel.Classes.Contains("mixed");

        // ---- 7b5. no user-facing "штамп" anywhere in the mounted window -------------------
        var stampWords = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(item => item.Text is { Length: > 0 } t
                && t.Contains("штамп", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Text!)
            .ToList();
        var noUserFacingStampWord = stampWords.Count == 0;
        foreach (var word in stampWords.Take(6))
        {
            limitations.Add("Слово «штамп» в интерфейсе: " + word);
        }

        editor.SelectObject(single.Id, additive: false);
        await SettleAsync();
        await NextFrameAsync(view);

        // ---- 7c. what the eye actually gets, measured on the live window ---------------
        // The owner's screenshot showed #10264A on a #2B2B2B ground - 1.03:1. Neither colour was
        // wrong on its own; they simply met. So the check is not "is the palette right" but "what
        // is every caption in this window actually drawn against right now".
        editor.SelectObject(single.Id, additive: false);
        await SettleAsync();
        await NextFrameAsync(view);

        (int Probes, double Worst, string Where, List<string> Failures) Measure()
        {
            var count = 0;
            var worst = double.MaxValue;
            var where = "(none)";
            var failures = new List<string>();
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!text.IsVisible || string.IsNullOrWhiteSpace(text.Text) || text.Bounds.Width < 1)
                {
                    continue;
                }

                if (Resolve(text.Foreground) is not { } foreground
                    || NearestBackground(text) is not { } background)
                {
                    continue;
                }

                count++;
                var ratio = Contrast(foreground, background);
                // WCAG relaxes to 3:1 only for genuinely large text - 24 px, or 18.66 px bold.
                // A hint, a caption and a switched-off control are ordinary small reading text and
                // are held to 4.5:1 like everything else; a class name is not a licence to be
                // fainter than the eye can follow.
                var bold = text.FontWeight >= FontWeight.Bold;
                var large = text.FontSize >= 24 || (bold && text.FontSize >= 18.66);
                var required = large ? 3.0 : 4.5;
                var note = $"'{Shorten(text.Text)}' [{Chain(text)}] {Hex(foreground)} on {Hex(background)} = {ratio:0.00}:1"
                    + $" (\u043d\u0443\u0436\u043d\u043e {required:0.0}:1)";
                if (ratio < worst)
                {
                    worst = ratio;
                    where = note;
                }

                if (ratio < required)
                {
                    failures.Add(note);
                }
            }

            return (count, count == 0 ? 0 : worst, where, failures);
        }

        // Measured with the file menu open, so the popup surface is part of the sample rather
        // than a blind spot.
        var probeMenu = Named<Button>(view, "FileMenuButton")?.Flyout as MenuFlyout;
        probeMenu?.ShowAt(Named<Button>(view, "FileMenuButton")!);
        await SettleAsync();
        await NextFrameAsync(view);
        var shipping = Measure();
        probeMenu?.Hide();
        await SettleAsync();
        var probes = shipping.Probes;
        var worstContrast = shipping.Worst;
        var worstWhere = shipping.Where;
        foreach (var item in shipping.Failures.Take(12))
        {
            limitations.Add("\u041d\u0438\u0437\u043a\u0438\u0439 \u043a\u043e\u043d\u0442\u0440\u0430\u0441\u0442: " + item);
        }

        // And again with the variant the owner's machine was running when the defect appeared. Map
        // pins its own variant, but a container Fluent still colours itself would show up here.
        var pinned = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync();
        await NextFrameAsync(view);
        var leaked = Measure();
        Application.Current.RequestedThemeVariant = pinned;
        await SettleAsync();
        await NextFrameAsync(view);
        foreach (var item in leaked.Failures.Take(8))
        {
            limitations.Add("\u041f\u0440\u0438 \u0447\u0443\u0436\u043e\u0439 \u0442\u0451\u043c\u043d\u043e\u0439 \u0442\u0435\u043c\u0435: " + item);
        }

        var everyVisibleCaptionIsReadable =
            probes > 0 && shipping.Failures.Count == 0 && leaked.Failures.Count == 0;

        // ---- 7d. the two states a screenshot could never show before ---------------------
        // An open menu and an open dropdown are where a colour mistake used to hide, because a
        // popup is a window of its own. With overlay popups this capture contains them.
        var menuShot = Path.ChangeExtension(options.OutputPath, ".menu.png");
        var fileButton = Named<Button>(view, "FileMenuButton");
        if (fileButton?.Flyout is { } flyout)
        {
            flyout.ShowAt(fileButton);
            await SettleAsync();
            await NextFrameAsync(view);
            Capture(window, menuShot, 1_400, 900);
            flyout.Hide();
            await SettleAsync();
        }
        else
        {
            limitations.Add("Меню «Файл» не найдено — снимок открытого меню не сделан.");
        }

        // ---- 7e. the same window at a narrow width ---------------------------------------
        var compactShot = Path.ChangeExtension(options.OutputPath, ".compact.png");
        window.Width = 960;
        window.Height = 640;
        await SettleAsync();
        await NextFrameAsync(view);
        // Only the captions the strip shows right now: the land group carries captions of its
        // own that are hidden while an object is selected.
        var compactCaptions = quickBar?.GetVisualDescendants().OfType<TextBlock>()
            .Where(item => item.IsEffectivelyVisible
                && item.Text is "Слой" or "Уровень" or "Масштаб" or "Непрозрачность" or "Поворот")
            .ToList() ?? [];
        var horizontalAccess = quickBar?.GetVisualDescendants().OfType<ScrollBar>()
            .Any(item => item.Orientation == Orientation.Horizontal
                && item.IsVisible && item.Maximum > item.Minimum) == true;
        // The strip wraps whole groups onto further lines; a horizontal scrollbar is the other
        // acceptable way to keep every caption reachable. Either counts, hidden captions do not.
        var everyCaptionLaidOut = compactCaptions.Count == 5
            && compactCaptions.All(item => item.IsVisible && item.Bounds.Width >= 24 && item.Bounds.Height > 0);
        var compactLayoutUsable = quickBar is not null
            && quickBar.IsVisible
            && quickBar.Bounds.Height > 0
            && compactCaptions.Select(item => item.Text).Distinct(StringComparer.Ordinal).Count() == 5
            && compactCaptions.All(item => item.Bounds.Width >= 24)
            && (horizontalAccess || everyCaptionLaidOut)
            && view.Canvas.Bounds.Width >= 300
            && view.Canvas.Bounds.Height >= 240;
        Capture(window, compactShot, 960, 640);

        window.Width = 1_416;
        window.Height = 939;
        await SettleAsync();
        await NextFrameAsync(view);
        var wideLayoutUsable = quickBar is { IsVisible: true }
            && quickBar.Bounds.Height > 0
            && view.Canvas.Bounds.Width >= 600
            && view.Canvas.Bounds.Height >= 400;
        var wideAndCompactLayoutsUsable = compactLayoutUsable && wideLayoutUsable;
        if (!wideAndCompactLayoutsUsable)
        {
            limitations.Add(
                "Адаптивность верхней полосы не прошла: compact=" + compactLayoutUsable
                + ", wide=" + wideLayoutUsable + ", horizontalAccess=" + horizontalAccess
                + $", canvas={view.Canvas.Bounds.Width:0}x{view.Canvas.Bounds.Height:0}.");
        }

        // ---- 8. screenshots of the real window ---------------------------------------
        var editorScreenshot = Path.ChangeExtension(options.OutputPath, ".editor.png");
        var panelScreenshot = Path.ChangeExtension(options.OutputPath, ".objects.png");
        Directory.CreateDirectory(Path.GetDirectoryName(editorScreenshot)!);

        // The inspector only appears under the Select tool, and its two shapes differ, so one shot
        // shows a single object and the other the whole pasted group.
        editor.ActiveTool = EditorTool.Select;
        var inspected = AllObjects(editor).Take(3).Select(item => item.Id).ToList();
        editor.SelectObject(inspected[0], additive: false);
        await SettleAsync();
        await NextFrameAsync(view);
        Capture(view, editorScreenshot, 1_416, 939);

        foreach (var id in inspected)
        {
            editor.SelectObject(id, additive: true);
        }

        await SettleAsync();
        await NextFrameAsync(view);
        Capture(view, panelScreenshot, 1_416, 939);

        // The objects tab of the working panel: the stack of rows with the level of each object.
        if (Named<Control>(view, "PanelSectionSwitcher") is not null)
        {
            view.ShowPanelSection(Map.Presentation.ViewModels.PanelSection.Assets);
            await SettleAsync();
            await NextFrameAsync(view);
            Capture(view, Path.ChangeExtension(options.OutputPath, ".objectlist.png"), 1_416, 939);
            view.ShowPanelSection(Map.Presentation.ViewModels.PanelSection.Layers);
            await SettleAsync();
        }

        // ---- 9. save, reopen, and compare the render order ----------------------------
        var expectedOrder = AllObjects(editor)
            .Select(item => (item.Name, item.Sublayer))
            .ToList();
        var expectedDrawOrder = DrawOrderNames(editor);
        var saved = await editor.SaveAsync();
        var reopened = MapProjectStore.Open(savePath).Document;
        var reopenedOrder = reopened.Layers.OfType<ObjectLayer>()
            .SelectMany(item => item.Objects)
            .Select(item => (item.Name, item.Sublayer))
            .ToList();
        var survivedSaveAndReopen = saved
            && expectedOrder.SequenceEqual(reopenedOrder)
            && reopened.Layers.OfType<ObjectLayer>().Count()
                == editor.Session.Document.Layers.OfType<ObjectLayer>().Count();

        // The renderer's answer has to be the same one the canvas showed.
        var reopenedDrawOrder = reopened.Layers.OfType<ObjectLayer>()
            .SelectMany(SceneGeometry.InDrawOrder)
            .Select(item => item.Name)
            .ToList();
        var exportMatchesTheCanvasOrder = expectedDrawOrder.SequenceEqual(reopenedDrawOrder);

        view.Canvas.DisposeRenderCaches();
        var resourcesDisposed = view.Canvas.ObjectCacheCount == 0
            && view.Canvas.TileCacheCount == 0
            && view.Canvas.ObjectCacheBytes == 0;

        var gatePassed = catalogShowsDistinctThumbnails
            && placedWithoutPreparingALayer
            && placementAndLayerUndoTogether
            && objectLevelChangesDrawOrder
            && objectMovedBetweenLayersByName
            && lockedLayerStillRefuses
            && copiedObjectCount == 3
            && pastedObjectCount == 3
            && pasteKeptRelativeLayout
            && pasteMintedNewIdentities
            && pasteSelectedTheWholeGroup
            && pasteIsOneUndoStep
            && redoRestoredTheGroup
            && survivedSaveAndReopen
            && exportMatchesTheCanvasOrder
            && resourcesDisposed
            && railHasNoSeparateStampButton
            && landFollowsAssetsInTheRail
            && objectPanelOpensWithLayerAndLevel
            && layerSelectorIsAListOfNames
            && levelSliderOnlyStopsOnWholeNumbers
            && oneSliderGestureIsOneUndo
            && mixedSelectionRefusesToInventAValue
            && shapeGetsTheSharedBlockWithoutTheBitmapOne
            && everyVisibleCaptionIsReadable
            && quickBarCarriesTheFrequentProperties
            && quickBarSaysDifferentLayers
            && layerDropDownListsEveryLayerByName
            && layerDropDownPicksFirstMiddleAndLast
            && manualLevelEntryIsAccepted
            && manualLevelEntryRejectsOutOfRange
            && levelResetReturnsToZero
            && levelEndsAreVisibleOnTheControl
            && noUserFacingStampWord
            && clipboardShortcutsReachedWindowRoute
            && undoRedoShortcutsReachedWindowRoute
            && wideAndCompactLayoutsUsable
            && limitations.Count == 0;

        return new NativeObjectWorkflowReport(
            DateTimeOffset.UtcNow,
            "hidden own-process Avalonia window, ShowActivated=false, ShowInTaskbar=false, no system input",
            editorScreenshot,
            panelScreenshot,
            catalogShowsDistinctThumbnails,
            placedWithoutPreparingALayer,
            placementAndLayerUndoTogether,
            objectLevelChangesDrawOrder,
            objectMovedBetweenLayersByName,
            lockedLayerStillRefuses,
            railOrder,
            railHasNoSeparateStampButton,
            landFollowsAssetsInTheRail,
            objectPanelOpensWithLayerAndLevel,
            layerSelectorIsAListOfNames,
            levelSliderBand,
            levelSliderOnlyStopsOnWholeNumbers,
            oneSliderGestureIsOneUndo,
            mixedSelectionRefusesToInventAValue,
            shapeGetsTheSharedBlockWithoutTheBitmapOne,
            quickBarCarriesTheFrequentProperties,
            quickBarSingleSelection,
            quickBarMixedSelection,
            quickBarSaysDifferentLayers,
            layerDropDownRowCount,
            layerDropDownListsEveryLayerByName,
            layerDropDownPicksFirstMiddleAndLast,
            manualLevelEntryIsAccepted,
            manualLevelEntryRejectsOutOfRange,
            levelResetReturnsToZero,
            levelEndsAreVisibleOnTheControl,
            noUserFacingStampWord,
            menuShot,
            dropShot,
            quickShot,
            compactShot,
            probes,
            Math.Round(worstContrast, 2),
            worstWhere,
            Math.Round(leaked.Worst, 2),
            leaked.Where,
            everyVisibleCaptionIsReadable,
            clipboardShortcutsReachedWindowRoute,
            undoRedoShortcutsReachedWindowRoute,
            wideAndCompactLayoutsUsable,
            copiedObjectCount,
            pastedObjectCount,
            pasteKeptRelativeLayout,
            pasteMintedNewIdentities,
            pasteSelectedTheWholeGroup,
            pasteIsOneUndoStep,
            redoRestoredTheGroup,
            survivedSaveAndReopen,
            exportMatchesTheCanvasOrder,
            resourcesDisposed,
            gatePassed,
            limitations);
    }

    // ------------------------------------------------------------------ helpers

    private static ObjectLayer FirstObjectLayer(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().First();

    private static IReadOnlyList<SceneObject> ObjectsOfFirstLayer(EditorViewModel editor) =>
        FirstObjectLayer(editor).Objects;

    private static IReadOnlyList<SceneObject> AllObjects(EditorViewModel editor) =>
        [.. editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(item => item.Objects)];

    private static List<ObjectId> DrawOrder(EditorViewModel editor) =>
        [.. editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(SceneGeometry.InDrawOrder).Select(item => item.Id)];

    private static List<string> DrawOrderNames(EditorViewModel editor) =>
        [.. editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(SceneGeometry.InDrawOrder).Select(item => item.Name)];

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.001;

    private static bool RaiseShortcut(EditorView view, Key key, KeyModifiers modifiers)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = view.Canvas,
        };
        view.Canvas.RaiseEvent(args);
        return args.Handled;
    }

    private static T? Named<T>(Visual root, string name) where T : Control
    {
        if (root.GetVisualDescendants().OfType<T>().FirstOrDefault(item => item.Name == name) is { } found) return found;
        // Folded sections of the inspector are not templated yet (2026-10-19 panel redesign):
        // open them the way a user would and look again.
        if (root is Control control) NativeEditorScenarioApplication.RevealTab(control, name);
        foreach (var folded in root.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray()) folded.IsExpanded = true;
        (root as Control)?.UpdateLayout();
        return root.GetVisualDescendants().OfType<T>().FirstOrDefault(item => item.Name == name);
    }

    /// <summary>The flat colour a brush paints, or null when it is not a flat opaque colour.</summary>
    private static Color? Resolve(IBrush? brush) =>
        brush is ISolidColorBrush solid && solid.Color.A > 8 ? solid.Color : null;

    /// <summary>
    /// The first opaque colour painted behind a caption. Transparent hosts are skipped, because a
    /// transparent row over a dark list is read against the list, not against nothing - which is
    /// exactly how the reported pair came about.
    /// </summary>
    private static Color? NearestBackground(Visual leaf)
    {
        for (var node = leaf.GetVisualParent(); node is not null; node = node.GetVisualParent())
        {
            var brush = node switch
            {
                Border border => border.Background,
                TemplatedControl templated => templated.Background,
                Panel panel => panel.Background,
                _ => null,
            };

            if (Resolve(brush) is { } colour)
            {
                return colour;
            }
        }

        return null;
    }

    private static double Channel(byte raw)
    {
        var value = raw / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        var (lighter, darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>True when the caption belongs to a control that is currently switched off.</summary>
    private static bool IsInsideDisabled(Visual leaf)
    {
        for (var node = leaf as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is InputElement { IsEffectivelyEnabled: false })
            {
                return true;
            }
        }

        return false;
    }

    private static string Chain(Visual leaf)
    {
        var parts = new List<string>();
        for (var node = leaf as Visual; node is not null && parts.Count < 5; node = node.GetVisualParent())
        {
            var control = node as Control;
            var classes = control is null || control.Classes.Count == 0 ? "" : "." + string.Join(".", control.Classes);
            parts.Add(node.GetType().Name + (control?.Name is { } n ? "#" + n : "") + classes);
        }

        return string.Join(" < ", parts);
    }

    private static string Shorten(string text) =>
        text.Length <= 28 ? text : text[..28] + "\u2026";

    private static void Capture(Visual view, string path, int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(view);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static MapDocument EmptyDocument() => new(
        DocumentId.New(), "Объекты и слои", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1_600, 1_000, "px", "#FF20242B"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B")]);

    private static AssetCatalog CreateCatalog(string workspace)
    {
        var sourceDirectory = Path.Combine(workspace, "source");
        Directory.CreateDirectory(sourceDirectory);

        string Draw(string name, SKColor color, int width, int height, bool triangle)
        {
            var path = Path.Combine(sourceDirectory, name + ".png");
            using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            if (triangle)
            {
                using var mountain = new SKPath();
                mountain.MoveTo(width / 2f, 4);
                mountain.LineTo(width - 4, height - 4);
                mountain.LineTo(4, height - 4);
                mountain.Close();
                surface.Canvas.DrawPath(mountain, paint);
            }
            else
            {
                surface.Canvas.DrawOval(new SKRect(4, 4, width - 4, height - 4), paint);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        var catalog = new AssetCatalog(Path.Combine(workspace, "catalog"));
        catalog.ImportSet(
            [
                Draw("tree-a", SKColors.ForestGreen, 64, 48, triangle: false),
                Draw("tree-b", SKColors.SeaGreen, 48, 64, triangle: false),
            ],
            "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        catalog.ImportSet(
            [Draw("mountain-a", SKColors.SaddleBrown, 72, 56, triangle: true)],
            "Гора", new AssetImportOptions(), DateTimeOffset.UnixEpoch.AddSeconds(1));
        return catalog;
    }

    private static async Task NextFrameAsync(EditorView view)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, EventArgs args) => completion.TrySetResult();
        view.Canvas.FrameRendered += OnFrame;
        try
        {
            view.Canvas.InvalidateVisual();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            view.Canvas.FrameRendered -= OnFrame;
        }
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(25);
    }

    private sealed class SmokeFilePicker(string path) : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path);
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
    }

    private sealed class SmokeLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class SmokeRecentMaps : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];
        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта") { }
        public void Remove(string path) { }
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
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
