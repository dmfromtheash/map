using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    /// <summary>«Экспорт»: the map's handout (R09f) - which sheet, and one button that saves the map laid into it.</summary>
    private Control BuildHandoutExport()
    {
        var result = new TextBlock { Name = "ExportHandoutResultText", TextWrapping = TextWrapping.Wrap };
        var choice = new ComboBox
        {
            Name = "ExportHandoutComboBox", MinWidth = 220,
            ItemsSource = MapHandouts.All.Select(MapHandouts.Name).ToArray(),
            SelectedIndex = MapHandouts.All.ToList().IndexOf(_editor.Handout),
        };
        var save = Ui.AsyncButton("ExportHandoutButton", "Сохранить хендаут…", async () =>
        {
            await _editor.ExportHandoutAsync();
            result.Text = _editor.StatusDetail;
        }, "compact");
        save.IsEnabled = _editor.Handout != MapHandout.None;
        var syncing = false;
        choice.SelectionChanged += (_, _) =>
        {
            if (syncing || choice.SelectedIndex < 0) return;
            _editor.Handout = MapHandouts.All[choice.SelectedIndex];
        };
        ((INotifyPropertyChanged)_editor).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EditorViewModel.Handout)) return;
            syncing = true;
            choice.SelectedIndex = MapHandouts.All.ToList().IndexOf(_editor.Handout);
            syncing = false;
            save.IsEnabled = _editor.Handout != MapHandout.None;
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Хендаут" }.Classed("role-caption"),
                new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Children = { choice, save } },
                new TextBlock
                {
                    Text = "Вся карта на старом листе на столе, один PNG. Холст не меняется.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                result,
            },
        };
    }
}
