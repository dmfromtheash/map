using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.Presentation.Services;

namespace Map.App.Services;

/// <summary>Save / Don't save / Cancel for a dirty document.</summary>
public sealed class ClosePromptWindow : Window
{
    private CloseChoice _choice = CloseChoice.Cancel;

    private ClosePromptWindow(string documentTitle)
    {
        Title = "Несохранённые изменения";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        Button Make(string name, string text, CloseChoice choice, bool isDefault = false)
        {
            var button = new Button { Name = name, Content = text, MinWidth = 120, IsDefault = isDefault };
            button.Click += (_, _) =>
            {
                _choice = choice;
                Close();
            };
            return button;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Name = "ClosePromptText",
                    Text = $"В карте «{documentTitle}» есть несохранённые изменения. Сохранить их?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Make("SaveChoiceButton", "Сохранить", CloseChoice.Save, isDefault: true),
                        Make("DontSaveChoiceButton", "Не сохранять", CloseChoice.DontSave),
                        Make("CancelChoiceButton", "Отмена", CloseChoice.Cancel),
                    },
                },
            },
        };
    }

    public static async Task<CloseChoice> ShowAsync(Window owner, string documentTitle)
    {
        var window = new ClosePromptWindow(documentTitle);
        await window.ShowDialog(owner);
        return window._choice;
    }
}

/// <summary>A short error with a path to recovery, never a stack trace.</summary>
public sealed class ErrorWindow : Window
{
    private ErrorWindow(string title, string message)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var ok = new Button
        {
            Name = "ErrorOkButton",
            Content = "Понятно",
            MinWidth = 120,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Name = "ErrorText",
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                ok,
            },
        };
    }

    public static Task ShowAsync(Window owner, string title, string message) =>
        new ErrorWindow(title, message).ShowDialog(owner);
}
