using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    private EditorView? _editorView;

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
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
        Content = _host;

        _dashboard = new DashboardViewModel(_store, _recent, _log);

        ShowDashboard();
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- navigation

    private void ShowDashboard()
    {
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
        _editorView = new EditorView(editor, requestBack: LeaveEditorAsync);
        _host.Content = _editorView;
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
            _closeApproved = true;
            Close();
        }
    }

    // ---------------------------------------------------------------- shortcuts

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_editorView is { } editorView)
        {
            var shortcuts = editorView.Editor.Shortcuts;
            if (MatchesShortcut(e, shortcuts.Get("select")))
            {
                editorView.Editor.ActiveTool = EditorTool.Select; e.Handled = true; return;
            }
            if (MatchesShortcut(e, shortcuts.Get("text")))
            {
                editorView.Editor.AddText(); e.Handled = true; return;
            }
            if (MatchesShortcut(e, shortcuts.Get("grid-snap")))
            {
                editorView.Editor.SnapSelectedObjectToGrid(); e.Handled = true; return;
            }
        }

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
