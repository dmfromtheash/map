using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Services;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The explanation card beside the asset panel (owner, 2026-11-11, variant 1 «Строки и показ рядом» of
/// https://claude.ai/artifact/PNyW4CtTbatoHGn7wESxgn): the pointer on a row or button opens, to the left of the panel and
/// level with that row, its name, one dry line and a short animation of what it does - frames of the real thing made by
/// <see cref="EditorViewModel.PanelDemo"/> with the chosen asset and the panel's settings and drawn by the program's
/// renderer, the path line, frames and pointer laid over them. It follows the one switch «Подсказки»; with motion off it
/// shows the finished frame. The frames are drawn once per kind and again only when the asset or a setting changes.
/// </summary>
public sealed partial class EditorView
{
    private const int ExplainFrames = 10, ExplainWidth = 300, ExplainHeight = 130;
    private Popup? _explainPopup;
    private TextBlock _explainTitle = null!, _explainText = null!;
    private Image _explainImage = null!;
    private DemoMarksControl _explainMarks = null!;
    private Control? _explainTarget;
    private DispatcherTimer? _explainTimer, _explainHide;
    private int _explainGeneration;
    private readonly Dictionary<PanelDemoKind, (string Signature, Bitmap[] Frames, PanelDemoMarks[] Marks, WorldBounds View)> _explainFrames = [];

    /// <summary>Smoke hook: the kind the card shows now, and how many frames it has drawn.</summary>
    public (PanelDemoKind? Kind, int Frames, bool Open) ExplainStateForSmoke =>
        (_explainKind, _explainKind is { } k && _explainFrames.TryGetValue(k, out var f) ? f.Frames.Length : 0, _explainPopup?.IsOpen == true);
    private PanelDemoKind? _explainKind;
    private readonly List<(Control Target, PanelDemoKind Kind, string Title, string Text, Func<bool>? When)> _explained = [];

    /// <summary>Smoke hook: every explained control that can open its card now.</summary>
    public IReadOnlyList<(Control Target, PanelDemoKind Kind, string Title)> ExplainedForSmoke =>
        [.. _explained.Where(e => e.Target.IsEffectivelyVisible && e.When?.Invoke() != false).Select(e => (e.Target, e.Kind, e.Title))];

    /// <summary>Smoke hook: opens the card of <paramref name="target"/> as the pointer would.</summary>
    public void ShowExplainForSmoke(Control target)
    {
        var entry = _explained.First(e => e.Target == target);
        ShowExplain(entry.Target, entry.Kind, entry.Title, entry.Text);
    }

    /// <summary>Smoke hook: the card itself, to be drawn into a picture.</summary>
    public Control? ExplainCardForSmoke => _explainPopup?.Child;

    /// <summary>Smoke hook: whether the frames of <paramref name="kind"/> for the current settings are drawn.</summary>
    public bool ExplainReadyForSmoke(PanelDemoKind kind) =>
        _explainFrames.TryGetValue(kind, out var f) && f.Signature == _editor.PanelDemoSignature;

    public void HideExplainForSmoke() => HideExplain();

    /// <summary>Smoke hook: holds the open card on frame <paramref name="index"/> of its animation.</summary>
    public void ShowExplainFrameForSmoke(int index)
    {
        if (_explainKind is not { } kind || !_explainFrames.TryGetValue(kind, out var f)) return;
        _explainTimer?.Stop();
        var i = Math.Clamp(index, 0, f.Frames.Length - 1);
        _explainImage.Source = f.Frames[i];
        _explainMarks.Show(f.Marks[i], ExplainWidth / (f.View.Right - f.View.Left), f.View);
    }

    /// <summary>Gives <paramref name="target"/> its card: the pointer or the keyboard on it opens the card.</summary>
    /// <param name="when">Null — always; otherwise the card opens only while it holds (the scope switch explains the
    /// asset tool's halves, not the land brush's).</param>
    private T Explain<T>(T target, PanelDemoKind kind, string title, string text, Func<bool>? when = null) where T : Control
    {
        // The card says it all; the plain tooltip on top of it would repeat it in a second box.
        if (when is null) ToolTip.SetServiceEnabled(target, false);
        _explained.Add((target, kind, title, text, when));
        target.PointerEntered += (_, _) => { if (when?.Invoke() != false) ShowExplain(target, kind, title, text); };
        target.PointerExited += (_, _) => HideExplainSoon(target);
        target.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod == Avalonia.Input.NavigationMethod.Tab && when?.Invoke() != false) ShowExplain(target, kind, title, text);
        };
        target.LostFocus += (_, _) => HideExplainSoon(target);
        target.DetachedFromVisualTree += (_, _) => { if (_explainTarget == target) HideExplain(); };
        return target;
    }

    private Popup ExplainPopup()
    {
        if (_explainPopup is not null) return _explainPopup;
        _explainTitle = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#F0D79A")) };
        _explainText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#C4B294")) };
        _explainImage = new Image { Width = ExplainWidth, Height = ExplainHeight, Stretch = Stretch.Fill };
        _explainMarks = new DemoMarksControl { Width = ExplainWidth, Height = ExplainHeight, IsHitTestVisible = false };
        var picture = new Border
        {
            Width = ExplainWidth, Height = ExplainHeight, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#1A140E")),
            Child = new Grid { Children = { _explainImage, _explainMarks } },
        };
        var card = new Border
        {
            Name = "ExplainCard", Width = ExplainWidth + 22, Padding = new Thickness(10), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#221A14")), BorderBrush = new SolidColorBrush(Color.Parse("#8A6A3A")),
            BorderThickness = new Thickness(1), IsHitTestVisible = false,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #8C000000"),
            Child = new StackPanel { Spacing = 6, Children = { _explainTitle, _explainText, picture } },
        };
        _explainPopup = new Popup
        {
            Child = card, Placement = PlacementMode.LeftEdgeAlignedTop, IsLightDismissEnabled = false,
            PlacementTarget = _contextPanel, IsHitTestVisible = false, Focusable = false,
        };
        ((Panel)_contextFooter).Children.Add(_explainPopup);
        return _explainPopup;
    }

    private void ShowExplain(Control target, PanelDemoKind kind, string title, string text)
    {
        _explainHide?.Stop();
        if (!UiFeel.HintsEnabled || !target.IsEffectivelyVisible || _contextCollapsed) return;
        var popup = ExplainPopup();
        _explainTarget = target; _explainKind = kind;
        _explainTitle.Text = title; _explainText.Text = text;
        // Level with the row: the card's top where the row's top is, never above the panel.
        var y = target.TranslatePoint(new Point(0, 0), _contextPanel)?.Y ?? 0;
        popup.VerticalOffset = Math.Max(0, y - 10);
        popup.HorizontalOffset = -8;
        popup.IsOpen = true;
        PlayExplain(kind);
    }

    private void HideExplainSoon(Control target)
    {
        if (_explainTarget != target) return;
        _explainHide ??= new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) => HideExplain());
        _explainHide.Stop(); _explainHide.Start();
    }

    private void HideExplain()
    {
        _explainHide?.Stop(); _explainTimer?.Stop();
        _explainGeneration++;
        _explainTarget = null; _explainKind = null;
        if (_explainPopup is not null) _explainPopup.IsOpen = false;
    }

    private void PlayExplain(PanelDemoKind kind)
    {
        _explainTimer?.Stop();
        var generation = ++_explainGeneration;
        var signature = _editor.PanelDemoSignature;
        if (_explainFrames.TryGetValue(kind, out var ready) && ready.Signature == signature)
        { Animate(ready.Frames, ready.Marks, ready.View); return; }

        // The documents are made on the UI thread from the live panel; the pixels are drawn off it.
        var frames = Enumerable.Range(0, ExplainFrames).Select(i => _editor.PanelDemo(kind, i, ExplainFrames)).ToArray();
        if (frames.Any(f => f is null))
        {
            _explainImage.Source = null;
            _explainMarks.Show(null, 1, default);
            return;
        }
        var demos = frames.Select(f => f!).ToArray();
        var area = demos[0].View;
        int pw = ExplainWidth * 2, ph = ExplainHeight * 2;
        var scale = pw / (area.Right - area.Left);
        var stamps = _editor.StampImages;
        var marks = demos.Select(d => d.Marks).ToArray();
        _explainMarks.Show(marks[^1], ExplainWidth / (area.Right - area.Left), area);
        UiTasks.Forget(this, () => System.Threading.Tasks.Task.Run(() =>
        {
            // The finished picture first, so the card shows the result at once; the motion follows.
            var last = RenderPreviewPixels(demos[^1].Document, area, pw, ph, scale, stamps);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _explainGeneration && !_explainFrames.ContainsKey(kind)) _explainImage.Source = ToBitmap(last, pw, ph);
            });
            var pixels = demos.Take(demos.Length - 1).Select(d => RenderPreviewPixels(d.Document, area, pw, ph, scale, stamps)).Append(last).ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                var bitmaps = pixels.Select(p => ToBitmap(p, pw, ph)).ToArray();
                if (_explainFrames.Remove(kind, out var old)) foreach (var bitmap in old.Frames) bitmap.Dispose();
                _explainFrames[kind] = (signature, bitmaps, marks, area);
                if (generation == _explainGeneration) Animate(bitmaps, marks, area);
            });
        }));

        void Animate(Bitmap[] bitmaps, PanelDemoMarks[] frameMarks, WorldBounds view)
        {
            var scaleMarks = ExplainWidth / (view.Right - view.Left);
            if (!UiFeel.EffectsEnabled)
            {
                _explainImage.Source = bitmaps[^1]; _explainMarks.Show(frameMarks[^1], scaleMarks, view);
                return;
            }
            var step = 0;
            void Tick()
            {
                // The ten frames, then the finished one held for four more.
                var i = Math.Min(step % (ExplainFrames + 4), ExplainFrames - 1);
                _explainImage.Source = bitmaps[i]; _explainMarks.Show(frameMarks[i], scaleMarks, view);
                step++;
            }
            Tick();
            _explainTimer?.Stop();
            _explainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(240), DispatcherPriority.Background, (_, _) =>
            {
                if (generation != _explainGeneration || _explainPopup?.IsOpen != true) { _explainTimer?.Stop(); return; }
                Tick();
            });
            _explainTimer.Start();
        }
    }

    private void DisposeExplainFrames()
    {
        HideExplain();
        foreach (var (_, frames, _, _) in _explainFrames.Values) foreach (var bitmap in frames) bitmap.Dispose();
        _explainFrames.Clear();
    }

    /// <summary>The marks of a demo frame over its picture: the path line and nodes, frames, the pointer and the note.</summary>
    private sealed class DemoMarksControl : Control
    {
        private PanelDemoMarks? _marks;
        private double _scale = 1;
        private static readonly IPen PathPen = new Pen(new SolidColorBrush(Color.Parse("#E6F0D79A")), 1.6, new DashStyle([4, 3], 0));
        private static readonly IPen NodePen = new Pen(new SolidColorBrush(Color.Parse("#5A4630")), 1.3);
        private static readonly IBrush NodeFill = new SolidColorBrush(Color.Parse("#F6E7C4"));
        private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#E7C982")), 1.4, new DashStyle([4, 3], 0));
        private static readonly IBrush Handle = new SolidColorBrush(Color.Parse("#E7C982"));
        private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#F6E7C4")), 1);
        private static readonly IBrush CursorFill = new SolidColorBrush(Color.Parse("#1A140F"));
        private static readonly IPen ClickPen = new Pen(new SolidColorBrush(Color.Parse("#CCE7C982")), 2);
        private static readonly IBrush NoteBack = new SolidColorBrush(Color.Parse("#DD1A140E"));
        private static readonly IBrush NoteInk = new SolidColorBrush(Color.Parse("#F0D79A"));

        private WorldBounds _view;

        public void Show(PanelDemoMarks? marks, double scale, WorldBounds view) { _marks = marks; _scale = scale; _view = view; InvalidateVisual(); }

        public override void Render(DrawingContext context)
        {
            if (_marks is not { } m) return;
            Point P(VectorPoint v) => new((v.X - _view.Left) * _scale, (v.Y - _view.Top) * _scale);
            if (m.PathNodes.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(P(m.PathNodes[0]), false);
                    foreach (var node in m.PathNodes.Skip(1)) g.LineTo(P(node));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, PathPen, geometry);
            }
            foreach (var node in m.PathNodes) context.DrawEllipse(NodeFill, NodePen, P(node), 3.6, 3.6);
            foreach (var box in m.Selected)
            {
                var r = new Rect((box.Left - _view.Left) * _scale - 2, (box.Top - _view.Top) * _scale - 2,
                    (box.Right - box.Left) * _scale + 4, (box.Bottom - box.Top) * _scale + 4);
                context.DrawRectangle(null, FramePen, r);
                foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    context.FillRectangle(Handle, new Rect(c.X - 2.5, c.Y - 2.5, 5, 5));
            }
            if (m.Cursor is { } cursor)
            {
                var at = P(cursor);
                if (m.Click) context.DrawEllipse(null, ClickPen, at, 9, 9);
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    g.BeginFigure(at, true);
                    foreach (var (x, y) in new[] { (0.0, 13.0), (3.5, 10.0), (6.5, 16.0), (8.5, 15.0), (5.5, 9.0), (10.0, 9.0) })
                        g.LineTo(new Point(at.X + x, at.Y + y));
                    g.EndFigure(true);
                }
                context.DrawGeometry(CursorFill, CursorPen, arrow);
            }
            if (m.Note is { Length: > 0 } note)
            {
                var text = new FormattedText(note, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 11, NoteInk);
                var box = new Rect(Bounds.Width - text.Width - 12, Bounds.Height - text.Height - 8, text.Width + 8, text.Height + 4);
                context.FillRectangle(NoteBack, box, 3);
                context.DrawText(text, new Point(box.X + 4, box.Y + 2));
            }
        }
    }
}
