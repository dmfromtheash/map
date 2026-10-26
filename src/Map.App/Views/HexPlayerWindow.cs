using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Views;

/// <summary>
/// The players' view of a hex map (Hex Kit, Hextml, Hexer): the same document the game master
/// edits, drawn with an opaque fog of war and only the keys the players may read, in its own
/// window that can sit on a second screen. It follows every edit of the master; it never edits.
/// </summary>
public sealed class HexPlayerWindow : Window
{
    private readonly EditorViewModel _editor;
    private readonly HexPlayerCanvas _canvas;
    private readonly StackPanel _knowledge = new() { Name = "HexPlayerKnowledgeList", Spacing = 10, Margin = new Thickness(14) };

    public HexPlayerWindow(EditorViewModel editor)
    {
        _editor = editor;
        Name = "HexPlayerWindow";
        Title = $"Map · {editor.Session.Document.Title} · окно игрока";
        Width = 1280;
        Height = 820;
        MinWidth = 480;
        MinHeight = 320;
        _canvas = new HexPlayerCanvas(editor);
        var fit = Ui.TextButton("HexPlayerFitButton", "Вся карта", _canvas.Fit, "ghost");
        var follow = new ToggleButton { Content = "Следовать за мастером", IsChecked = false }.Named("HexPlayerFollowToggle").Classed("compact");
        follow.IsCheckedChanged += (_, _) => _canvas.FollowMaster = follow.IsChecked == true;
        var hint = new TextBlock
        {
            Text = "Колесо — приближение, перетаскивание — перемещение. Туман и ключи — как видят игроки.",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        }.Classed("faint");
        Content = Ui.WorkspaceBackdrop(new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(12, 8),
                    Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { fit, follow, hint } },
                }.Classed("surface-raised").Docked(Dock.Top),
                new ScrollViewer { Name = "HexPlayerKnowledgePanel", Width = 300, Content = _knowledge }.Docked(Dock.Right),
                _canvas,
            },
        });
        Opened += (_, _) => Dispatcher.UIThread.Post(_canvas.Fit, DispatcherPriority.Loaded);
        editor.SceneChanged += RefreshKnowledge;
        ((INotifyPropertyChanged)editor).PropertyChanged += KnowledgeChanged;
        RefreshKnowledge(null, EventArgs.Empty);
        Closed += (_, _) =>
        {
            editor.SceneChanged -= RefreshKnowledge;
            ((INotifyPropertyChanged)editor).PropertyChanged -= KnowledgeChanged;
            _canvas.Detach();
        };
    }
    private void KnowledgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.CanUndo) or nameof(EditorViewModel.HexLevel)) RefreshKnowledge(sender, EventArgs.Empty);
    }

    private void RefreshKnowledge(object? sender, EventArgs e)
    {
        _knowledge.Children.Clear();
        void Add(string text, bool title = false) => _knowledge.Children.Add(new TextBlock
        { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = title ? FontWeight.SemiBold : FontWeight.Normal });
        Add("Карта знаний", true);
        Add("Известные места и слухи. Сведения могут оказаться неточными.");
        // R27: a painted map tells the players its notes and cards meant for them, of what they have seen.
        var players = PlayerScene.Sanitize(_editor.Session.Document);
        foreach (var note in players.Notes)
        {
            Add(note.Title, true);
            if (note.Markdown.Length > 0) Add(note.Markdown);
        }
        foreach (var card in players.Entities)
        {
            Add(card.Name + " · " + MapEntities.KindTitle(card.Kind), true);
            if (card.Markdown.Length > 0) Add(card.Markdown);
        }
        foreach (var source in _editor.Session.Document.Layers.OfType<HexLayer>().Where(l => l.Visible && l.Level == _editor.HexLevelShown))
        {
            var layer = ExportPlayerVisibility.Sanitize(source);
            foreach (var r in layer.Regions)
            {
                Add(r.Name, true);
                if (r.Rumor.Length > 0) Add("Слух: " + r.Rumor);
            }
            foreach (var (hex, note) in layer.Notes.OrderBy(p => layer.Board.Layout.ToOffset(p.Key).Row).ThenBy(p => layer.Board.Layout.ToOffset(p.Key).Column))
            {
                Add(HexLayerRenderer.NumberOf(layer.Board, layer.Board.Layout.ToOffset(hex)) + " · " + note.Title, true);
                Add(note.PlayerText);
            }
            foreach (var (_, cell) in layer.Board.Cells)
                foreach (var p in cell.PointsOfInterest.Where(p => p.Name.Length > 0 || p.Rumor.Length > 0))
                {
                    Add(p.Name, true);
                    if (p.Rumor.Length > 0) Add("Слух: " + p.Rumor);
                }
        }
    }
}

