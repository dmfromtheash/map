using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>The minimal create-map wizard as a modal window over the wizard view-model.</summary>
public sealed class WizardWindow : Window
{
    private readonly NewMapWizardViewModel _wizard = new();
    private MapDocument? _created;

    private WizardWindow()
    {
        Title = "Новая карта";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var titleBox = new TextBox { Name = "WizardTitleBox", Text = _wizard.Title };
        var widthBox = new TextBox { Name = "WizardWidthBox", Text = _wizard.WidthText };
        var heightBox = new TextBox { Name = "WizardHeightBox", Text = _wizard.HeightText };

        var backgroundCombo = new ComboBox
        {
            Name = "WizardBackgroundCombo",
            ItemsSource = _wizard.BackgroundChoices,
            SelectedIndex = 0,
            MinWidth = 140,
        };

        var error = new TextBlock
        {
            Name = "WizardErrorText",
            Text = "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)),
            TextWrapping = TextWrapping.Wrap,
        };

        var swap = new Button { Name = "SwapOrientationButton", Content = "Сменить ориентацию" };
        swap.Click += (_, _) =>
        {
            _wizard.WidthText = widthBox.Text ?? "";
            _wizard.HeightText = heightBox.Text ?? "";
            _wizard.SwapOrientation();
            widthBox.Text = _wizard.WidthText;
            heightBox.Text = _wizard.HeightText;
        };

        var create = new Button { Name = "CreateMapButton", Content = "Создать", IsDefault = true };
        create.Click += (_, _) =>
        {
            _wizard.Title = titleBox.Text ?? "";
            _wizard.WidthText = widthBox.Text ?? "";
            _wizard.HeightText = heightBox.Text ?? "";
            _wizard.Background = backgroundCombo.SelectedItem as string ?? _wizard.Background;

            var document = _wizard.Create(System.DateTimeOffset.UtcNow);
            if (document is null)
            {
                error.Text = _wizard.ValidationError;
                return;
            }

            _created = document;
            Close();
        };

        var cancel = new Button { Name = "CancelWizardButton", Content = "Отмена", IsCancel = true };
        cancel.Click += (_, _) => Close();

        Control Row(string label, Control field) => new DockPanel
        {
            Margin = new Thickness(0, 3),
            Children =
            {
                Docked(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }),
                field,
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                Row("Название", titleBox),
                Row("Ширина, px", widthBox),
                Row("Высота, px", heightBox),
                Row("Фон", backgroundCombo),
                swap,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { create, cancel },
                },
            },
        };
    }

    private static Control Docked(Control control)
    {
        DockPanel.SetDock(control, Dock.Left);
        return control;
    }

    /// <summary>Returns the created blank document, or null when the user cancelled.</summary>
    public static async Task<MapDocument?> ShowAsync(Window owner)
    {
        var window = new WizardWindow();
        await window.ShowDialog(owner);
        return window._created;
    }
}
