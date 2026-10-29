using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.Presentation.Services;

namespace Map.App.Views;

/// <summary>Explicit v1 migration choice; cancelling is the safe default.</summary>
public sealed class MigrationPromptWindow : Window
{
    private bool _confirmed;

    public MigrationPromptWindow()
    {
        Title = "Нужно обновить копию проекта";
        Name = "MigrationPromptWindow";
        // The window grows to its buttons instead of pushing the last one past its edge (owner
        // 2026-10-21: «Отмена» stuck out of the unsaved-changes window); text wraps from 680 px.
        MinWidth = 520;
        MaxWidth = 680;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var create = new Button
        {
            Name = "CreateMigratedCopyButton",
            Content = "Создать копию и открыть",
            MinWidth = 190,
        };
        create.Click += (_, _) => { _confirmed = true; Close(); };
        var cancel = new Button
        {
            Name = "CancelMigrationButton",
            Content = "Отмена",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 110,
        };
        cancel.Click += (_, _) => Close();
        Opened += (_, _) => cancel.Focus();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Name = "MigrationPromptText",
                    Text = "Исходный файл останется без изменений. Map создаст новую копию формата v2.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, create },
                },
            },
        };
    }

    public static async Task<bool> ShowAsync(Window owner)
    {
        var window = new MigrationPromptWindow();
        await window.ShowDialog(owner);
        return window._confirmed;
    }
}

/// <summary>Save / Don't save / Cancel for a dirty document.</summary>
public sealed class ClosePromptWindow : Window
{
    private CloseChoice _choice = CloseChoice.Cancel;

    private ClosePromptWindow(string documentTitle)
    {
        Title = "Несохранённые изменения";
        // The window grows to its buttons instead of pushing the last one past its edge (owner
        // 2026-10-21: «Отмена» stuck out of the unsaved-changes window); text wraps from 620 px.
        MinWidth = 460;
        MaxWidth = 620;
        SizeToContent = SizeToContent.WidthAndHeight;
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
        // The window grows to its buttons instead of pushing the last one past its edge (owner
        // 2026-10-21: «Отмена» stuck out of the unsaved-changes window); text wraps from 680 px.
        MinWidth = 520;
        MaxWidth = 680;
        SizeToContent = SizeToContent.WidthAndHeight;
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

/// <summary>One explicit dangerous choice with a safe Cancel default.</summary>
public sealed class ConfirmationWindow : Window
{
    private bool _confirmed;

    private ConfirmationWindow(
        string title,
        string message,
        string confirmText,
        string confirmButtonName,
        bool destructive)
    {
        Title = title;
        // The window grows to its buttons instead of pushing the last one past its edge (owner
        // 2026-10-21: «Отмена» stuck out of the unsaved-changes window); text wraps from 660 px.
        MinWidth = 500;
        MaxWidth = 660;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var confirm = new Button
        {
            Name = confirmButtonName,
            Content = confirmText,
            MinWidth = 120,
        };
        if (destructive)
        {
            confirm.Classes.Add("destructive");
        }
        confirm.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };

        var cancel = new Button
        {
            Name = "CheckpointCancelButton",
            Content = "Отмена",
            MinWidth = 120,
            IsDefault = true,
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Name = "CheckpointConfirmationText",
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm },
                },
            },
        };
    }

    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string confirmText,
        string confirmButtonName,
        bool destructive = false)
    {
        var window = new ConfirmationWindow(
            title, message, confirmText, confirmButtonName, destructive);
        await window.ShowDialog(owner);
        return window._confirmed;
    }
}