/// <summary>Draws the players' view on the CPU into a bitmap; redrawn after edits and view changes.</summary>
internal sealed class HexPlayerCanvas : Control
{
    private readonly EditorViewModel _editor;
    private readonly Viewport _viewport = new();
    private readonly HexChunkCache _cache = new() { BuildInBackground = true };
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private MapDocument? _source, _players;
    private bool _dirty = true;
    private Point? _drag;

    public HexPlayerCanvas(EditorViewModel editor)
    {
        _editor = editor;
        ClipToBounds = true;
        Focusable = true;
        ((INotifyPropertyChanged)editor).PropertyChanged += OnEditorChanged;
        editor.SceneChanged += OnSceneChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

    /// <summary>The players' window shows the part of the map the master is looking at.</summary>
    public bool FollowMaster { get; set; }

    public void Detach()
    {
        _timer.Stop();
        ((INotifyPropertyChanged)_editor).PropertyChanged -= OnEditorChanged;
        _editor.SceneChanged -= OnSceneChanged;
        _cache.Dispose();
        _bitmap?.Dispose();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.CanUndo) or nameof(EditorViewModel.CanRedo) or nameof(EditorViewModel.HexLevel)) _dirty = true;
    }

    private void OnSceneChanged(object? sender, EventArgs e) => _dirty = true;

    public void Fit()
    {
        var document = _editor.Session.Document;
        _viewport.Resize(Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        _viewport.FitTo(document.Canvas.Width, document.Canvas.Height);
        _dirty = true;
    }

    private void Refresh()
    {
        if (FollowMaster)
        {
            var master = _editor.Session.Viewport;
            var (cx, cy) = master.ScreenToWorld(master.ViewWidth / 2, master.ViewHeight / 2);
            var scale = master.Scale;
            var x = cx - Bounds.Width / (2 * scale);
            var y = cy - Bounds.Height / (2 * scale);
            if (Math.Abs(x - _viewport.OffsetX) > 0.5 || Math.Abs(y - _viewport.OffsetY) > 0.5 || Math.Abs(scale - _viewport.Scale) > 1e-6)
            {
                _viewport.Set(x, y, scale);
                _dirty = true;
            }
        }

        if (!_dirty || Bounds.Width < 1 || Bounds.Height < 1) return;
        _dirty = false;
        Draw();
        InvalidateVisual();
    }

    private void Draw()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        _viewport.Resize(Bounds.Width, Bounds.Height);
        using var locked = _bitmap.Lock();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x22, 0x20, 0x26));
        canvas.Scale((float)scaling);
        canvas.Scale((float)_viewport.Scale);
        canvas.Translate((float)-_viewport.OffsetX, (float)-_viewport.OffsetY);
        // R27: the players' copy of the map - labels and notes under the fog left out.
        var source = _editor.Session.Document;
        if (!ReferenceEquals(source, _source)) { _source = source; _players = PlayerScene.Sanitize(source, sanitizeHex: false); }
        var document = _players!;
        var (left, top) = _viewport.ScreenToWorld(0, 0);
        var (right, bottom) = _viewport.ScreenToWorld(Bounds.Width, Bounds.Height);
        // Only the hexes and labels of the shown level: notes, the master's drawings and other
        // levels stay with the master.
        var shown = _editor.HexLevelShown;
        SceneRenderer.Render(canvas, document, new Map.Rendering.RenderOptions
        {
            ViewScale = _viewport.Scale * scaling,
            HexChunks = _cache,
            HexPlayerView = true,
            HexPresentation = HexPresentation.PlayerKnowledge,
            HexLevel = shown,
            VisibleWorld = new WorldBounds(left, top, right, bottom),
            RequestRefinedFrame = () => _dirty = true,
        });
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null) return;
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), new Rect(Bounds.Size));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _dirty = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        _viewport.ZoomAt(point.X, point.Y, e.Delta.Y > 0 ? 1.2 : 1 / 1.2);
        _dirty = true;
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } last) return;
        var point = e.GetPosition(this);
        _viewport.PanByScreenDelta(point.X - last.X, point.Y - last.Y);
        _drag = point;
        _dirty = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }
}
