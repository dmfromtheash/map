using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Controls;
using Map.App.Services;
using Map.Presentation.ViewModels;
using MapDocument = Map.Core.Documents.MapDocument;
using WorldBounds = Map.Core.Documents.WorldBounds;

namespace Map.App.Views;

/// <summary>
/// The create button of a tool, at the very top of its panel (owner, 2026-10-31, variant 5 with the picture of
/// variant 4 of https://claude.ai/artifact/W7YjnwxaAjvnAoh9qVpMJi): a bright cloud «Нажми сюда, чтобы создать…»
/// fixed to the button, two rings spreading from it, and inside the button a small picture where the river draws
/// itself, the island grows, the road is laid. The hints are switched off together in the settings or by the cloud's
/// «×» (<see cref="UiFeel.HintsEnabled"/>); motion follows <see cref="UiFeel.EffectsEnabled"/>, plays about nine seconds
/// after the panel opens and again while the pointer comes to the button, and stops with the panel: each animation frame
/// repaints the window, the map included.
/// </summary>
public sealed partial class EditorView
{
    private static readonly Color HintGold = Color.Parse("#F0D79A");
    private static readonly Color HintInk = Color.Parse("#2A1D0C");

    /// <param name="makes">The end of «Нажми сюда, чтобы создать …»; null — the button gets no cloud of its own (a second
    /// create button under the first one, as the lake under the river).</param>
    private StackPanel CreateHero(string name, string title, string hint, string? makes, CreateDemoKind kind,
        MapIcons.SolidGlyph glyph, Action action)
    {
        var button = HeroButton(name, title, hint, glyph, action);
        var picture = DemoPicture(kind, out var animate);
        // The picture sits inside the button above its title: the button says what it does by showing it.
        var content = (Grid)button.Content!;
        button.Content = null; // the grid leaves the button before it joins the new stack
        button.Content = new StackPanel { Spacing = 8, Children = { picture, content } };
        return HintAround(name, button, makes is null ? null : "Нажми сюда, чтобы создать " + makes, picture, animate);
    }

    /// <summary>
    /// The hint around any create button: the cloud with its «×» (when <paramref name="text"/> is given), the two rings
    /// and the motion of <paramref name="picture"/>. Everything follows the one switch «Подсказки».
    /// </summary>
    private static StackPanel HintAround(string name, Button button, string? text, Control? picture = null,
        Action<CancellationToken>? animate = null) => HintAround(name, button, text, out _, picture, animate);

