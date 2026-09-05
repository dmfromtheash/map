using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Persistence;
using Map.Platform.Windows;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.App;

/// <summary>
/// The single native window hosting the dashboard and the editor. Navigation lives here; document
/// truth lives in the editing session; nothing is lost silently on close.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IProjectStore _store;
    private readonly IRecentMaps _recent;
    private readonly IAppLog _log;
    private readonly AssetCatalog _assets;
    private readonly ShortcutSettingsStore _shortcutStore;
    private readonly EditorShortcutSettings _shortcuts;
    private readonly EditorPreferencesStore _editorPreferencesStore;
    private EditorLocalPreferences _editorPreferences;
    private readonly AppSettingsStore _appSettingsStore;
    private AppSettings _appSettings;
    private readonly ContentControl _host = new() { Name = "ViewHost" };
    private readonly DashboardViewModel _dashboard;
    private readonly DispatcherTimer _autosaveTimer;

    private EditorView? _editorView;
    private bool _autosaveTickRunning;
    private Key? _heldPanKey;
    private string? _shortcutLoadWarning;

    public MainWindow(
        IProjectStore store,
        IRecentMaps recent,
        IAppLog log,
        AssetCatalog assets,
        ShortcutSettingsStore? shortcutStore = null,
        EditorShortcutSettings? shortcuts = null,
        string? shortcutLoadWarning = null,
        AppSettingsStore? appSettingsStore = null,
        AppSettings? appSettings = null)
    {
        _store = store;
        _recent = recent;
        _log = log;
        _assets = assets;
        var appSettingsPath = TestMode.Enabled
            ? Path.Combine(AppPaths.ResolveTestProfileRoot(TestMode.ProfileRootOverride), "settings.json")
            : AppPaths.AppSettingsPath;
        _appSettingsStore = appSettingsStore ?? new AppSettingsStore(appSettingsPath);
        _appSettings = AppSettings.Sanitize(appSettings ?? _appSettingsStore.Load());
        _shortcutStore = shortcutStore
            ?? new ShortcutSettingsStore(AppPaths.ResolveShortcutSettingsPath(
                TestMode.Enabled, TestMode.ShortcutSettingsPathOverride));
        _shortcuts = shortcuts ?? LoadShortcuts(_shortcutStore, out _shortcutLoadWarning);
        if (shortcuts is not null)
        {
            _shortcutLoadWarning = shortcutLoadWarning;
        }
        var preferencesPath = TestMode.Enabled
            ? Path.Combine(
                AppPaths.ResolveTestProfileRoot(TestMode.ProfileRootOverride),
                "editor-preferences.json")
            : AppPaths.EditorPreferencesPath;
        _editorPreferencesStore = new EditorPreferencesStore(preferencesPath);
        _editorPreferences = _editorPreferencesStore.Load();

        Title = $"Map · {AppVersion.Current}";
        Name = "MainWindow";
        Width = 1024;
        Height = 680;
        MinWidth = 960;
        MinHeight = 640;
        Content = _host;
        if (TestMode.StartupSmoke)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-32000, -32000);
            Opened += async (_, _) =>
            {
                try
                {
                    await Task.Delay(1000);
                    var root = AppPaths.ResolveTestProfileRoot(TestMode.ProfileRootOverride);
                    Directory.CreateDirectory(root);
                    UpdateLayout();
                    using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize((int)Math.Ceiling(Bounds.Width), (int)Math.Ceiling(Bounds.Height)));
                    image.Render(this);
                    image.Save(Path.Combine(root, "startup.png"));
                    File.WriteAllText(Path.Combine(root, "startup.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Window = Name, Title, Dashboard = _host.Content is DashboardView,
                        Assembly = typeof(MainWindow).Assembly.Location, Width = Bounds.Width, Height = Bounds.Height,
                    }));
                }
                catch (Exception error) { _log.Error("STARTUP_SMOKE_FAILED", error); }
                finally { Close(); }
            };
        }

        _dashboard = new DashboardViewModel(
            _store,
            _recent,
            _log,
            new AvaloniaFilePicker(this),
            new DialogPrompts(this),
            new WindowsProjectTrash(),
            appVersion: AppVersion.Current);
        _autosaveTimer = new DispatcherTimer
        {
            Interval = TestMode.Enabled
                ? TestMode.AutosaveInterval
                : TimeSpan.FromSeconds(_appSettings.AutosaveIntervalSeconds),
        };
        _autosaveTimer.Tick += OnAutosaveTick;

        ApplySettings(_appSettings);

        ShowDashboard();
        Closing += OnClosing;

        // KeyDown is a bubbling-only event in Avalonia, so a tunnel handler would never fire.
        // Listening on the bubble route with handledEventsToo keeps the shortcuts working even
        // when a child control has already marked the key as handled.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // ---------------------------------------------------------------- navigation

    private void ShowDashboard()
    {
        _autosaveTimer.Stop();
        _editorView = null;
        var view = new DashboardView(
            _dashboard,
            newMapRequested: NewMapAsync,
            openFileRequested: OpenFileAsync,
            openRecentRequested: card => OpenPathAsync(card.Path),
            restoreRequested: RestoreAsync,
            shortcuts: _shortcuts,
            cloneRequested: CloneProjectAsync,
            deleteRequested: card => _dashboard.DeleteAsync(card),
            templateRequested: CreateFromUserTemplateAsync,
            backupRequested: ImportBackupAsync,
            settingsRequested: OpenAppSettings,
            helpRequested: ShowLocalHelp);

        _host.Content = view;
        view.Refresh();
    }

    private void ShowEditor(EditorViewModel editor)
    {
        _autosaveTimer.Stop();
        _editorView = new EditorView(
            editor,
            requestBack: LeaveEditorAsync,
            requestSecondWindow: OpenSecondWindow,
            requestShortcutSettings: OpenShortcutSettings);
        _editorView.Canvas.PerformanceMode = _appSettings.PerformanceMode;
        _host.Content = _editorView;
        if (TestMode.AutosaveEnabled)
        {
            _autosaveTimer.Start();
        }

        // Without a focused element inside the window, key input has nowhere to route and every
        // shortcut silently does nothing. The canvas is the natural first owner of the keyboard.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _editorView?.Canvas.Focus(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OpenSecondWindow()
    {
        var window = new MainWindow(
            _store, _recent, _log, _assets, _shortcutStore, _shortcuts, _shortcutLoadWarning,
            _appSettingsStore, _appSettings)
        {
            Title = $"Map · {AppVersion.Current} · второе окно",
        };
        window.Show();
    }

    private EditorViewModel CreateEditor(Map.Core.Documents.MapDocument document, string? path, bool startDirty,
        DateTimeOffset? lastSavedUtc = null)
    {
        var editor = new EditorViewModel(
            document,
            path,
            startDirty,
            _store,
            _recent,
            new AvaloniaFilePicker(this),
            new DialogPrompts(this),
            _log,
            lastSavedUtc: lastSavedUtc,
            appVersion: AppVersion.Current,
            assetCatalog: _assets,
            shortcuts: _shortcuts,
            exportDestinationPicker: new AvaloniaExportDestinationPicker(this),
            folderLauncher: new WindowsFolderLauncher(),
            localPreferences: _editorPreferences,
            saveLocalPreferences: preferences =>
            {
                _editorPreferences = preferences;
                _editorPreferencesStore.Save(preferences);
            });
        ConfigureExportDefaults(editor);
        return editor;
    }

    private async Task NewMapAsync()
    {
        var document = await WizardWindow.ShowAsync(this);
        if (document is not null)
        {
            _log.Info("NEW_MAP_CREATED");
            ShowEditor(CreateEditor(document, path: null, startDirty: true));
        }
    }

    private async Task OpenFileAsync()
    {
        var path = UiTestFilePaths.MapPath ?? await new AvaloniaFilePicker(this).PickOpenAsync();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        var opened = await _dashboard.TryOpenAsync(path);
        if (opened is not null)
        {
            ShowEditor(CreateEditor(opened.Project.Document, opened.Path, startDirty: false,
                lastSavedUtc: opened.Project.ModifiedUtc));
        }
    }

    private Task CloneProjectAsync(RecentCard card) => _dashboard.CloneAsync(card);

    private async Task CreateFromUserTemplateAsync()
    {
        var path = await new AvaloniaFilePicker(this).PickOpenAsync();
        if (path is null) return;
        await _dashboard.CloneAsync(new RecentCard(
            path, Path.GetFileNameWithoutExtension(path), DateTimeOffset.MinValue, true, null),
            asTemplate: true);
    }

    private Task ImportBackupAsync() => OpenFileAsync();

    private async void OpenAppSettings()
    {
        var dialog = new AppSettingsWindow(
            _appSettingsStore, _appSettings, ApplySettings, BuildLocalDiagnostics);
        await dialog.ShowDialog(this);
    }

    private void ApplySettings(AppSettings settings)
    {
        _appSettings = AppSettings.Sanitize(settings);

        // Map has ONE approved palette and one approved icon set, both drawn for a light working
        // surface over dark chrome. Handing Fluent a dark variant replaced the container colours
        // underneath that palette while Map's own text and artwork stayed light-surface: the
        // objects list ended up with #10264A text on a #2B2B2B ground (1.03:1) and the file menu
        // showed navy artwork on near-black. The variant is therefore pinned, and the palette
        // below is what decides every colour.
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = ThemeVariant.Light;
        }
        FontSize = 13 * _appSettings.UiScale;
        if (!TestMode.Enabled)
            _autosaveTimer.Interval = TimeSpan.FromSeconds(_appSettings.AutosaveIntervalSeconds);
        if (_editorView is { } editorView)
        {
            editorView.Canvas.PerformanceMode = _appSettings.PerformanceMode;
            ConfigureExportDefaults(editorView.Editor);
        }
    }

    private void ConfigureExportDefaults(EditorViewModel editor) => editor.ConfigureExportDefaults(
        _appSettings.DefaultExportFormat switch
        {
            "Jpeg" => Map.Export.ExportImageFormat.Jpeg,
            "Webp" => Map.Export.ExportImageFormat.Webp,
            _ => Map.Export.ExportImageFormat.Png,
        },
        _appSettings.DefaultExportScalePercent,
        _appSettings.DefaultExportIncludeGrid);

    private string BuildLocalDiagnostics()
    {
        if (_editorView is not { } view)
            return "Рендерер: Avalonia + Skia, CPU fallback доступен. Карта не открыта. " +
                   $"Режим: {_appSettings.PerformanceMode}.";
        var document = view.Editor.Session.Document;
        var objectCount = document.Layers.OfType<Map.Core.Documents.ObjectLayer>()
            .Sum(layer => layer.Objects.Count);
        return $"Рендерер: Avalonia + Skia, CPU fallback доступен. " +
               $"Режим: {_appSettings.PerformanceMode}; LOD: {view.Canvas.LastLodTier}; " +
               $"кэш тайлов: {view.Canvas.TileCacheCount}; кэш объектов: {view.Canvas.ObjectCacheCount}; " +
               $"Undo: {(view.Editor.CanUndo ? "есть" : "пусто")}; Redo: {(view.Editor.CanRedo ? "есть" : "пусто")}; " +
               $"слоёв: {document.Layers.Count}; объектов: {objectCount}; " +
               $"последнее давление пера: {view.Canvas.LastPressureSample:0.00} (мышь: 1.00).";
    }

    private void ShowLocalHelp()
    {
        var help = new Window
        {
            Title = "Справка Map — работает без интернета",
            ShowActivated = !TestMode.Enabled,
            ShowInTaskbar = !TestMode.Enabled,
            Width = 720,
            Height = 650,
            MinWidth = 520,
            MinHeight = 400,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Name = "LocalHelpText",
                    Margin = new Avalonia.Thickness(24),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = "ПЕРВАЯ КАРТА\n\n" +
                           "Нажмите «Новая карта», выберите размер и создайте проект. Сохраните его как .mapproj.\n\n" +
                           "ИНСТРУМЕНТЫ\n\n" +
                           "Слева выбираются объекты, кисть, земля, линии, фигуры, текст, маски и заметки. " +
                           "Текущее действие и его точные значения находятся в контекстной панели.\n\n" +
                           "ВОССТАНОВЛЕНИЕ\n\n" +
                           "Автосохранённые копии появляются на стартовом экране. Восстановление открывает копию " +
                           "и не перезаписывает исходный файл без явного сохранения.\n\n" +
                           "КЛАВИАТУРА\n\n" +
                           "Ctrl+S — сохранить, Ctrl+Z — отменить, Ctrl+Shift+Z — повторить, G — привязка к сетке, " +
                           "Esc — отменить незавершённое действие. Полный список: кнопка сочетаний в редакторе.\n\n" +
                           "Map не требует сети, аккаунта или телеметрии.",
                },
            },
        };
        if (TestMode.Enabled) help.Position = new PixelPoint(-32000, -32000);
        _ = help.ShowDialog(this);
    }

    private Task RestoreAsync(RecoveryCard card)
    {
        var restored = _dashboard.TryRestore(card);
        if (restored is not null)
        {
            // The recovered snapshot opens DIRTY and unbound to the original path decision:
            // nothing overwrites the old file until the user explicitly saves.
            ShowEditor(CreateEditor(restored.Document, card.Candidate.TargetPath, startDirty: true));
        }

        return Task.CompletedTask;
    }

    private async Task LeaveEditorAsync()
    {
        if (_editorView is null)
        {
            return;
        }

        if (await _editorView.Editor.ConfirmCloseAsync())
        {
            ShowDashboard();
        }
    }

    private async void OnAutosaveTick(object? sender, EventArgs e)
    {
        if (_autosaveTickRunning || _editorView is not { } editorView)
        {
            return;
        }

        _autosaveTickRunning = true;
        try
        {
            await editorView.Editor.TryAutosaveAsync();
        }
        finally
        {
            _autosaveTickRunning = false;
        }
    }

    // ---------------------------------------------------------------- window close

    private bool _closeApproved;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_editorView is null || await _editorView.Editor.ConfirmCloseAsync())
        {
            try { await _assets.FlushMetadataAsync(); }
            catch (IOException error) { _log.Error("CATALOG_METADATA_SAVE_FAILED", error); }
            _autosaveTimer.Stop();
            _closeApproved = true;
            Close();
        }
    }

    // ---------------------------------------------------------------- shortcuts

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Application shortcuts never steal editing keys from any text field. The settings dialog
        // is a separate window and captures its own gesture input explicitly.
        if (IsTypingInText() || !ShortcutKeyAdapter.TryCreate(e, out var input))
        {
            return;
        }

        var activeContexts = ActiveShortcutContexts();
        var command = _shortcuts.Resolve(input, activeContexts);
        if (command is not null && ExecuteShortcut(command, e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_heldPanKey == e.Key && _editorView is { } editorView)
        {
            editorView.Canvas.IsSpacePanHeld = false;
            _heldPanKey = null;
        }
    }

    private IReadOnlySet<ShortcutContext> ActiveShortcutContexts()
    {
        var contexts = new HashSet<ShortcutContext>();
        if (_editorView is not { } editorView)
        {
            contexts.Add(ShortcutContext.Dashboard);
            return contexts;
        }

        contexts.Add(ShortcutContext.Editor);
        if (editorView.Canvas.IsFocused) contexts.Add(ShortcutContext.Canvas);
        if (editorView.Editor.SelectionCount > 0) contexts.Add(ShortcutContext.Selection);
        if (editorView.IsNotesOpen) contexts.Add(ShortcutContext.Notes);
        contexts.Add(editorView.Editor.ActiveTool switch
        {
            EditorTool.Brush => ShortcutContext.Brush,
            EditorTool.Mask => ShortcutContext.Mask,
            EditorTool.Land => ShortcutContext.Land,
            EditorTool.Text => ShortcutContext.Text,
            EditorTool.Stamp => ShortcutContext.Stamp,
            _ => ShortcutContext.Editor,
        });
        return contexts;
    }

    private bool ExecuteShortcut(string command, Key sourceKey)
    {
        if (command == EditorShortcutSettings.Commands.NewMap)
        {
            _ = HandleNewShortcutAsync();
            return true;
        }

        if (command == EditorShortcutSettings.Commands.OpenMap)
        {
            _ = HandleOpenShortcutAsync();
            return true;
        }

        if (_editorView is not { } view)
        {
            return false;
        }

        var editor = view.Editor;
        switch (command)
        {
            case EditorShortcutSettings.Commands.Save:
                _ = editor.SaveAsync();
                break;
            case EditorShortcutSettings.Commands.Undo:
                editor.Undo();
                break;
            case EditorShortcutSettings.Commands.Redo:
            case EditorShortcutSettings.Commands.RedoAlternate:
                editor.Redo();
                break;
            case EditorShortcutSettings.Commands.ShortcutHelp:
            case EditorShortcutSettings.Commands.ShortcutHelpAlternate:
                ShowShortcutHelp();
                break;
            case EditorShortcutSettings.Commands.ShortcutSettings:
                OpenShortcutSettings();
                break;
            case EditorShortcutSettings.Commands.SidePanel:
                view.ToggleSidePanel();
                break;
            case EditorShortcutSettings.Commands.GridSnap:
                editor.ToggleGridSnap();
                break;
            case EditorShortcutSettings.Commands.PreviousTool:
                editor.SelectPreviousTool();
                break;
            case EditorShortcutSettings.Commands.Pan:
                view.Canvas.IsSpacePanHeld = true;
                _heldPanKey = sourceKey;
                break;
            case EditorShortcutSettings.Commands.Select:
                editor.ActiveTool = EditorTool.Select;
                break;
            case EditorShortcutSettings.Commands.Line:
                editor.ActiveTool = EditorTool.Line;
                break;
            case EditorShortcutSettings.Commands.Shape:
                editor.ActiveTool = EditorTool.Shape;
                break;
            case EditorShortcutSettings.Commands.Text:
                editor.ActiveTool = EditorTool.Text;
                break;
            case EditorShortcutSettings.Commands.Stamp:
                editor.ActiveTool = EditorTool.Stamp;
                view.OpenAssetCatalog();
                break;
            case EditorShortcutSettings.Commands.Brush:
                editor.ActiveTool = EditorTool.Brush;
                break;
            case EditorShortcutSettings.Commands.Mask:
                editor.ActiveTool = EditorTool.Mask;
                break;
            case EditorShortcutSettings.Commands.Land:
                editor.ActiveTool = EditorTool.Land;
                break;
            case EditorShortcutSettings.Commands.Notes:
                view.OpenNotes();
                break;
            case EditorShortcutSettings.Commands.Escape:
                editor.HandleEscape();
                break;
            case EditorShortcutSettings.Commands.FinishPath:
                editor.FinishPenPath();
                break;
            case EditorShortcutSettings.Commands.Delete:
            case EditorShortcutSettings.Commands.DeleteAlternate:
                editor.DeleteSelectedObject();
                break;
            case EditorShortcutSettings.Commands.NudgeLeft:
                editor.NudgeSelectedObject(-1, 0);
                break;
            case EditorShortcutSettings.Commands.NudgeRight:
                editor.NudgeSelectedObject(1, 0);
                break;
            case EditorShortcutSettings.Commands.NudgeUp:
                editor.NudgeSelectedObject(0, -1);
                break;
            case EditorShortcutSettings.Commands.NudgeDown:
                editor.NudgeSelectedObject(0, 1);
                break;
            case EditorShortcutSettings.Commands.NudgeLargeLeft:
                editor.NudgeSelectedObject(-10, 0);
                break;
            case EditorShortcutSettings.Commands.NudgeLargeRight:
                editor.NudgeSelectedObject(10, 0);
                break;
            case EditorShortcutSettings.Commands.NudgeLargeUp:
                editor.NudgeSelectedObject(0, -10);
                break;
            case EditorShortcutSettings.Commands.NudgeLargeDown:
                editor.NudgeSelectedObject(0, 10);
                break;
            case EditorShortcutSettings.Commands.PrimaryUp:
                AdjustPrimary(editor, 1, false);
                break;
            case EditorShortcutSettings.Commands.PrimaryDown:
                AdjustPrimary(editor, -1, false);
                break;
            case EditorShortcutSettings.Commands.PrimaryLargeUp:
                AdjustPrimary(editor, 1, true);
                break;
            case EditorShortcutSettings.Commands.PrimaryLargeDown:
                AdjustPrimary(editor, -1, true);
                break;
            case EditorShortcutSettings.Commands.SecondaryUp:
                AdjustSecondary(view, 1, false);
                break;
            case EditorShortcutSettings.Commands.SecondaryDown:
                AdjustSecondary(view, -1, false);
                break;
            case EditorShortcutSettings.Commands.SecondaryLargeUp:
                AdjustSecondary(view, 1, true);
                break;
            case EditorShortcutSettings.Commands.SecondaryLargeDown:
                AdjustSecondary(view, -1, true);
                break;
            case EditorShortcutSettings.Commands.TertiaryUp:
                AdjustTertiary(view, 1, false);
                break;
            case EditorShortcutSettings.Commands.TertiaryDown:
                AdjustTertiary(view, -1, false);
                break;
            case EditorShortcutSettings.Commands.TertiaryLargeUp:
                AdjustTertiary(view, 1, true);
                break;
            case EditorShortcutSettings.Commands.TertiaryLargeDown:
                AdjustTertiary(view, -1, true);
                break;
            case EditorShortcutSettings.Commands.RotateLeft:
                AdjustRotation(editor, -1, false);
                break;
            case EditorShortcutSettings.Commands.RotateRight:
                AdjustRotation(editor, 1, false);
                break;
            case EditorShortcutSettings.Commands.PreviousSelection:
                editor.RestoreLastSelection();
                break;
            case EditorShortcutSettings.Commands.MaskMode:
                editor.ToggleMaskPaintMode();
                break;
            case EditorShortcutSettings.Commands.Copy:
                editor.CopySelectedObjects();
                break;
            case EditorShortcutSettings.Commands.Paste:
                editor.PasteObjects();
                break;
            case EditorShortcutSettings.Commands.Lock:
                editor.ToggleSelectedObjectLock();
                break;
            case EditorShortcutSettings.Commands.SelectAll:
                editor.SelectAllByFilter();
                break;
            case EditorShortcutSettings.Commands.Group:
                _ = view.GroupSelectionInteractiveAsync();
                break;
            case EditorShortcutSettings.Commands.Ungroup:
                editor.UngroupSelection();
                break;
            case EditorShortcutSettings.Commands.SublayerUp:
                editor.AdjustSelectedSublayer(1);
                break;
            case EditorShortcutSettings.Commands.SublayerDown:
                editor.AdjustSelectedSublayer(-1);
                break;
            case EditorShortcutSettings.Commands.ObjectUp:
                editor.ReorderSelection(1);
                break;
            case EditorShortcutSettings.Commands.ObjectDown:
                editor.ReorderSelection(-1);
                break;
            case EditorShortcutSettings.Commands.ObjectTop:
                editor.ReorderSelection(1, toExtreme: true);
                break;
            case EditorShortcutSettings.Commands.ObjectBottom:
                editor.ReorderSelection(-1, toExtreme: true);
                break;
            case EditorShortcutSettings.Commands.LayerUp:
                editor.ReorderActiveLayer(up: true);
                break;
            case EditorShortcutSettings.Commands.LayerDown:
                editor.ReorderActiveLayer(up: false);
                break;
            case EditorShortcutSettings.Commands.StampFocusLibrary:
                view.OpenAssetCatalog(focusSearch: true);
                break;
            case EditorShortcutSettings.Commands.StampNextVariant:
                editor.NextVariant();
                view.RefreshLibraryPanel();
                break;
            case EditorShortcutSettings.Commands.StampPreviousVariant:
                editor.PreviousVariant();
                view.RefreshLibraryPanel();
                break;
            case EditorShortcutSettings.Commands.StampDensityUp:
                editor.AdjustStampDensity(1, false);
                view.RefreshLibraryPanel();
                break;
            case EditorShortcutSettings.Commands.StampDensityDown:
                editor.AdjustStampDensity(-1, false);
                view.RefreshLibraryPanel();
                break;
            case EditorShortcutSettings.Commands.FontPrevious:
                editor.SelectAdjacentTextFont(-1);
                break;
            case EditorShortcutSettings.Commands.FontNext:
                editor.SelectAdjacentTextFont(1);
                break;
            default:
                return false;
        }

        return true;
    }

    private static void AdjustPrimary(EditorViewModel editor, int direction, bool large)
    {
        if (editor.ResizePendingPlacement(direction, large))
            return;
        if (editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
            editor.AdjustBrushSize(direction, large);
        else if (editor.ActiveTool == EditorTool.Text)
            editor.AdjustSelectedTextFontSize(direction, large);
        else if (editor.ActiveTool == EditorTool.Select)
            editor.ResizeSelectedObject(direction, large);
    }

    private static void AdjustRotation(EditorViewModel editor, int direction, bool large)
    {
        if (!editor.RotatePendingPlacement(direction, large))
            editor.RotateSelectedObject(direction, large);
    }

    private static void AdjustSecondary(EditorView view, int direction, bool large)
    {
        var editor = view.Editor;
        if (direction > 0 && view.IsNotesOpen)
            view.OpenNotes(focusEditor: true);
        else if (editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
            editor.AdjustBrushOpacity(direction, large);
        else if (editor.ActiveTool == EditorTool.Select)
            editor.AdjustSelectedOpacity(direction, large);
    }

    private static void AdjustTertiary(EditorView view, int direction, bool large)
    {
        var editor = view.Editor;
        if (editor.ActiveTool == EditorTool.Stamp)
        {
            editor.AdjustStampArea(direction, large);
            view.RefreshLibraryPanel();
        }
        else if (editor.ActiveTool == EditorTool.Brush)
            editor.AdjustBrushSoftness(direction, large);
        else if (editor.ActiveTool == EditorTool.Text)
            editor.AdjustSelectedTextCurve(direction, large);
    }

    private bool IsTypingInText() => FocusManager?.GetFocusedElement() is TextBox;

    private void ShowShortcutHelp()
    {
        var text = new StringBuilder();
        foreach (var group in EditorShortcutSettings.Definitions
                     .Where(definition => definition.IsEditable && _shortcuts.Get(definition.Id).Length > 0)
                     .GroupBy(definition => definition.Group))
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine(group.Key.ToUpperInvariant());
            foreach (var definition in group)
            {
                text.Append(_shortcuts.Get(definition.Id))
                    .Append(" — ")
                    .Append(definition.Name)
                    .Append(" · ")
                    .AppendLine(EditorShortcutSettings.ContextName(definition.Context));
            }
        }
        text.AppendLine()
            .AppendLine("МЫШЬ")
            .AppendLine("Колесо — масштаб · средняя кнопка или удержание команды панорамы + drag — панорама")
            .AppendLine($"{_shortcuts.Get(EditorShortcutSettings.Commands.StampCycleVariant)} — вариант ассета над холстом");

        var dialog = new Window
        {
            Title = "Горячие клавиши Map",
            Width = 760,
            Height = 680,
            MinWidth = 520,
            MinHeight = 420,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Margin = new Avalonia.Thickness(24),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = text.ToString(),
                },
            },
        };
        _ = dialog.ShowDialog(this);
    }

    private async void OpenShortcutSettings()
    {
        var returnFocus = FocusManager?.GetFocusedElement();
        var dialog = new ShortcutSettingsWindow(
            _shortcuts,
            draft =>
            {
                var result = _shortcutStore.Save(
                    draft.GetOverrides(), EditorShortcutSettings.KnownCommandIds);
                if (!result.Succeeded) return result.Error ?? "Не удалось сохранить сочетания.";
                _shortcuts.ReplaceWith(draft);
                _shortcutLoadWarning = null;
                return null;
            },
            _shortcutLoadWarning);
        await dialog.ShowDialog(this);
        returnFocus?.Focus();
    }

    private static EditorShortcutSettings LoadShortcuts(
        ShortcutSettingsStore store,
        out string? warning)
    {
        var loaded = store.Load(EditorShortcutSettings.KnownCommandIds);
        var candidate = new EditorShortcutSettings();
        var applied = candidate.ApplyPersisted(loaded.Bindings);
        warning = loaded.Warning;
        if (applied != loaded.Bindings.Count)
        {
            warning = "Часть локальных сочетаний не распознана и заменена стандартными.";
        }

        if (candidate.GetConflicts().Count > 0)
        {
            warning = "В локальных сочетаниях найден конфликт. Используются стандартные; исходный файл не изменён.";
            return new EditorShortcutSettings();
        }

        return candidate;
    }

    private async Task HandleNewShortcutAsync()
    {
        if (_editorView is not null && !await _editorView.Editor.ConfirmCloseAsync())
        {
            return;
        }

        if (_editorView is not null)
        {
            ShowDashboard();
        }

        await NewMapAsync();
    }

    private async Task HandleOpenShortcutAsync()
    {
        if (_editorView is not null && !await _editorView.Editor.ConfirmCloseAsync())
        {
            return;
        }

        if (_editorView is not null)
        {
            ShowDashboard();
        }

        await OpenFileAsync();
    }
}
