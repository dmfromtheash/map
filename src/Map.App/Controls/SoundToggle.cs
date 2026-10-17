using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Map.App.Services;
using Map.App.Views;

namespace Map.App.Controls;

/// <summary>
/// The one switch at the top of the window that turns every interface sound on or off
/// (2026-10-17). Every copy - library header, editor bar - follows the same setting and repaints
/// when it changes; the choice is saved at once. Turning sound off is silent; turning it on
/// answers with the "on" chime.
/// </summary>
public static class SoundToggle
{
    private const string SpeakerIcon = "M4 9.5 H8 L13 5 V19 L8 14.5 H4 Z M16 9 A4 4 0 0 1 16 15 M18.5 6.5 A7.5 7.5 0 0 1 18.5 17.5";
    private const string SpeakerOffIcon = "M4 9.5 H8 L13 5 V19 L8 14.5 H4 Z M16 9.5 L21 14.5 M21 9.5 L16 14.5";

    /// <param name="name">Automation name of this copy.</param>
    /// <param name="withCaption">Icon and "Звук: вкл/выкл" when true; icon only when false.</param>
    public static Button Create(string name, bool withCaption)
    {
        var button = new Button { Name = name };
        button.Classes.Add(withCaption ? "compact" : "icon");
        button.Classes.Add("sound-toggle");

        void Paint()
        {
            var on = UiFeel.SoundsSetting;
            var icon = MapIcons.Stroked(on ? SpeakerIcon : SpeakerOffIcon, 18);
            button.Content = withCaption
                ? new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        icon,
                        new TextBlock { Text = on ? "Звук: вкл" : "Звук: выкл", VerticalAlignment = VerticalAlignment.Center },
                    },
                }
                : icon;
            ToolTip.SetTip(button, on
                ? "Звуки интерфейса включены. Нажмите, чтобы выключить все звуки."
                : "Звуки интерфейса выключены. Нажмите, чтобы включить.");
            AutomationProperties.SetName(button, on ? "Выключить звуки интерфейса" : "Включить звуки интерфейса");
        }

        void OnChanged() => Dispatcher.UIThread.Post(Paint);
        button.Click += (_, _) =>
        {
            var on = !UiFeel.SoundsSetting;
            UiFeel.SetSounds(on);
            if (on) UiSound.PlayToggle(true);
        };
        button.AttachedToVisualTree += (_, _) => { UiFeel.Changed += OnChanged; Paint(); };
        button.DetachedFromVisualTree += (_, _) => UiFeel.Changed -= OnChanged;
        Paint();
        return button;
    }
}
