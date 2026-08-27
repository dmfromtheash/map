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
        var path = UiTestFilePaths.MapPath ?? await new AvaloniaFilePicker(this).PickOpenAsync();
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

        var editor = editorView.Editor;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            editorView.Canvas.IsSpacePanHeld = true;
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.K)
        {
            ShowShortcutHelp();
            e.Handled = true;
            return;
        }

        if (e.PhysicalKey == PhysicalKey.Slash && shift && !control && !alt)
        {
            ShowShortcutHelp();
            e.Handled = true;
            return;
        }

        if (e.PhysicalKey == PhysicalKey.Slash && e.KeyModifiers == KeyModifiers.None)
        {
            editorView.ToggleSidePanel();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.O:
                    // The stamp tool and its library are one thought: the key selects the tool
                    // and opens the panel the tool needs, exactly as it did before it had a tool.
                    editor.ActiveTool = EditorTool.Stamp;
                    editorView.OpenAssetCatalog();
                    e.Handled = true;
                    return;
                case Key.N:
                    editorView.OpenNotes();
                    e.Handled = true;
                    return;
                case Key.F:
                    editorView.OpenAssetCatalog(focusSearch: true);
                    e.Handled = true;
                    return;
                case Key.Tab when editorView.Canvas.IsFocused:
                    editor.SelectPreviousTool();
                    e.Handled = true;
                    return;
                case Key.G:
                    editor.ToggleGridSnap();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    // First press leaves an open group and selects it whole; the next clears.
                    editor.HandleEscape();
                    e.Handled = true;
                    return;
                case Key.Delete:
                case Key.Back:
                    editor.DeleteSelectedObject();
                    e.Handled = true;
                    return;
                case Key.Left:
                    editor.NudgeSelectedObject(-1, 0);
                    e.Handled = true;
                    return;
                case Key.Right:
                    editor.NudgeSelectedObject(1, 0);
                    e.Handled = true;
                    return;
                case Key.Up:
                    editor.NudgeSelectedObject(0, -1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    editor.NudgeSelectedObject(0, 1);
                    e.Handled = true;
                    return;
                case Key.X when editor.ActiveTool == EditorTool.Mask:
                    editor.ToggleMaskPaintMode();
                    e.Handled = true;
                    return;
            }
        }

        if (shift && !control && !alt && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = 10.0;
            editor.NudgeSelectedObject(
                e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.Key is Key.W or Key.S)
        {
            var direction = e.Key == Key.W ? 1 : -1;
            if (editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
                editor.AdjustBrushSize(direction, shift);
            else if (editor.ActiveTool == EditorTool.Text)
                editor.AdjustSelectedTextFontSize(direction, shift);
            else if (editor.ActiveTool == EditorTool.Select)
                editor.ResizeSelectedObject(direction, shift);
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.Key is Key.E or Key.D)
        {
            var direction = e.Key == Key.E ? 1 : -1;
            if (e.Key == Key.E && editorView.IsNotesOpen)
                editorView.OpenNotes(focusEditor: true);
            else if (editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
                editor.AdjustBrushOpacity(direction, shift);
            else if (editor.ActiveTool == EditorTool.Select)
                editor.AdjustSelectedOpacity(direction, shift);
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.Key is Key.Q or Key.A)
        {
            var direction = e.Key == Key.Q ? 1 : -1;
            if (editor.ActiveTool == EditorTool.Stamp)
            {
                editor.AdjustStampArea(direction, shift);
                editorView.RefreshLibraryPanel();
            }
            else if (editor.ActiveTool == EditorTool.Brush)
                editor.AdjustBrushSoftness(direction, shift);
            else if (editor.ActiveTool == EditorTool.Text)
                editor.AdjustSelectedTextCurve(direction, shift);
            e.Handled = true;
            return;
        }

        // ------------------------------------------------------------ stamp context

        if (alt && !control && e.Key is Key.Q or Key.A)
        {
            if (editor.ActiveTool == EditorTool.Stamp)
            {
                editor.AdjustStampDensity(e.Key == Key.Q ? 1 : -1, shift);
                editorView.RefreshLibraryPanel();
                e.Handled = true;
            }

            return;
        }

        if (alt && !control && e.Key is Key.Left or Key.Right)
        {
            if (editor.ActiveTool == EditorTool.Stamp)
            {
                if (e.Key == Key.Right)
                    editor.NextVariant();
                else
                    editor.PreviousVariant();
                editorView.RefreshLibraryPanel();
                e.Handled = true;
            }

            return;
        }

        if (shift && !control && !alt && e.PhysicalKey is PhysicalKey.Period or PhysicalKey.Comma)
        {
            editor.RotateSelectedObject(e.PhysicalKey == PhysicalKey.Period ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.PhysicalKey == PhysicalKey.Backquote)
        {
            editor.RestoreLastSelection();
            e.Handled = true;
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

    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _editorView is { } editorView)
        {
            editorView.Canvas.IsSpacePanHeld = false;
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

        var typing = IsTypingInText();

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

            case Key.Z when _editorView is not null && !typing:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _editorView.Editor.Redo();
                else
                    _editorView.Editor.Undo();
                break;

            case Key.Y when _editorView is not null && !typing:
                _editorView.Editor.Redo();
                break;

            case Key.C when _editorView is not null && !typing:
                _editorView.Editor.CopySelectedComposition();
                break;

            case Key.V when _editorView is not null && !typing:
                _editorView.Editor.PasteComposition();
                break;

            case Key.L when _editorView is not null && !typing:
                _editorView.Editor.ToggleSelectedObjectLock();
                break;

            case Key.A when _editorView is not null && !typing:
                _editorView.Editor.SelectAllByFilter();
                break;

            case Key.G when _editorView is not null && !typing:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _editorView.Editor.UngroupSelection();
                else
                    _ = _editorView.GroupSelectionInteractiveAsync();
                break;

            case Key.Up when _editorView is not null && !typing:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _editorView.Editor.ReorderActiveLayer(up: true);
                else
                    _editorView.Editor.AdjustSelectedSublayer(1);
                break;

            case Key.Down when _editorView is not null && !typing:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _editorView.Editor.ReorderActiveLayer(up: false);
                else
                    _editorView.Editor.AdjustSelectedSublayer(-1);
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
        _ => null,
    };

    private bool IsTypingInText() => FocusManager?.GetFocusedElement() is TextBox;

    private void ShowShortcutHelp()
    {
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
                    Text = """
                    ГЛОБАЛЬНО
                    Колесо — масштаб · средняя кнопка или Пробел+drag — панорама
                    K или ? — эта справка · / — правая панель · G — привязка к сетке
                    Ctrl+Z / Ctrl+Shift+Z — отмена / повтор · Tab — предыдущий инструмент
                    V Выбор · M Маска · B Кисть · O Ассеты/штампы · R Фигура · L Линия · T Текст · N Заметки

                    ВЫБОР
                    Ctrl+A — выбрать всё по поиску и фильтру · Ctrl+клик — добавить/убрать объект
                    Рамка мышью по пустому месту — выделить область · Ctrl+рамка — добавить к выбору
                    Esc — выйти из группы / снять выбор · ` / ~ — вернуть предыдущий выбор
                    Delete / Backspace — удалить всё выделение · стрелки — сдвинуть · Shift+стрелки — крупный шаг
                    W/S — размер · Shift+W/S — крупный шаг · </> — поворот
                    Ctrl+колесо — поворот · Ctrl+Alt+колесо — точный поворот · Shift+колесо — размер
                    Ctrl+C / Ctrl+V — копировать / вставить · Ctrl+L — блокировка выделения
                    Ctrl+G — сгруппировать · Ctrl+Shift+G — разгруппировать
                    Ctrl+↑/↓ — подслой · Ctrl+Shift+↑/↓ — слой

                    КИСТЬ / МАСКА / ТЕКСТ
                    W/S — размер · E/D — непрозрачность · Q/A — мягкость кисти или кривизна текста
                    X — режим маски · F — каталог ассетов · E в заметках — редактирование

                    Shift увеличивает шаг. Сочетания для каталога шрифтов будут добавлены
                    отдельным проверяемым срезом.
                    """,
                },
            },
        };
        _ = dialog.ShowDialog(this);
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
