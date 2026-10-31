using System;
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
using Map.App.Controls;
using Map.App.Services;

namespace Map.App.Views;

/// <summary>What a create button makes: the live picture inside it draws exactly that.</summary>
internal enum CreateHeroKind { River, Land, Road }

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

    private static StackPanel CreateHero(string name, string title, string hint, string makes, CreateHeroKind kind,
        MapIcons.SolidGlyph glyph, Action action)
    {
        var button = HeroButton(name, title, hint, glyph, action);
        var picture = HeroPicture(kind, out var animate);
        // The picture sits inside the button above its title: the button says what it does by showing it.
        var content = (Grid)button.Content!;
        button.Content = null; // the grid leaves the button before it joins the new stack
        button.Content = new StackPanel { Spacing = 8, Children = { picture, content } };

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
        var cloudText = new TextBlock
        {
            Text = "Нажми сюда, чтобы создать " + makes, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
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
            IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(0, 4, 0, 6),
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
            cloud.IsVisible = hints;
            picture.IsVisible = hints;
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
                animate(token);
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

    /// <summary>
    /// The small picture of what the button makes, on the paper colour of the owner's maps: the river and the road
    /// draw themselves along their line, the island grows from a speck. Still (finished) when motion is off.
    /// </summary>
    private static Border HeroPicture(CreateHeroKind kind, out Action<CancellationToken> animate)
    {
        var canvas = new Canvas { Width = 300, Height = 80 };
        Shape line;
        switch (kind)
        {
            case CreateHeroKind.Land:
                line = new Path
                {
                    Data = Geometry.Parse("M50,52 C58,26 96,24 116,16 S178,6 200,24 S250,52 222,64 S150,72 120,68 S44,78 50,52 Z"),
                    Fill = new SolidColorBrush(Color.Parse("#B9A26E")), Stroke = new SolidColorBrush(Color.Parse("#6B5A35")), StrokeThickness = 3,
                    RenderTransformOrigin = new RelativePoint(.5, .55, RelativeUnit.Relative),
                };
                break;
            case CreateHeroKind.Road:
                line = new Path
                {
                    Data = Geometry.Parse("M10,70 L70,46 L130,54 L200,24 L290,14"), Stroke = new SolidColorBrush(Color.Parse("#8A5A2B")),
                    StrokeThickness = 6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                };
                break;
            default:
                line = new Path
                {
                    Data = Geometry.Parse("M8,18 C60,8 52,56 104,46 S176,24 204,64 S262,58 292,72"), Stroke = new SolidColorBrush(Color.Parse("#3A7FAE")),
                    StrokeThickness = 7, StrokeLineCap = PenLineCap.Round,
                };
                break;
        }
        canvas.Children.Add(line);
        const double length = 70; // the line's length in stroke widths: the dash that uncovers it
        if (kind != CreateHeroKind.Land) line.StrokeDashArray = [length, length];
        animate = token =>
        {
            var ease = new CubicEaseInOut();
            if (kind == CreateHeroKind.Land)
            {
                var grow = new ScaleTransform();
                line.RenderTransform = grow;
                _ = AnimateAsync(line, ScaleTransform.ScaleXProperty, 3, ease, [(0, .15), (.6, 1), (1, 1)], token);
                _ = AnimateAsync(line, ScaleTransform.ScaleYProperty, 3, ease, [(0, .15), (.6, 1), (1, 1)], token);
                _ = AnimateAsync(line, OpacityProperty, 3, ease, [(0, .2), (.6, 1), (1, 1)], token);
                return;
            }
            _ = AnimateAsync(line, Shape.StrokeDashOffsetProperty, 3, ease, [(0, length), (.6, 0), (1, 0)], token);
        };
        return new Border
        {
            Height = 64, CornerRadius = new CornerRadius(8), ClipToBounds = true, IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.Parse("#E9DCC0")),
            Child = new Viewbox { Stretch = Stretch.Fill, Child = canvas },
        };
    }
}
