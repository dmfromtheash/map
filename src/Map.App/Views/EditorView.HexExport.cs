using System.Linq;
using Avalonia.Controls;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    /// <summary>The export's level is independent of changing the level in the workshop.</summary>
    private StackPanel BuildHexExportLevelSelector()
    {
        var levels = new ComboBox { Name = "ExportHexLevelComboBox", MinWidth = 180 };
        var values = System.Array.Empty<int>();
        var syncing = false;
        void Sync()
        {
            syncing = true;
            var current = _editor.HexLevels.ToArray();
            if (!values.SequenceEqual(current))
            {
                values = current;
                levels.ItemsSource = values.Select(EditorViewModel.HexLevelName).ToArray();
            }
            levels.SelectedIndex = System.Array.IndexOf(values, _editor.ExportHexLevel);
            syncing = false;
        }
        levels.SelectionChanged += (_, _) =>
        {
            if (syncing || levels.SelectedIndex < 0 || levels.SelectedIndex >= values.Length) return;
            _editor.ExportHexLevel = values[levels.SelectedIndex];
            SyncExport();
        };
        HexSync(Sync, [.. DocumentChanged, nameof(EditorViewModel.HexLevels), nameof(EditorViewModel.ExportHexLevel)]);
        Sync();
        return new StackPanel
        {
            IsVisible = _editor.IsHexWorkshop, Spacing = 4,
            Children = { new TextBlock { Text = "Уровень карты для экспорта" }.Classed("role-caption"), levels },
        };
    }
}
