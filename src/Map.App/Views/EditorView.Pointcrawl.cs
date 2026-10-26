using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R32: «Пойнткроул» - a window beside the map: places and ways put by clicks, the chosen place
/// (name, note, card, visited) and way (hours, danger, hidden, one way), the spring layout, fog by
/// visit, and the PDF key for the master or the players.
/// </summary>
public sealed partial class EditorView
{
    private Window? _pointcrawlWindow;
    private Control? _pointcrawlContent;
    private readonly List<Action> _pointcrawlSyncs = [];
    private bool _syncingPointcrawl;

    private void ShowPointcrawl()
    {
        _pointcrawlWindow = ShowAuxiliaryWindow(_pointcrawlWindow, "Пойнткроул", BuildPointcrawlContent(), 660, 760, "PointcrawlWindow");
        SyncPointcrawl();
    }

    private Control BuildPointcrawlContent()
    {
        if (_pointcrawlContent is not null) return _pointcrawlContent;
        var shown = Ui.PropertyToggle("PointcrawlShownToggle", "Показывать на карте");
        var fog = Ui.PropertyToggle("PointcrawlFogToggle", "Туман по посещению");
        ToolTip.SetTip(fog, "Игроки видят посещённые места, пути из них и «?» на их концах. Скрытые пути — только ведущему.");
        shown.IsCheckedChanged += (_, _) => { if (!_syncingPointcrawl) { _editor.SetPointcrawlOptions(shown.IsChecked == true, fog.IsChecked == true); SyncPointcrawl(); } };
        fog.IsCheckedChanged += (_, _) => { if (!_syncingPointcrawl) { _editor.SetPointcrawlOptions(shown.IsChecked == true, fog.IsChecked == true); SyncPointcrawl(); } };
        var place = Ui.CommandButton("PointcrawlPlaceButton", "Место", MapIcons.Plus, () => { _editor.StartAddPointcrawlPlace(); SyncPointcrawl(); }, "compact");
        var link = Ui.CommandButton("PointcrawlLinkButton", "Путь", MapIcons.LineRoad, () => { _editor.StartLinkPointcrawl(); SyncPointcrawl(); }, "compact");
        var arrange = Ui.TextButton("PointcrawlArrangeButton", "Раскладка", () => { _editor.ArrangePointcrawl(); SyncPointcrawl(); }, "compact");
        ToolTip.SetTip(arrange, "Связанные места сближаются, остальные расходятся; одна отмена вернёт как было.");
        var cancel = Ui.TextButton("PointcrawlCancelButton", "Отменить выбор", () => { _editor.CancelPointcrawlPick(); SyncPointcrawl(); }, "compact");
        var places = new StackPanel { Name = "PointcrawlPlaces", Spacing = 2 };
        var ways = new StackPanel { Name = "PointcrawlWays", Spacing = 2 };

        var placeName = new TextBox { Name = "PointcrawlPlaceNameBox", PlaceholderText = "Название" };
        var placeNote = new TextBox { Name = "PointcrawlPlaceNoteBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "Что здесь (для ведущего)" };
        var visited = Ui.PropertyToggle("PointcrawlVisitedToggle", "Посещено");
        var card = new ComboBox { Name = "PointcrawlCardBox", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Карточка мира" };
        var savePlace = Ui.TextButton("PointcrawlSavePlaceButton", "Сохранить место", () =>
        {
            if (_editor.SelectedPointcrawlNodeId is not { } id) return;
            var cards = _editor.Entities;
            Guid? entity = card.SelectedIndex > 0 && card.SelectedIndex - 1 < cards.Length ? cards[card.SelectedIndex - 1].Id : null;
            _editor.EditPointcrawlNode(id, placeName.Text ?? "", placeNote.Text ?? "", visited.IsChecked == true, entity);
            SyncPointcrawl();
        }, "compact");
        var goPlace = Ui.TextButton("PointcrawlGoPlaceButton", "Перейти", () => { if (_editor.SelectedPointcrawlNodeId is { } id) _editor.OpenPointcrawlPlace(id); }, "compact");
        var deletePlace = Ui.TextButton("PointcrawlDeletePlaceButton", "Удалить место", () =>
        {
            if (_editor.SelectedPointcrawlNodeId is { } id) _editor.DeletePointcrawlNode(id);
            SyncPointcrawl();
        }, "destructive", "compact");
        var placeCard = new StackPanel { Name = "PointcrawlPlaceCard", Spacing = 6, Children = { placeName, placeNote, visited, card,
            new Ui.ChoiceGrid { MaxColumns = 3, Children = { savePlace, goPlace, deletePlace } } } };

        var wayName = new TextBox { Name = "PointcrawlWayNameBox", PlaceholderText = "Название пути (необязательно)" };
        var hours = new TextBox { Name = "PointcrawlWayHoursBox", Width = 70 };
        var danger = new ComboBox { Name = "PointcrawlWayDangerBox", Width = 120, ItemsSource = new[] { "без опасности", "1", "2", "3", "4", "5" } };
        var hidden = Ui.PropertyToggle("PointcrawlWayHiddenToggle", "Скрытый");
        var oneWay = Ui.PropertyToggle("PointcrawlWayOneWayToggle", "В одну сторону");
        var saveWay = Ui.TextButton("PointcrawlSaveWayButton", "Сохранить путь", () =>
        {
            if (_editor.SelectedPointcrawlEdgeId is not { } id) return;
            var h = double.TryParse(hours.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 4;
            _editor.EditPointcrawlEdge(id, wayName.Text ?? "", h, Math.Max(0, danger.SelectedIndex), hidden.IsChecked == true, oneWay.IsChecked == true);
            SyncPointcrawl();
        }, "compact");
        var deleteWay = Ui.TextButton("PointcrawlDeleteWayButton", "Удалить путь", () =>
        {
            if (_editor.SelectedPointcrawlEdgeId is { } id) _editor.DeletePointcrawlEdge(id);
            SyncPointcrawl();
        }, "destructive", "compact");
        var wayCard = new StackPanel
        {
            Name = "PointcrawlWayCard", Spacing = 6,
            Children =
            {
                wayName,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Часов", VerticalAlignment = VerticalAlignment.Center }, hours,
                    new TextBlock { Text = "Опасность", VerticalAlignment = VerticalAlignment.Center }, danger } },
                hidden, oneWay,
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { saveWay, deleteWay } },
            },
        };

        var keyMaster = Ui.AsyncButton("PointcrawlKeyMasterButton", "PDF-ключ ведущего…", async () => await _editor.PickAndExportPointcrawlKeyAsync(players: false), "compact");
        var keyPlayers = Ui.AsyncButton("PointcrawlKeyPlayersButton", "PDF для игроков…", async () => await _editor.PickAndExportPointcrawlKeyAsync(players: true), "compact");

        void Sync()
        {
            _syncingPointcrawl = true;
            try
            {
                var crawl = _editor.Pointcrawl;
                shown.IsChecked = crawl.Shown; fog.IsChecked = crawl.FogByVisit;
                cancel.IsVisible = _editor.PointcrawlPickArmed;
                places.Children.Clear();
                foreach (var node in crawl.Nodes)
                {
                    var captured = node;
                    var row = Ui.ListRow("PointcrawlPlace_" + node.Id.ToString("N"), MapIcons.LineTarget, $"{crawl.NumberOf(node.Id)}. {node.Name}",
                        node.Visited ? "посещено" : "не посещено", [], () =>
                        {
                            _editor.SelectedPointcrawlNodeId = captured.Id; _editor.SelectedPointcrawlEdgeId = null; SyncPointcrawl();
                        }, node.Name);
                    Ui.SetSelected(row, node.Id == _editor.SelectedPointcrawlNodeId);
                    places.Children.Add(row);
                }
                if (crawl.Nodes.Length == 0)
                    places.Children.Add(new TextBlock { Text = "Мест пока нет. «Место» — и щелчок на карте.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
                ways.Children.Clear();
                var names = crawl.Nodes.ToDictionary(n => n.Id, n => crawl.NumberOf(n.Id));
                foreach (var edge in crawl.Edges)
                {
                    var captured = edge;
                    var flags = string.Join(", ", new[] { $"{edge.Hours:0.#} ч", edge.Danger > 0 ? $"опасность {edge.Danger}" : null,
                        edge.Hidden ? "скрытый" : null, edge.OneWay ? "в одну сторону" : null }.Where(s => s is not null));
                    var row = Ui.ListRow("PointcrawlWay_" + edge.Id.ToString("N"), MapIcons.LineRoad,
                        $"{names.GetValueOrDefault(edge.From)} {(edge.OneWay ? "→" : "—")} {names.GetValueOrDefault(edge.To)}" + (edge.Name.Length > 0 ? " · " + edge.Name : ""),
                        flags, [], () => { _editor.SelectedPointcrawlEdgeId = captured.Id; _editor.SelectedPointcrawlNodeId = null; SyncPointcrawl(); }, edge.Name);
                    Ui.SetSelected(row, edge.Id == _editor.SelectedPointcrawlEdgeId);
                    ways.Children.Add(row);
                }

                var node0 = _editor.SelectedPointcrawlNode;
                placeCard.IsVisible = node0 is not null;
                card.ItemsSource = new[] { "без карточки" }.Concat(_editor.Entities.Select(e => e.Name)).ToArray();
                if (node0 is not null)
                {
                    if (!placeName.IsFocused) placeName.Text = node0.Name;
                    if (!placeNote.IsFocused) placeNote.Text = node0.Note;
                    visited.IsChecked = node0.Visited;
                    card.SelectedIndex = node0.EntityId is { } e ? Math.Max(0, _editor.Entities.IndexOf(_editor.Entities.FirstOrDefault(x => x.Id == e)!) + 1) : 0;
                }
                var edge0 = _editor.SelectedPointcrawlEdge;
                wayCard.IsVisible = edge0 is not null;
                if (edge0 is not null)
                {
                    if (!wayName.IsFocused) wayName.Text = edge0.Name;
                    if (!hours.IsFocused) hours.Text = edge0.Hours.ToString("0.#", CultureInfo.InvariantCulture);
                    danger.SelectedIndex = edge0.Danger;
                    hidden.IsChecked = edge0.Hidden; oneWay.IsChecked = edge0.OneWay;
                }
            }
            finally { _syncingPointcrawl = false; }
        }
        _pointcrawlSyncs.Add(Sync);

        _pointcrawlContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Места и пути поверх карты: путешествие от точки к точке. Место может быть карточкой мира и вести на её карту в атласе."),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { shown, fog } },
                    new Ui.ChoiceGrid { MaxColumns = 3, Children = { place, link, arrange } },
                    cancel,
                    Ui.Subgroup("Места", new StackPanel { Spacing = 6, Children = { places, placeCard } }),
                    Ui.Subgroup("Пути", new StackPanel { Spacing = 6, Children = { ways, wayCard } }),
                    Ui.Subgroup("Ключ", new Ui.ChoiceGrid { MaxColumns = 2, Children = { keyMaster, keyPlayers } }),
                },
            },
        };
        Sync();
        return _pointcrawlContent;
    }

    private void SyncPointcrawl()
    {
        foreach (var sync in _pointcrawlSyncs) sync();
    }
}
