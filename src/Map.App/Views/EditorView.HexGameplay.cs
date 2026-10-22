using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildHexPartySection()
    {
        var summary = new TextBlock { Name = "HexPartySummary", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        HexSync(() => summary.Text = _editor.HexPartySummary, DocumentChanged);
        var radius = new NumericUpDown { Name = "HexPartyRadiusBox", Minimum = 0, Maximum = HexGameplay.MaxSightRadius, Increment = 1 };
        var day = new NumericUpDown { Name = "HexCampaignDayBox", Minimum = 0, Maximum = int.MaxValue, Increment = 1 };
        radius.ValueChanged += (_, _) => { if (!_syncingHex) _editor.HexPartyRadius = (int)(radius.Value ?? 1); };
        day.ValueChanged += (_, _) => { if (!_syncingHex) _editor.HexCampaignDay = (int)(day.Value ?? 0); };
        HexSync(() => { radius.Value = _editor.HexPartyRadius; day.Value = _editor.HexCampaignDay; }, DocumentChanged);
        return Ui.Category("HexPartyCategory", "Партия", "accent-create", new StackPanel { Spacing = 6, Children =
        {
            summary, HexHint("Щёлкните по гексу или перетащите маркер партии. Каждый шаг можно отменить."),
            Ui.Subgroup("Радиус обзора, гексы", radius), Ui.Subgroup("День кампании", day),
            Ui.TextButton("HexPartyClearHistoryButton", "Очистить историю", _editor.ClearHexPartyHistory, "compact"),
            Ui.TextButton("HexPartyPlayerButton", "Окно игрока…", _editor.OpenHexPlayerView, "compact"),
        } });
    }

    private Control BuildHexRevealSection()
    {
        var condition = new ComboBox { Name = "HexRevealConditionBox", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Всегда", "При посещении", "Вручную", "С даты" } };
        var manual = new CheckBox { Name = "HexRevealManualBox", Content = "Раскрыто вручную" };
        var day = new NumericUpDown { Name = "HexRevealDayBox", Minimum = 0, Maximum = int.MaxValue, Increment = 1 };
        var rumor = new TextBox { Name = "HexRumorBox", Watermark = "Слух вместо правды для игроков", MaxLength = HexNote.MaxTextLength,
            AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 48 };
        var label = new TextBox { Name = "HexApproximateLabelBox", Watermark = "Приблизительная подпись", MaxLength = HexNote.MaxTitleLength };
        void Commit()
        {
            if (_syncingHex || _editor.HexKeyNote is not { } n || condition.SelectedIndex < 0) return;
            _editor.SetHexKey(n with { RevealCondition = (RevealCondition)condition.SelectedIndex,
                ManuallyRevealed = manual.IsChecked == true, RevealDay = (int)(day.Value ?? 0),
                Rumor = rumor.Text ?? "", ApproximateLabel = label.Text ?? "" });
        }
        condition.SelectionChanged += (_, _) => Commit(); manual.IsCheckedChanged += (_, _) => Commit();
        day.ValueChanged += (_, _) => Commit(); rumor.LostFocus += (_, _) => Commit(); label.LostFocus += (_, _) => Commit();
        HexSync(() =>
        {
            if (_editor.HexKeyNote is not { } n) return;
            condition.SelectedIndex = (int)n.RevealCondition; manual.IsChecked = n.ManuallyRevealed; day.Value = n.RevealDay;
            rumor.Text = n.Rumor; label.Text = n.ApproximateLabel;
            manual.IsVisible = n.RevealCondition == RevealCondition.Manual; day.IsVisible = n.RevealCondition == RevealCondition.FromDate;
        }, [.. DocumentChanged, nameof(EditorViewModel.HexKeyHex), nameof(EditorViewModel.HexKeyNote)]);
        return Ui.Subgroup("Раскрытие и знания игроков", new StackPanel { Spacing = 5, Children = { condition, manual, day, label, rumor,
            Ui.TextButton("HexKnowledgeSaveButton", "Сохранить знания", Commit, "compact") } });
    }

    private Control BuildHexRegionKnowledge(HexRegion region) => KnowledgeFields("HexRegion" + region.Id, region.Rumor,
        region.ApproximateLabel, (rumor, label) => _editor.SetHexRegionKnowledge(region.Id, rumor, label));

    private Control BuildHexPoiKnowledge(int index, HexPoi point) => KnowledgeFields("HexPoi" + index, point.Rumor,
        point.ApproximateLabel, (rumor, label) =>
        {
            if (index < _editor.HexPoints.Count) _editor.SetHexPoi(index, _editor.HexPoints[index] with { Rumor = rumor, ApproximateLabel = label });
        });

    private static Control KnowledgeFields(string id, string rumorText, string labelText, Action<string, string> save)
    {
        var rumor = new TextBox { Name = id + "RumorBox", Text = rumorText, Watermark = "Слух для игроков", MaxLength = HexNote.MaxTextLength,
            AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var label = new TextBox { Name = id + "ApproximateLabelBox", Text = labelText, Watermark = "Приблизительная подпись", MaxLength = HexNote.MaxTitleLength };
        void Commit() => save(rumor.Text ?? "", label.Text ?? "");
        rumor.LostFocus += (_, _) => Commit(); label.LostFocus += (_, _) => Commit();
        return new StackPanel { Spacing = 3, Children = { label, rumor } };
    }
}
