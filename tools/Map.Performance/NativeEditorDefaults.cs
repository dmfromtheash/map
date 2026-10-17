using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using NativeKey = Avalonia.Input.Key;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.App;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    private MainWindow CreateDefaultsWindow(bool seedLegacy)
    {
        var profile = Path.Combine(_workspace, "profile");
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_SHORTCUT_SETTINGS_PATH", Path.Combine(profile, "shortcuts.json"));
        if (seedLegacy) File.WriteAllText(Path.Combine(profile, "editor-preferences.json"), LegacyPreferences(3));
        return new MainWindow(_store, _recent, new LocalLog(), _catalog)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
        };
    }

    private static string LegacyPreferences(int version) => JsonSerializer.Serialize(new
    {
        version, autoSelect = "Always", primaryAutosaveEnabled = false,
        stamp = new StampToolSettings
        {
            Mode = StampPlacementMode.Series, ScalePercent = 175, RotationDegrees = 90, Sublayer = -4,
            ObjectOpacity = .3, RandomizeVariant = true, RandomFlip = true,
            RandomAngleStep = StampRandomAngleStep.Degrees45, AssetSetId = "old-set", VariantId = "old-variant",
        },
        libraryTileSize = 160, libraryShowNames = false,
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static EditorView MountDefaultsEditor(MainWindow window)
    {
        var document = new MapDocument(DocumentId.New(), "Проверка клавиш", DateTimeOffset.UtcNow,
            new CanvasDefinition(1200, 800, "px", "#FFF5F2E8"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF5F2E8"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        // The production window constructs the editor and loads its actual preferences path.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var editor = (EditorViewModel)typeof(MainWindow).GetMethod("CreateEditor", flags)!
            .Invoke(window, [document, null, false, null])!;
        typeof(MainWindow).GetMethod("ShowEditor", flags)!.Invoke(window, [editor]);
        return (EditorView)typeof(MainWindow).GetField("_editorView", flags)!.GetValue(window)!;
    }

    private async Task VerifyEditorDefaults()
    {
        RegionalArtPack.Import(_catalog);
        _view = MountDefaultsEditor((MainWindow)_window);
        _editor = _view.Editor;
        await Settle();
        _view.Canvas.FitToCanvas();
        Check("startup ignores legacy placement and selected asset", _editor.StampToolSettings == new StampToolSettings()
            && !_editor.HasChosenAsset && _editor.PendingPlacement is null);
        Check("startup resets gallery but preserves autosave choice", _editor.LibraryTileSize == 112
            && _editor.LibraryShowNames && !_editor.PrimaryAutosaveEnabled);
        foreach (var version in new[] { 1, 2, 3 })
        {
            var path = Path.Combine(_workspace, $"legacy-v{version}.json");
            File.WriteAllText(path, LegacyPreferences(version));
            Check($"legacy preferences v{version} starts neutral", new EditorPreferencesStore(path).Load()
                == new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        }

        foreach (var (key, tool) in new[] { (NativeKey.V, EditorTool.Select), (NativeKey.B, EditorTool.Brush),
                     (NativeKey.M, EditorTool.Mask), (NativeKey.L, EditorTool.Line), (NativeKey.R, EditorTool.Shape),
                     (NativeKey.T, EditorTool.Text), (NativeKey.H, EditorTool.River), (NativeKey.J, EditorTool.Road), (NativeKey.P, EditorTool.Land) })
        {
            PressEditorKey(key);
            Check(key + " switches the active tool", _editor.ActiveTool == tool);
            PressEditorKey(NativeKey.F);
            await Settle();
            var catalog = CatalogWindow();
            if (catalog.FocusManager?.GetFocusedElement() is not TextBox { Name: "AssetSearchBox" })
                Console.WriteLine($"catalog state: visible={catalog.IsVisible}, tool={_editor.ActiveTool}, focus={catalog.FocusManager?.GetFocusedElement()}");
            Check("F opens catalog from " + tool, catalog.IsVisible && _editor.ActiveTool == EditorTool.Stamp
                && catalog.FocusManager?.GetFocusedElement() is TextBox { Name: "AssetSearchBox" });
            EscapeCatalog(catalog);
            Check("Escape closes catalog from " + tool, !catalog.IsVisible);
        }
        PressEditorKey(NativeKey.O); await Settle();
        Check("O opens the asset catalog", CatalogWindow().IsVisible);
        EscapeCatalog(CatalogWindow());
        PressEditorKey(NativeKey.None, physical: PhysicalKey.F); await Settle();
        Check("physical F opens catalog independently of text layout", CatalogWindow().IsVisible);
        EscapeCatalog(CatalogWindow());

        var set = _catalog.Sets.First(s => s.VariantIds.Count >= 2);
        _editor.SelectAssetSet(set.Id);
        await Settle();
        var originalVariant = _editor.CurrentVariant!.Id;
        PressEditorKey(NativeKey.Right, KeyModifiers.Alt);
        var nextVariant = _editor.CurrentVariant!.Id;
        PressEditorKey(NativeKey.Left, KeyModifiers.Alt);
        Check("Alt arrows change and restore the asset variant", nextVariant != originalVariant && _editor.CurrentVariant.Id == originalVariant);
        PressEditorKey(NativeKey.W);
        Check("W increases placement scale", _editor.StampToolSettings.ScalePercent > 100);
        PressEditorKey(NativeKey.S);
        Check("S restores placement scale", _editor.StampToolSettings.ScalePercent == 100);
        PressEditorKey(NativeKey.D);
        Check("D rotates the future asset", _editor.StampToolSettings.RotationDegrees > 0);
        PressEditorKey(NativeKey.A);
        Check("A rotates the future asset back", _editor.StampToolSettings.RotationDegrees == 0);

        _editor.StampPlacementMode = StampPlacementMode.Area;
        _editor.SetStampScalePercent(175, true); _editor.SetStampRotationDegrees(45, true);
        _editor.SetStampOpacityPercent(25, true); _editor.SetStampFlip(true, true);
        _editor.SetStampHueDegrees(70); _editor.SetStampShadowStandard();
        _editor.RandomVariantAfterPlacement = true; _editor.StampRandomFlip = true;
        _editor.StampRandomAngleStep = StampRandomAngleStep.Degrees45;
        var document = _editor.Session.Document; var undo = _editor.Session.UndoDepth;
        await Settle();
        Click(_view, "ResetStampToolSettingsButton"); await Settle();
        Check("visible reset button restores neutral placement", NeutralPlacement(_editor)
            && _editor.CurrentVariant!.Id == originalVariant && _editor.PendingPlacement?.Mode == StampPlacementMode.One);
        Check("reset preserves map and undo history", ReferenceEquals(document, _editor.Session.Document) && undo == _editor.Session.UndoDepth);
        Capture(_view, "01-defaults-button");

        // Text and number fields must retain normal typing. Enter committing a number must not
        // also finish the path after the field returns focus to the canvas.
        Click(_view, "StampModePath"); await Settle();
        Click(_view, "StartEditablePathButton");
        await Settle();
        _view.Canvas.ClickForSmoke(220, 220); _view.Canvas.ClickForSmoke(420, 290);
        await Settle();
        var entry = _view.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled && t.Name == "EditablePathSpacingBox");
        entry.BringIntoView(); await Settle();
        entry.Focus();
        var textKey = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = NativeKey.F, PhysicalKey = PhysicalKey.F };
        entry.RaiseEvent(textKey);
        Check("F inside a text field does not open catalog", !CatalogWindow().IsVisible && entry.IsFocused);
        var toolBeforeTab = _editor.ActiveTool;
        entry.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = NativeKey.Tab });
        Check("Tab inside a text field keeps normal focus navigation", _editor.ActiveTool == toolBeforeTab);
        entry.Focus();
        entry.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = NativeKey.Enter });
        Check("Enter in a number field only commits the field", _editor.IsCreatingEditablePath);
        PressEditorKey(NativeKey.Enter); await Settle();
        Check("Enter on canvas commits a path", !_editor.IsCreatingEditablePath && _editor.SelectedEditablePath is not null);
        var completed = _editor.Session.Document;
        PressEditorKey(NativeKey.Z, KeyModifiers.Control);
        Check("Ctrl Z undoes the path", !ReferenceEquals(completed, _editor.Session.Document));
        PressEditorKey(NativeKey.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Check("Ctrl Shift Z restores the path", ReferenceEquals(completed, _editor.Session.Document));
        PressEditorKey(NativeKey.Z, KeyModifiers.Control);
        PressEditorKey(NativeKey.Y, KeyModifiers.Control);
        Check("Ctrl Y restores the path", ReferenceEquals(completed, _editor.Session.Document));
        Click(_view, "ResetStampToolSettingsButton"); await Settle();
        Check("reset from selected path does not edit placed content", ReferenceEquals(completed, _editor.Session.Document) && NeutralPlacement(_editor));

        Click(_view, "StampModeFillArea"); await Settle();
        Click(_view, "StartEditableAreaButton"); await Settle();
        _view.Canvas.ClickForSmoke(230, 240); _view.Canvas.ClickForSmoke(410, 240);
        PressEditorKey(NativeKey.Escape);
        Check("Escape cancels an unfinished area without editing the map", !_editor.IsCreatingEditableArea
            && ReferenceEquals(completed, _editor.Session.Document));
        Click(_view, "ResetStampToolSettingsButton"); await Settle();
        _view.Canvas.ClickForSmoke(320, 350); await Settle();
        PressEditorKey(NativeKey.Escape); PressEditorKey(NativeKey.V);
        _editor.Session.SelectedObjectId = _editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(l => l.Objects).Last().Id;
        var beforeDelete = _editor.Session.Document;
        var objectsBeforeDelete = beforeDelete.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        PressEditorKey(NativeKey.Delete);
        Check("Delete removes a selected object", _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == objectsBeforeDelete.Length - 1);
        PressEditorKey(NativeKey.Z, KeyModifiers.Control);
        Check("Ctrl Z restores a deleted object", objectsBeforeDelete.SequenceEqual(_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)));

        PressEditorKey(NativeKey.B); await Settle(); PressEditorKey(NativeKey.V); await Settle(); PressEditorKey(NativeKey.Tab);
        Check("Tab returns to previous tool", _editor.ActiveTool == EditorTool.Brush);
        PressEditorKey(NativeKey.Space);
        Check("Space starts temporary pan", _view.Canvas.IsSpacePanHeld);
        _view.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = NativeKey.Space });
        Check("releasing Space ends temporary pan", !_view.Canvas.IsSpacePanHeld);

        _editor.ArmStampPlacement(); _editor.StampPlacementMode = StampPlacementMode.Series;
        _editor.SetStampScalePercent(220, true); _editor.SetStampOpacityPercent(30, true);
        _editor.LibraryTileSize = 160; _editor.LibraryShowNames = false;
        var preferences = new EditorPreferencesStore(Path.Combine(_workspace, "profile", "editor-preferences.json"));
        Check("disk preferences contain no remembered placement", preferences.Load() == new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        var restarted = CreateDefaultsWindow(seedLegacy: false);
        var reopenedView = MountDefaultsEditor(restarted);
        try
        {
            restarted.Show(); await Settle();
            Check("new application window starts with single neutral placement", reopenedView.Editor.StampToolSettings == new StampToolSettings()
                && !reopenedView.Editor.HasChosenAsset && reopenedView.Editor.LibraryTileSize == 112 && reopenedView.Editor.LibraryShowNames);
        }
        finally { reopenedView.CloseAuxiliaryWindowsForSmoke(); reopenedView.Canvas.DisposeRenderCaches(); restarted.Close(); }
        Click(_view, "ResetStampToolSettingsButton"); await Settle();
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
    }

    private static bool NeutralPlacement(EditorViewModel editor)
    {
        var settings = editor.StampToolSettings;
        return settings with { AssetSetId = null, VariantId = null, Style = null } == new StampToolSettings()
            && settings.Style is { FlipHorizontal: false, FlipVertical: false, Blur: 0, ShadowOpacity: 0,
                Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0, Blend: BlendModes.Normal, ClipMaskEnabled: false };
    }

    private void PressEditorKey(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None)
    {
        var focused = _view!.Canvas.Focus();
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, PhysicalKey = physical,
        };
        _view.Canvas.RaiseEvent(args);
        if (key == NativeKey.Tab) Console.WriteLine($"Tab state: focusedBefore={focused}, focusedAfter={_view.Canvas.IsFocused}, handled={args.Handled}, tool={_editor.ActiveTool}, focus={_window.FocusManager?.GetFocusedElement()}");
    }

    private Window CatalogWindow() => ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows
        .Single(w => w.Name == "AssetCatalogWindow");

    private static void EscapeCatalog(Window catalog)
    {
        var target = catalog.FocusManager?.GetFocusedElement() as InputElement ?? catalog;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = NativeKey.Escape });
    }
}
