using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Persistence;
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
    private readonly ContentControl _host = new() { Name = "ViewHost" };
    private readonly DashboardViewModel _dashboard;
    private readonly DispatcherTimer _autosaveTimer;

    private EditorView? _editorView;
    private bool _autosaveTickRunning;

    public MainWindow(IProjectStore store, IRecentMaps recent, IAppLog log, AssetCatalog assets)
    {
        _store = store;
        _recent = recent;
        _log = log;
        _assets = assets;

        Title = "Map";
        Name = "MainWindow";
        Width = 1400;
        Height = 900;
        MinWidth = 960;
        MinHeight = 640;
        Content = _host;

        _dashboard = new DashboardViewModel(_store, _recent, _log);
        _autosaveTimer = new DispatcherTimer { Interval = TestMode.AutosaveInterval };
        _autosaveTimer.Tick += OnAutosaveTick;

        ShowDashboard();
        Closing += OnClosing;

        // KeyDown is a bubbling-only event in Avalonia, so a tunnel handler would never fire.
        // Listening on the bubble route with handledEventsToo keeps the shortcuts working even
        // when a child control has already marked the key as handled.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
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
            restoreRequested: RestoreAsync);

        _host.Content = view;
        view.Refresh();
    }

    private void ShowEditor(EditorViewModel editor)
    {
        _autosaveTimer.Stop();
        _editorView = new EditorView(editor, requestBack: LeaveEditorAsync, requestSecondWindow: OpenSecondWindow);
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
        var window = new MainWindow(_store, _recent, _log, _assets) { Title = "Map · второе окно" };
        window.Show();
    }

    private EditorViewModel CreateEditor(Map.Core.Documents.MapDocument document, string? path, bool startDirty) =>
        new(
            document,
            path,
            startDirty,
            _store,
            _recent,
            new AvaloniaFilePicker(this),
            new DialogPrompts(this),
            _log,
            assetCatalog: _assets);

    private async Task NewMapAsync()
    {
        var document = await WizardWindow.ShowAsync(this);
        if (document is not null)
        {
            _log.Info($"New map \"{document.Title}\" {document.Canvas.Width}x{document.Canvas.Height}.");
            ShowEditor(CreateEditor(document, path: null, startDirty: true));
        }
    }

    private async Task OpenFileAsync()
    {
        var path = await new AvaloniaFilePicker(this).PickOpenAsync();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private Task OpenPathAsync(string path)
    {
        var opened = _dashboard.TryOpen(path);
        if (opened is not null)
        {
            ShowEditor(CreateEditor(opened.Document, path, startDirty: false));
        }

        return Task.CompletedTask;
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
        if (_closeApproved || _editorView is null)
        {
            return;
        }

        e.Cancel = true;
        if (await _editorView.Editor.ConfirmCloseAsync())
        {
            _autosaveTimer.Stop();
            _closeApproved = true;
            Close();
        }
    }

    // ---------------------------------------------------------------- shortcuts

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editorView is not { } editorView || IsTypingInText())
        {
            return;
        }

        // One key per tool, matching the tooltips on the rail. Text entry is excluded above so
        // typing a Russian or Latin letter into a name field can never switch the tool.
        if (e.KeyModifiers == KeyModifiers.None && ToolForKey(e.Key) is { } tool)
        {
            editorView.Editor.ActiveTool = tool;
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, editorView.Editor.Shortcuts.Get("grid-snap")))
        {
            editorView.Editor.SnapSelectedObjectToGrid();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                _ = HandleNewShortcutAsync();
                break;

            case Key.O:
                _ = HandleOpenShortcutAsync();
                break;

            case Key.S when _editorView is not null:
                _ = _editorView.Editor.SaveAsync();
                break;

            case Key.Z when _editorView is not null:
                _editorView.Editor.Undo();
                break;

            case Key.Y when _editorView is not null:
                _editorView.Editor.Redo();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>The rail's single-key bindings; null for any key that is not a tool.</summary>
    private static EditorTool? ToolForKey(Key key) => key switch
    {
        Key.V => EditorTool.Select,
        Key.L => EditorTool.Line,
        Key.R => EditorTool.Shape,
        Key.T => EditorTool.Text,
        Key.B => EditorTool.Brush,
        Key.M => EditorTool.Mask,
        Key.E => EditorTool.Land,
        _ => null,
    };

    private bool IsTypingInText() => FocusManager?.GetFocusedElement() is TextBox;

    private static bool MatchesShortcut(KeyEventArgs e, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key) || key != e.Key) return false;
        var expected = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            expected |= part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "SHIFT" => KeyModifiers.Shift,
                "ALT" => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };
        }
        return e.KeyModifiers == expected;
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
