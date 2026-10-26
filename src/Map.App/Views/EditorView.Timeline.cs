using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R31: «Время» - a window beside the map with the year slider (dragging shows the year live, the
/// release is one undo step), the span, the political mode and its legend; the years, border key
/// and political role of the selection; and the series by years as PNG files or a GIF.
/// </summary>
public sealed partial class EditorView
{
    private Window? _timelineWindow;
    private readonly List<Action> _timelineSyncs = [];
    private bool _syncingTimeline;

    private void ShowTimeline()
    {
        _timelineWindow = ShowAuxiliaryWindow(_timelineWindow, "Шкала времени", BuildTimelineContent(), 640, 640, "TimelineWindow");
        SyncTimeline();
    }

    private Control? _timelineContent;

    private Control BuildTimelineContent()
    {
        if (_timelineContent is not null) return _timelineContent;
        static int? Year(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        var on = Ui.PropertyToggle("TimelineEnabledToggle", "Показывать карту на выбранный год");
        on.IsCheckedChanged += (_, _) => { if (!_syncingTimeline) { _editor.SetTimeline(_editor.Timeline with { Enabled = on.IsChecked == true }); SyncTimeline(); } };
        var year = new TextBlock { Name = "TimelineYearText", FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var slider = new Slider { Name = "TimelineYearSlider", SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncingTimeline) return;
            _editor.SetTimelineYear((int)Math.Round(slider.Value), commit: false);
            year.Text = _editor.TimelineYearShown.ToString(CultureInfo.InvariantCulture);
        };
        slider.AddHandler(PointerReleasedEvent, (_, _) => { _editor.SetTimelineYear((int)Math.Round(slider.Value), commit: true); SyncTimeline(); },
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        slider.KeyUp += (_, _) => { _editor.SetTimelineYear((int)Math.Round(slider.Value), commit: true); SyncTimeline(); };
        var first = new TextBox { Name = "TimelineFirstBox", Width = 90 };
        var last = new TextBox { Name = "TimelineLastBox", Width = 90 };
        var seriesFrom = new TextBox { Name = "SeriesFromBox", Width = 80 };
        var seriesTo = new TextBox { Name = "SeriesToBox", Width = 80 };
        var span = Ui.TextButton("TimelineSpanButton", "Задать годы шкалы", () =>
        {
            if (Year(first.Text) is { } f && Year(last.Text) is { } l)
            {
                _editor.SetTimelineSpan(f, l);
                // The series follows the new span.
                seriesFrom.Text = _editor.Timeline.First.ToString(CultureInfo.InvariantCulture);
                seriesTo.Text = _editor.Timeline.Last.ToString(CultureInfo.InvariantCulture);
            }
            SyncTimeline();
        }, "compact");

        var political = Ui.PropertyToggle("TimelinePoliticalToggle", "Политическая карта");
        ToolTip.SetTip(political, "Государства — своим цветом с чёткой границей, спорные земли — пунктиром.");
        political.IsCheckedChanged += (_, _) => { if (!_syncingTimeline) { _editor.SetTimeline(_editor.Timeline with { PoliticalMode = political.IsChecked == true }); SyncTimeline(); } };
        var legend = new StackPanel { Name = "PoliticalLegend", Spacing = 3 };

        var from = new TextBox { Name = "SelectionEraFromBox", Width = 90, PlaceholderText = "с года" };
        var to = new TextBox { Name = "SelectionEraToBox", Width = 90, PlaceholderText = "по год" };
        var applyEra = Ui.TextButton("SelectionEraButton", "Годы выделенного", () => { _editor.SetSelectedEra(Year(from.Text), Year(to.Text)); SyncTimeline(); }, "compact");
        ToolTip.SetTip(applyEra, "Пусто — без ограничения. Объект, подпись или дорога видны только в эти годы.");
        var key = new TextBox { Name = "SelectionMorphKeyBox", Width = 160, PlaceholderText = "ключ границы" };
        var applyKey = Ui.TextButton("SelectionMorphKeyButton", "Связать эпохи", () => { _editor.SetSelectedMorphKey(key.Text); SyncTimeline(); }, "compact");
        ToolTip.SetTip(applyKey, "Фигуры с одним ключом — одна граница в разные годы; между их годами она плавно перетекает.");
        var role = new ComboBox { Name = "SelectionPoliticalBox", Width = 180, ItemsSource = new[] { "Не на политической карте", "Государство", "Спорные земли" } };
        role.SelectionChanged += (_, _) => { if (!_syncingTimeline && role.SelectedIndex >= 0) { _editor.SetSelectedPolitical((PoliticalRole)role.SelectedIndex); SyncTimeline(); } };
        var selection = new StackPanel
        {
            Name = "TimelineSelection", Spacing = 6,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { from, to, applyEra } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { key, applyKey } },
                role,
            },
        };
        var noSelection = new TextBlock { Text = "Выделите объект, подпись, фигуру или дорогу, чтобы задать её годы.", TextWrapping = TextWrapping.Wrap }.Classed("faint");

        var seriesStep = new TextBox { Name = "SeriesStepBox", Width = 60, Text = "10" };
        var seriesSize = new TextBox { Name = "SeriesSizeBox", Width = 70, Text = "1200" };
        (int First, int Last, int Step, int Size) Series() => (Year(seriesFrom.Text) ?? _editor.Timeline.First, Year(seriesTo.Text) ?? _editor.Timeline.Last,
            Math.Max(1, Year(seriesStep.Text) ?? 10), Math.Clamp(Year(seriesSize.Text) ?? 1200, 64, 4096));
        var png = Ui.AsyncButton("SeriesPngButton", "PNG-серия…", async () => { var s = Series(); await _editor.PickAndExportTimelinePngAsync(s.First, s.Last, s.Step, s.Size); }, "compact");
        var gif = Ui.AsyncButton("SeriesGifButton", "GIF…", async () => { var s = Series(); await _editor.PickAndExportTimelineGifAsync(s.First, s.Last, s.Step, s.Size); }, "compact");

        void Sync()
        {
            _syncingTimeline = true;
            try
            {
                var t = _editor.Timeline;
                on.IsChecked = t.Enabled;
                slider.Minimum = t.First; slider.Maximum = t.Last; slider.Value = _editor.TimelineYearShown;
                slider.IsEnabled = t.Enabled;
                year.Text = t.Enabled ? _editor.TimelineYearShown.ToString(CultureInfo.InvariantCulture) : "все годы";
                if (!first.IsFocused) first.Text = t.First.ToString(CultureInfo.InvariantCulture);
                if (!last.IsFocused) last.Text = t.Last.ToString(CultureInfo.InvariantCulture);
                if (!seriesFrom.IsFocused && string.IsNullOrEmpty(seriesFrom.Text)) seriesFrom.Text = t.First.ToString(CultureInfo.InvariantCulture);
                if (!seriesTo.IsFocused && string.IsNullOrEmpty(seriesTo.Text)) seriesTo.Text = t.Last.ToString(CultureInfo.InvariantCulture);
                political.IsChecked = t.PoliticalMode;
                legend.Children.Clear();
                if (t.PoliticalMode)
                {
                    foreach (var (name, colour, kind) in _editor.PoliticalLegend)
                    {
                        var swatch = new Border { Width = 18, Height = 12, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(kind == PoliticalRole.Disputed ? 1.5 : 1),
                            BorderBrush = kind == PoliticalRole.Disputed ? new SolidColorBrush(Color.Parse("#FF8A2F25")) : Brushes.Black,
                            Background = Color.TryParse(colour, out var c) ? new SolidColorBrush(c, kind == PoliticalRole.Disputed ? .3 : .6) : Brushes.Gray };
                        legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch,
                            new TextBlock { Text = name + (kind == PoliticalRole.Disputed ? " — спорные земли" : ""), VerticalAlignment = VerticalAlignment.Center } } });
                    }
                    if (legend.Children.Count == 0)
                        legend.Children.Add(new TextBlock { Text = "Отметьте фигуры как «Государство» или «Спорные земли».", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
                }
                var timed = _editor.HasTimedSelection;
                selection.IsVisible = timed; noSelection.IsVisible = !timed;
                if (timed)
                {
                    var era = _editor.SelectedEra;
                    if (!from.IsFocused) from.Text = era?.From?.ToString(CultureInfo.InvariantCulture) ?? "";
                    if (!to.IsFocused) to.Text = era?.To?.ToString(CultureInfo.InvariantCulture) ?? "";
                    var shape = _editor.Session.SelectedObject?.Shape is not null;
                    key.IsEnabled = applyKey.IsEnabled = role.IsEnabled = shape;
                    if (!key.IsFocused) key.Text = _editor.Session.SelectedObject?.MorphKey ?? "";
                    role.SelectedIndex = (int)(_editor.Session.SelectedObject?.Political ?? PoliticalRole.None);
                }
            }
            finally { _syncingTimeline = false; }
        }
        _timelineSyncs.Add(Sync);

        _timelineContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Карта во времени: у объекта, подписи, фигуры или дороги можно задать годы; ползунок показывает мир на выбранный год."),
                    on, year, slider,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { first, last, span } },
                    Ui.Subgroup("Выделенное", new StackPanel { Spacing = 6, Children = { noSelection, selection } }),
                    Ui.Subgroup("Политическая карта", new StackPanel { Spacing = 6, Children = { political, legend } }),
                    Ui.Subgroup("Серия по годам", new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
                            {
                                new TextBlock { Text = "с", VerticalAlignment = VerticalAlignment.Center }, seriesFrom,
                                new TextBlock { Text = "по", VerticalAlignment = VerticalAlignment.Center }, seriesTo,
                                new TextBlock { Text = "шаг", VerticalAlignment = VerticalAlignment.Center }, seriesStep,
                                new TextBlock { Text = "размер", VerticalAlignment = VerticalAlignment.Center }, seriesSize,
                            } },
                            new Ui.ChoiceGrid { MaxColumns = 2, Children = { png, gif } },
                        },
                    }),
                },
            },
        };
        Sync();
        return _timelineContent;
    }

    private void SyncTimeline()
    {
        foreach (var sync in _timelineSyncs) sync();
    }
}
