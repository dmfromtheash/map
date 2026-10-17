using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Map.App.Services;

/// <summary>
/// Sound on everything, wired once for the whole application (2026-10-17) instead of per
/// control: every button answers the pointer and the press, a switch or tick box chimes up or
/// down, a slider ticks as it moves, a list breathes as it opens and closes, a menu command
/// presses. The sound button itself stays silent when it turns sound off.
/// </summary>
public static class UiSoundHooks
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Button.ClickEvent.AddClassHandler<Button>(OnClick, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.PointerEnteredEvent.AddClassHandler<Button>(OnButtonEntered);
        InputElement.PointerEnteredEvent.AddClassHandler<MenuItem>(OnMenuEntered);
        InputElement.PointerEnteredEvent.AddClassHandler<ComboBoxItem>((_, _) => UiSound.PlayHover());
        MenuItem.ClickEvent.AddClassHandler<MenuItem>(OnMenuClick, RoutingStrategies.Bubble, handledEventsToo: true);
        RangeBase.ValueProperty.Changed.AddClassHandler<Slider>(OnSliderValue);
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>(OnDropDown);
    }

    private static bool IsScrollPart(Visual control) => control.FindAncestorOfType<ScrollBar>() is not null;

    private static void OnClick(Button button, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, button) || button.Classes.Contains("sound-toggle")) return;
        if (button is RepeatButton)
        {
            // slider track and spinner steps tick; scrollbar arrows stay quiet
            if (!IsScrollPart(button)) UiSound.PlayTick(0.5);
            return;
        }
        if (button is ToggleButton toggle) UiSound.PlayToggle(toggle.IsChecked == true);
        else UiSound.PlayPress();
    }

    private static void OnButtonEntered(Button button, PointerEventArgs e)
    {
        if (!button.IsEffectivelyEnabled || button is RepeatButton || IsScrollPart(button)) return;
        UiSound.PlayHover();
    }

    private static void OnMenuEntered(MenuItem item, PointerEventArgs e)
    {
        if (item.IsEffectivelyEnabled && item.Header is not null) UiSound.PlayHover();
    }

    private static void OnMenuClick(MenuItem item, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, item)) UiSound.PlayPress();
    }

    private static void OnSliderValue(Slider slider, AvaloniaPropertyChangedEventArgs e)
    {
        // Only a person moving the slider is heard, not the view synchronising it.
        if (!slider.IsPointerOver && !slider.IsKeyboardFocusWithin) return;
        if (e.OldValue is double before && e.NewValue is double after && Math.Abs(after - before) < 1e-9) return;
        var range = slider.Maximum - slider.Minimum;
        UiSound.PlayTick(range > 0 ? (slider.Value - slider.Minimum) / range : 0.5);
    }

    private static void OnDropDown(ComboBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool open && box.IsEffectivelyVisible) UiSound.PlayList(open);
    }
}
