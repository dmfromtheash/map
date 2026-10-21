using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>The «Переправы» block of the road panel (R06): one row per crossing with its kind.</summary>
public sealed partial class EditorView
{
    private StackPanel? _roadCrossingsPanel;

    private Control BuildRoadCrossingsBlock()
    {
        _roadCrossingsPanel = new StackPanel { Spacing = 4 }.Named("RoadCrossingsPanel");
        return Ui.Subgroup("Переправы", _roadCrossingsPanel);
    }

    private string? _roadCrossingsSignature;

    private void SyncRoadCrossings()
    {
        if (_roadCrossingsPanel is null) return;
        var crossings = _editor.SelectedRoadCrossings;
        // Rebuild only when the set or a kind changed: the buttons stay the same objects between
        // panel refreshes, so a click that was found a moment ago still lands on a live button.
        var signature = string.Join("|", crossings.Select(c => $"{c.BranchId:N}:{c.ChannelId:N}:{c.Ordinal}:{c.Kind}"));
        if (signature == _roadCrossingsSignature) return;
        _roadCrossingsSignature = signature;
        _roadCrossingsPanel.Children.Clear();
        if (crossings.Count == 0)
        {
            _roadCrossingsPanel.Children.Add(Ui.Hint("Дорога не пересекает рек. Дорога через реку сама получает мост."));
            return;
        }
        for (var i = 0; i < crossings.Count; i++)
        {
            var crossing = crossings[i];
            var index = i;
            var river = _editor.Rivers.FirstOrDefault(r => r.Id == crossing.RiverId);
            _roadCrossingsPanel.Children.Add(new TextBlock
            {
                Text = $"Переправа {i + 1} · {river?.Name ?? "река"}",
                FontSize = 11.5,
            }.Named("RoadCrossingTitle" + index));
            var (control, buttons) = Ui.Segmented("RoadCrossingKind" + index,
                [("Bridge", "Мост", null), ("Ford", "Брод", null), ("Ferry", "Паром", null), ("None", "Нет", null)],
                key =>
                {
                    // The crossing is looked up at click time: the road may have changed since the row was built.
                    var current = _editor.SelectedRoadCrossings;
                    if (index < current.Count) _editor.SetCrossingKind(current[index], Enum.Parse<RoadCrossingKind>(key));
                });
            foreach (var button in buttons)
            {
                var active = button.Name == "RoadCrossingKind" + index + crossing.Kind;
                if (button is ToggleButton toggle) toggle.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
            _roadCrossingsPanel.Children.Add(control);
        }
    }
}
