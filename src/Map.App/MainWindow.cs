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
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Map.App.Controls;
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
    private readonly ScaleTransform _uiScale = new(1, 1);
    private readonly DashboardViewModel _dashboard;
    private readonly MapLibrary _library;
    private readonly DispatcherTimer _autosaveTimer;

    private EditorView? _editorView;

    /// <summary>The catalog gained cards outside the editor's own commands; the open library re-reads it.</summary>
    public void NotifyAssetCatalogChanged()
    {
        if (_editorView is null) return;
        _editorView.Editor.RefreshLibrary();
        _editorView.RefreshLibraryPanel();
    }
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

        // The build time in the title tells at a glance whether the shortcut starts the newest build.
        var built = File.GetLastWriteTime(typeof(MainWindow).Assembly.Location);
        Title = $"Map · {AppVersion.Current} · сборка {built:dd.MM HH:mm}";
        Name = "MainWindow";
        Width = 1024;
        Height = 680;
        MinWidth = 960;
        MinHeight = 640;
        // Every page is laid out for about 1920×1080. On a larger screen the whole interface grows
        // with the window (UpdateUiScale), so a 1440p or 4K monitor is not a small island of
        // controls in a sea of empty space; the user's own scale setting multiplies on top.
        Content = new LayoutTransformControl { Name = "UiScaleHost", LayoutTransform = _uiScale, Child = _host };
        SizeChanged += (_, _) => UpdateUiScale();
        // Inkarnate's editor owns the whole screen; a 1024×680 window left the map a fraction of
        // it. Ordinary starts open maximised (Width/Height above remain the restore size).
        if (!TestMode.StartupSmoke)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            // A test run never opens on the owner's main screen: with a second display it starts there.
            if (TestMode.Enabled && Screens.All.FirstOrDefault(screen => !screen.IsPrimary) is { } second)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(second.WorkingArea.X, second.WorkingArea.Y);
            }
        }
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

        // Every map in this folder (and its folders) is listed on the library page, whether or not
        // it was opened recently; save and open dialogs start here.
        var profileRoot = TestMode.Enabled ? AppPaths.ResolveTestProfileRoot(TestMode.ProfileRootOverride) : AppPaths.Root;
        _library = new MapLibrary(
            _appSettings.MapsRoot ?? (TestMode.Enabled
                ? Path.Combine(profileRoot, "Maps")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Map")),
            Path.Combine(profileRoot, "library.json"));
        _dashboard = new DashboardViewModel(
            _store,
            _recent,
            _log,
            new AvaloniaFilePicker(this, LibraryFolder),
            new DialogPrompts(this),
            new WindowsProjectTrash(),
            appVersion: AppVersion.Current, assetCatalog: _assets, library: _library,
            persistLibraryFolder: path => _appSettings = _appSettingsStore.Save(_appSettings with { MapsRoot = path }));
        _autosaveTimer = new DispatcherTimer
        {
            Interval = TestMode.Enabled
                ? TestMode.AutosaveInterval
                : TimeSpan.FromSeconds(_appSettings.AutosaveIntervalSeconds),
        };
        _autosaveTimer.Tick += OnAutosaveTick;

        // The sound button at the top of the library and the editor saves its choice here.
        UiFeel.PersistSounds = on => _appSettings = _appSettingsStore.Save(_appSettings with { InterfaceSounds = on });
        ApplySettings(_appSettings);

        ShowDashboard();
        Closing += OnClosing;
        ScalingChanged += (_, _) => ApplyCursor();

        // KeyDown is a bubbling-only event in Avalonia, so a tunnel handler would never fire.
        // Listening on the bubble route with handledEventsToo keeps the shortcuts working even
        // when a child control has already marked the key as handled.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The library folder, created on first use; null when the disk refuses (dialogs then open where Windows chooses).</summary>
    private string? LibraryFolder() => _library.EnsureExists() ? _library.Root : null;

    // ---------------------------------------------------------------- navigation

    private void ShowDashboard()
    {
        _autosaveTimer.Stop();
        _editorView = null;
        Map.App.Controls.ApprovedArtwork.PrewarmInBackground();
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
        editor.AtlasMapRequested += async (_, path) =>
        {
            if (await editor.ConfirmCloseAsync()) await OpenPathAsync(path);
        };
        if (editor.IsHexWorkshop)
        {
            editor.HexPlayerViewRequested += (_, _) => new HexPlayerWindow(editor).Show();
            editor.HexChildMapRequested += async (_, hex) =>
            {
                // A child map opens in a window of its own, so the parent map stays where it was.
                if (await editor.PrepareHexChildMapAsync(hex) is { } childPath) OpenInNewWindow(childPath);
            };
        }

        _host.Content = _editorView;
        if (TestMode.AutosaveEnabled)
        {
            _autosaveTimer.Start();
        }

        // Without a focused element inside the window, key input has nowhere to route and every
        // shortcut silently does nothing. The canvas is the natural first owner of the keyboard.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (!OwnedWindows.Any(window => window.IsVisible)) _editorView?.Canvas.Focus();
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Opens a map file in a further Map window (a hex's child map).</summary>
    private void OpenInNewWindow(string path)
    {
        var window = new MainWindow(
            _store, _recent, _log, _assets, _shortcutStore, _shortcuts, _shortcutLoadWarning,
            _appSettingsStore, _appSettings)
        {
            Title = $"Map · {AppVersion.Current} · {System.IO.Path.GetFileNameWithoutExtension(path)}",
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = window.OpenPathAsync(path), Avalonia.Threading.DispatcherPriority.Loaded);
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
            new AvaloniaFilePicker(this, LibraryFolder),
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

    /// <summary>
    /// Opens a document in the workshop of its category. Every road into an editor - a new map,
    /// a file, a recovered copy - passes here, so a future category with its own surface is one
    /// more case, and a category this build lacks is refused instead of opened as standard.
    /// </summary>
    private void OpenInWorkshop(Map.Core.Documents.MapDocument document, string? path, bool startDirty,
        DateTimeOffset? lastSavedUtc = null)
    {
        if (!_dashboard.AcceptsCategory(document))
        {
            ShowDashboard();
            return;
        }

        switch (MapCategories.Find(document.Category)?.Workshop)
        {
            case MapCategories.StandardWorkshop:
            // The hex constructor runs on the shared editor core; the editor builds its own tool
            // set from the document's category, so the two workshops still never mix.
            case MapCategories.HexWorkshop:
                ShowEditor(CreateEditor(document, path, startDirty, lastSavedUtc));
                break;
            default:
                _dashboard.LastError = MapCategories.UnknownCategoryMessage(document.Title, document.Category);
                _log.Error("WORKSHOP_UNAVAILABLE " + document.Category);
                ShowDashboard();
                break;
        }
    }

    private async Task NewMapAsync()
    {
        var document = await WizardWindow.ShowAsync(this, _assets);
        if (document is not null)
        {
            _log.Info("NEW_MAP_CREATED " + document.Category);
            OpenInWorkshop(document, path: null, startDirty: true);
        }
    }

    private async Task OpenFileAsync()
    {
        var path = UiTestFilePaths.MapPath ?? await new AvaloniaFilePicker(this, LibraryFolder).PickOpenAsync();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    internal async Task OpenPathAsync(string path)
    {
        var opened = await _dashboard.TryOpenAsync(path);
        if (opened is not null)
        {
            OpenInWorkshop(opened.Project.Document, opened.Path, startDirty: false,
                lastSavedUtc: opened.Project.ModifiedUtc);
        }
    }

    private Task CloneProjectAsync(RecentCard card) => _dashboard.CloneAsync(card);

    private async Task CreateFromUserTemplateAsync()
    {
        var path = await new AvaloniaFilePicker(this, LibraryFolder).PickOpenAsync();
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

    /// <summary>Map's brass pointer while interface effects are on; the system arrow otherwise.</summary>
    private void ApplyCursor() => Cursor = UiFeel.EffectsEnabled ? BrassCursor.For(RenderScaling) : null;

    private void ApplySettings(AppSettings settings)
    {
        _appSettings = AppSettings.Sanitize(settings);

        // Map has ONE approved palette: since 2026-10-17 the dark "Tavern" one (owner's choice
        // "E"). Fluent's variant must match it, or its own container colours end up underneath
        // Map's text - the light variant once put #10264A text on a #2B2B2B ground (1.03:1). The
        // variant is therefore pinned to the palette's, and App.axaml decides every colour.
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = ThemeVariant.Dark;
        }
        FontSize = 13;
        UpdateUiScale();
        UiFeel.Apply(_appSettings.InterfaceSounds, _appSettings.InterfaceEffects);
        ApplyCursor();
        if (!TestMode.Enabled)
            _autosaveTimer.Interval = TimeSpan.FromSeconds(_appSettings.AutosaveIntervalSeconds);
        if (_editorView is { } editorView)
        {
            editorView.Canvas.PerformanceMode = _appSettings.PerformanceMode;
            ConfigureExportDefaults(editorView.Editor);
        }
    }

    /// <summary>
    /// The scale of the whole interface: 1 up to a 1920×1080 window, then growing with the
    /// smaller of the two ratios at 60 % of the rate (2560×1440 → ×1.16, 3440×1440 → ×1.17,
    /// 3840×2160 → ×1.5 at most), times the scale chosen in the settings.
    /// </summary>
    private void UpdateUiScale()
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        UiScaling.UserScale = _appSettings.UiScale;
        var scale = UiScaling.ForSize(size.Width, size.Height);
        if (Math.Abs(_uiScale.ScaleX - scale) < .004) return;
        _uiScale.ScaleX = scale;
        _uiScale.ScaleY = scale;
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
            OpenInWorkshop(restored.Document, card.Candidate.TargetPath, startDirty: true);
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
        // A field may commit Enter and return focus to the canvas during this same event.
        // Do not also finish the canvas contour when that handled event reaches the window.
        if (e.Handled && e.Key == Key.Enter) return;
        // Application shortcuts never steal editing keys from any text field. The settings dialog
        // is a separate window and captures its own gesture input explicitly.
        // The editor-only commands run through the same routing a smoke run drives with a real
        // key event, so the key path itself is production code and testable outside this window.
        if (_editorView is { } editorForKey && EditorKeyRouting.TryHandleKey(this, editorForKey, _shortcuts, e))
        {
            e.Handled = true;
            return;
        }

        if (EditorKeyRouting.IsTypingInText(this, e) || !ShortcutKeyAdapter.TryCreate(e, out var input))
        {
            return;
        }

        var activeContexts = EditorKeyRouting.Contexts(_editorView, e);
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

    private IReadOnlySet<ShortcutContext> ActiveShortcutContexts() => EditorKeyRouting.Contexts(_editorView);

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
                view.Canvas.Focus();
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
                // A hex map has no asset catalog: its tiles are in the hex palette.
                if (editor.IsHexWorkshop) break;
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
            case EditorShortcutSettings.Commands.River:
                editor.ActiveTool = EditorTool.River;
                break;
            case EditorShortcutSettings.Commands.Road:
                editor.ActiveTool = EditorTool.Road;
                break;
            case EditorShortcutSettings.Commands.Hex:
                editor.ActiveTool = EditorTool.Hex;
                break;
            case EditorShortcutSettings.Commands.Measure when !editor.IsHexWorkshop:
                editor.ActiveTool = EditorTool.Measure;
                break;
            // Escape, Enter and Delete are handled by EditorKeyRouting before this switch runs.
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
                editor.ActiveTool = EditorTool.Stamp;
                view.OpenAssetCatalog(focusSearch: true);
                break;
            case EditorShortcutSettings.Commands.StampNextVariant:
                editor.NextStampFamilyVariant();
                view.RefreshLibraryPanel();
                break;
            case EditorShortcutSettings.Commands.StampPreviousVariant:
                editor.PreviousStampFamilyVariant();
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

    private bool IsTypingInText() => EditorKeyRouting.IsTypingInText(this);

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
