using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Map.App.Services;

namespace Map.App.Controls;

/// <summary>
/// Weight and touch for the commands a person meets first (2026-10-17). Timings were measured
/// frame by frame on the game menu the owner pointed at - ornaments unfold beside the chosen
/// item in ~0.2 s and fold away in ~0.15 s, a screen's items appear one after another - and are
/// rebuilt here with Map's own brass flourish, a springy press and Map's own synthesised sounds.
/// Nothing is copied: no art, no audio, no code.
/// </summary>
public static class Tactile
{
    // Map's own flourish on a 14x14 grid: a curled tail and a cut diamond pointing at the caption.
    private const string FlourishLeft =
        "F0 M0 7 C2.6 4.6 5 4.4 7.2 5.8 L10.2 2.4 L14 7 L10.2 11.6 L7.2 8.2 C5 9.6 2.6 9.4 0 7 Z " +
        "M10.2 5.2 L11.9 7 L10.2 8.8 L8.5 7 Z";
    private const string FlourishRight =
        "F0 M14 7 C11.4 4.6 9 4.4 6.8 5.8 L3.8 2.4 L0 7 L3.8 11.6 L6.8 8.2 C9 9.6 11.4 9.4 14 7 Z " +
        "M3.8 5.2 L2.1 7 L3.8 8.8 L5.5 7 Z";

    private static readonly TransformOperations Rest = TransformOperations.Parse("translateY(0px) scale(1)");
    private static readonly TransformOperations Pressed = TransformOperations.Parse("translateY(1px) scale(0.955)");

    private static IBrush BrassBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F3DDA4"), 0),
            new GradientStop(Color.Parse("#D4AE62"), 0.5),
            new GradientStop(Color.Parse("#9C7838"), 1),
        },
    };

    /// <summary>
    /// Makes a button tactile. With <paramref name="ornaments"/> its caption is framed by two
    /// flourishes that unfold under the pointer or keyboard focus; without them only the press,
    /// the glow and the sounds are added, so a compact button keeps its size.
    /// </summary>
    public static T Attach<T>(T button, bool ornaments = true) where T : Button
    {
        button.Classes.Add("tactile");
        button.RenderTransformOrigin = RelativePoint.Center;
        button.RenderTransform = Rest;

        Path? left = null, right = null;
        if (ornaments && button.Content is Control content)
        {
            left = Flourish(FlourishLeft, +8);
            right = Flourish(FlourishRight, -8);
            button.Content = null;
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { left, content, right },
            };
        }

        void Show(bool on)
        {
            if (left is null || right is null) return;
            var animate = UiFeel.EffectsEnabled;
            foreach (var (path, inward) in new[] { (left, 8.0), (right, -8.0) })
            {
                path.Transitions = animate
                    ? new Transitions
                    {
                        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(on ? 170 : 140), Easing = new CubicEaseOut() },
                        new TransformOperationsTransition
                        {
                            Property = Visual.RenderTransformProperty,
                            Duration = TimeSpan.FromMilliseconds(on ? 280 : 150),
                            Easing = on ? new BackEaseOut() : new CubicEaseIn(),
                        },
                    }
                    : null;
                path.Opacity = on ? 1 : 0;
                path.RenderTransform = TransformOperations.Parse(on
                    ? "translateX(0px) scale(1)"
                    : $"translateX({inward.ToString(System.Globalization.CultureInfo.InvariantCulture)}px) scale(0.3)");
            }
        }

        void Press(bool down)
        {
            if (!UiFeel.EffectsEnabled) { button.Transitions = null; button.RenderTransform = Rest; return; }
            button.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(down ? 70 : 340),
                    Easing = down ? new CubicEaseOut() : new ElasticSpring(),
                },
            };
            button.RenderTransform = down ? Pressed : Rest;
        }

        button.PointerEntered += (_, _) =>
        {
            if (!button.IsEffectivelyEnabled) return;
            Show(true);
        };
        button.PointerExited += (_, _) =>
        {
            if (!button.IsKeyboardFocusWithin) Show(false);
            Press(false);
        };
        button.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) Show(true);
        };
        button.LostFocus += (_, _) => { if (!button.IsPointerOver) Show(false); };
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (button.IsEffectivelyEnabled && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) Press(true);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Press(false),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        button.PointerCaptureLost += (_, _) => Press(false);
        return button;
    }

    private static Path Flourish(string data, double inward) => new()
    {
        Data = Geometry.Parse(data),
        Fill = BrassBrush(),
        Stretch = Stretch.Uniform,
        Width = 15,
        Height = 15,
        Opacity = 0,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = RelativePoint.Center,
        RenderTransform = TransformOperations.Parse(
            $"translateX({inward.ToString(System.Globalization.CultureInfo.InvariantCulture)}px) scale(0.3)"),
        Classes = { "flourish" },
    };

    /// <summary>
    /// Brings a page's items in one after another - a short rise and fade, ~70 ms apart - the way
    /// the studied menu's items appear. Does nothing when interface effects are off.
    /// </summary>
    public static void Reveal(IReadOnlyList<Control> items, int firstDelayMs = 60, int staggerMs = 70)
    {
        if (!UiFeel.EffectsEnabled || items.Count == 0) return;
        foreach (var item in items)
        {
            item.Transitions = null;
            item.Opacity = 0;
            item.RenderTransformOrigin = RelativePoint.Center;
            item.RenderTransform = TransformOperations.Parse("translateY(14px) scale(1)");
        }
        // The clock starts at the first frame the window actually draws, not when the page is
        // built: a page built before the window appears would otherwise finish unseen.
        var anchor = items[0];
        void Begin()
        {
            if (TopLevel.GetTopLevel(anchor) is { } top) top.RequestAnimationFrame(_ => Schedule(items, firstDelayMs, staggerMs));
            else Schedule(items, firstDelayMs, staggerMs);
        }
        if (anchor.IsLoaded) Begin();
        else
        {
            void OnLoaded(object? sender, RoutedEventArgs e) { anchor.Loaded -= OnLoaded; Begin(); }
            anchor.Loaded += OnLoaded;
        }
    }

    private static void Schedule(IReadOnlyList<Control> items, int firstDelayMs, int staggerMs)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            DispatcherTimer.RunOnce(() =>
            {
                item.Transitions = new Transitions
                {
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(380), Easing = new CubicEaseOut() },
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(460), Easing = new QuinticEaseOut() },
                };
                item.Opacity = 1;
                item.RenderTransform = Rest;
            }, TimeSpan.FromMilliseconds(firstDelayMs + i * staggerMs));
        }
    }

    /// <summary>A soft spring for the release: overshoots ~4 % once and settles.</summary>
    private sealed class ElasticSpring : Easing
    {
        public override double Ease(double progress)
        {
            if (progress >= 1) return 1;
            return 1 - Math.Exp(-6.5 * progress) * Math.Cos(progress * Math.PI * 2.4);
        }
    }
}