    /// <param name="cloudText">The cloud's words, for a button whose action changes (the asset panel's one button).</param>
    private static StackPanel HintAround(string name, Button button, string? text, out TextBlock cloudText,
        Control? picture = null, Action<CancellationToken>? animate = null)
    {
        // A plain mark, not a themed button: the theme's button fill hid the «×» on the gold cloud.
        var close = new Border
        {
            Name = name + "HintClose", Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(4, 0, 2, 0),
            Background = new SolidColorBrush(Color.Parse("#33000000")), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Focusable = true,
            Child = new TextBlock
            {
                Text = "×", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(HintInk),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0),
            },
        };
        AutomationProperties.SetName(close, "Убрать подсказки «Нажми сюда»");
        ToolTip.SetTip(close, "Убрать все подсказки «Нажми сюда». Вернуть: Настройки → Интерфейс → Подсказки.");
        close.PointerReleased += (_, _) => UiFeel.SetHints(false, persist: true);
        close.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space) UiFeel.SetHints(false, persist: true); };
        cloudText = new TextBlock
        {
            Name = name + "HintText", Text = text ?? "", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            FontWeight = FontWeight.Bold, FontSize = 14, Foreground = new SolidColorBrush(HintInk), VerticalAlignment = VerticalAlignment.Center,
        };
        var cloud = new StackPanel
        {
            Name = name + "Hint",
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(HintGold), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 4, 7),
                    Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { cloudText, close } },
                },
                // The tail of the cloud points at the button.
                new Path
                {
                    Data = Geometry.Parse("M0,0 L22,0 L11,11 Z"), Fill = new SolidColorBrush(HintGold),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
        Grid.SetColumn(close, 1);

        Border Ring() => new()
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(HintGold),
            IsHitTestVisible = false, Opacity = 0, Margin = button.Margin, // the ring lies on the button's own edge
        };
        var ring1 = Ring(); var ring2 = Ring();
        var holder = new Grid { Children = { ring1, ring2, button } };
        var hero = new StackPanel { Name = name + "Hero", Spacing = 2, Margin = new Thickness(0, 0, 0, 6), Children = { cloud, holder } };

        CancellationTokenSource? running = null;
        void Stop()
        {
            running?.Cancel(); running?.Dispose(); running = null;
            ring1.Opacity = ring2.Opacity = 0;
            cloud.RenderTransform = null;
        }
        void Follow()
        {
            var hints = UiFeel.HintsEnabled;
            cloud.IsVisible = hints && text is not null;
            if (picture is not null) picture.IsVisible = hints;
            Stop();
            if (!hints || !UiFeel.EffectsEnabled || TopLevel.GetTopLevel(hero) is null) return;
            running = new CancellationTokenSource();
            var token = running.Token;
            try
            {
                var lift = new TranslateTransform();
                cloud.RenderTransform = lift;
                _ = AnimateAsync(cloud, TranslateTransform.YProperty, 1.6, new SineEaseInOut(), [(0, 0), (.5, -4), (1, 0)], token);
                _ = WavesAsync(ring1, TimeSpan.Zero, token);
                _ = WavesAsync(ring2, TimeSpan.FromSeconds(1), token);
                animate?.Invoke(token);
                _ = RestAsync(token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
            {
                // Motion is decoration: a renderer that cannot animate leaves the still hint, never a broken panel.
                Stop();
            }
        }
        // Every animation frame repaints the window, the map with it: three rounds catch the eye, then the hint rests
        // in its finished picture until the pointer comes back to it.
        async System.Threading.Tasks.Task RestAsync(CancellationToken token)
        {
            try { await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(9), token); }
            catch (OperationCanceledException) { return; }
            Stop();
        }
        hero.PointerEntered += (_, _) => { if (running is null) Follow(); };
        void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Follow);
        hero.AttachedToVisualTree += (_, _) => { UiFeel.Changed += OnChanged; Follow(); };
        // A static event holds whoever listens: leave it with the panel (the closed editor leak of 2026-10-31).
        hero.DetachedFromVisualTree += (_, _) => { UiFeel.Changed -= OnChanged; Stop(); };
        Follow();
        return hero;
    }

    /// <summary>The tool's panel with its create button above the tabs.</summary>
    private static StackPanel WithCreateHero(Control hero, Control tabs) => new StackPanel { Spacing = 4, Children = { hero, tabs } };

    /// <summary>
    /// One number of <paramref name="target"/> through its frames, endlessly, until cancelled. Only plain numbers are
    /// animated (a transform object's X/Y/scale, opacity, a dash offset): Avalonia 12 has no key-frame animator for
    /// RenderTransform itself.
    /// </summary>
    private static System.Threading.Tasks.Task AnimateAsync(Animatable target, AvaloniaProperty property, double seconds, Easing easing,
        (double Cue, double Value)[] frames, CancellationToken token)
    {
        var animation = new Animation { Duration = TimeSpan.FromSeconds(seconds), IterationCount = IterationCount.Infinite, Easing = easing };
        foreach (var (cue, value) in frames)
            animation.Children.Add(new KeyFrame { Cue = new Cue(cue), Setters = { new Setter(property, value) } });
        return animation.RunAsync(target, token);
    }

    /// <summary>A ring leaving the button and fading, every two seconds.</summary>
    private static async System.Threading.Tasks.Task WavesAsync(Border ring, TimeSpan delay, CancellationToken token)
    {
        try { await System.Threading.Tasks.Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
        var grow = new ScaleTransform();
        ring.RenderTransform = grow;
        var ease = new CubicEaseOut();
        try
        {
            await System.Threading.Tasks.Task.WhenAll(
                AnimateAsync(ring, OpacityProperty, 2, ease, [(0, .8), (1, 0)], token),
                AnimateAsync(ring, ScaleTransform.ScaleXProperty, 2, ease, [(0, 1), (1, 1.07)], token),
                AnimateAsync(ring, ScaleTransform.ScaleYProperty, 2, ease, [(0, 1), (1, 1.25)], token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or InvalidCastException)
        {
            ring.Opacity = 0; // decoration only: a ring that cannot move simply stays hidden
        }
    }

    private const int DemoFrames = 8;
    private readonly Dictionary<CreateDemoKind, (string Signature, Bitmap[] Frames)> _demoFrames = [];

    /// <summary>
    /// The live picture of a create button: frames of the real thing being made (owner, 2026-10-31: «1 в 1 всё как в
    /// настоящем использовании»), built by <see cref="EditorViewModel.CreateDemoFrame"/> on this map's page, sea, style
    /// and light and drawn by the program's renderer (<see cref="RenderPreviewPixels"/>). The frames are drawn once in
    /// the background and again only when what they are drawn with changes; the motion shows a frame every 220 ms, so it
    /// repaints the window a few times a second, not sixty. Still on the finished frame when motion is off.
    /// </summary>
    private Border DemoPicture(CreateDemoKind kind, out Action<CancellationToken> animate)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var holder = new Border
        {
            Height = 64, CornerRadius = new CornerRadius(8), ClipToBounds = true, IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.Parse("#1A140E")), Child = image,
        };
        void Show(Bitmap[] frames) => image.Source = frames[^1];
        void Prepare()
        {
            var signature = _editor.CreateDemoSignature;
            if (_demoFrames.TryGetValue(kind, out var ready) && ready.Signature == signature) { Show(ready.Frames); return; }
            // The documents are made on the UI thread from the live map; the pixels are drawn off it.
            var documents = Enumerable.Range(0, DemoFrames).Select(i => _editor.CreateDemoFrame(kind, i, DemoFrames)).ToArray();
            var stamps = _editor.StampImages;
            var area = new WorldBounds(0, 0, EditorViewModel.CreateDemoWidth, EditorViewModel.CreateDemoHeight);
            UiTasks.Forget(this, () => System.Threading.Tasks.Task.Run(() =>
            {
                byte[] Draw(MapDocument d) => RenderPreviewPixels(d, area, EditorViewModel.CreateDemoWidth, EditorViewModel.CreateDemoHeight, 1, stamps);
                // The finished picture first, so the button shows what it makes at once; the growth follows.
                var finished = Draw(documents[^1]);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_demoFrames.ContainsKey(kind) || image.Source is not null) return;
                    image.Source = ToBitmap(finished, EditorViewModel.CreateDemoWidth, EditorViewModel.CreateDemoHeight);
                });
                var pixels = documents.Take(documents.Length - 1).Select(Draw).Append(finished).ToArray();
                Dispatcher.UIThread.Post(() =>
                {
                    var frames = pixels.Select(p => ToBitmap(p, EditorViewModel.CreateDemoWidth, EditorViewModel.CreateDemoHeight)).ToArray();
                    if (_demoFrames.Remove(kind, out var old)) foreach (var bitmap in old.Frames) bitmap.Dispose();
                    _demoFrames[kind] = (signature, frames);
                    Show(frames);
                });
            }));
        }
        holder.AttachedToVisualTree += (_, _) => Prepare();
        animate = token =>
        {
            if (!_demoFrames.TryGetValue(kind, out var ready)) return;
            var frames = ready.Frames;
            var step = 0;
            // Eight frames of growth, then the finished map held for four more.
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background, (_, _) =>
            {
                image.Source = frames[Math.Min(step % (DemoFrames + 4), DemoFrames - 1)];
                step++;
            });
            timer.Start();
            token.Register(() => Dispatcher.UIThread.Post(() => { timer.Stop(); image.Source = frames[^1]; }));
        };
        return holder;
    }
}
