using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R29: «Переходы» in the Notes tool - stairs, cave mouths, shafts and portals with two ends, a
/// direction and a cost; the check of their ends; and the level of the world of each atlas map.
/// </summary>
public sealed partial class EditorView
{
    private readonly List<Action> _transitionSyncs = [];

    private static readonly TransitionKind[] TransitionKinds = Enum.GetValues<TransitionKind>();
    private static readonly (TransitionDirection Direction, string Title)[] TransitionDirections =
        [(TransitionDirection.Both, "В обе стороны"), (TransitionDirection.Forward, "Только туда"), (TransitionDirection.Back, "Только обратно")];
    private static readonly WorldLevel[] WorldLevels = [WorldLevel.Sky, WorldLevel.Surface, WorldLevel.Underdark];

    private Control BuildTransitionsPage()
    {
        var newKind = new ComboBox { Name = "NewTransitionKind", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = TransitionKinds.Select(MapTransition.KindTitle).ToArray() };
        var create = Ui.CommandButton("NewTransitionButton", "Поставить", MapIcons.Plus, () =>
        {
            _editor.StartNewTransition(TransitionKinds[Math.Max(0, newKind.SelectedIndex)]);
            SyncTransitions();
        }, "compact");
        ToolTip.SetTip(create, "Затем щёлкните на карте место первого конца.");
        var cancel = Ui.TextButton("TransitionPickCancelButton", "Отменить выбор точки", () => { _editor.CancelTransitionPick(); SyncTransitions(); }, "compact");
        var list = new StackPanel { Name = "TransitionList", Spacing = 2 };

        var name = new TextBox { Name = "TransitionNameBox", PlaceholderText = "Название" };
        var kind = new ComboBox { Name = "TransitionKindBox", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = TransitionKinds.Select(MapTransition.KindTitle).ToArray() };
        var direction = new ComboBox { Name = "TransitionDirectionBox", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = TransitionDirections.Select(d => d.Title).ToArray() };
        var cost = new TextBox { Name = "TransitionCostBox", Width = 80 };
        var save = Ui.CommandButton("SaveTransitionButton", "Сохранить", MapIcons.Check, () =>
        {
            if (_editor.SelectedTransitionId is not { } id) return;
            var minutes = double.TryParse(cost.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
            _editor.EditTransition(id, name.Text ?? "", TransitionKinds[Math.Max(0, kind.SelectedIndex)], TransitionDirections[Math.Max(0, direction.SelectedIndex)].Direction, minutes);
            SyncTransitions();
        }, "compact");
        var target = new TextBlock { Name = "TransitionTargetText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        var here = Ui.TextButton("TransitionTargetHereButton", "Второй конец на этой карте…", () => { _editor.PickTransitionTarget(); SyncTransitions(); }, "compact");
        ToolTip.SetTip(here, "Щёлкните на карте. У гекс-карты сначала выберите уровень, на который ведёт переход.");
        var otherMap = new ComboBox { Name = "TransitionMapBox", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Другая карта атласа" };
        var otherX = new TextBox { Name = "TransitionMapXBox", Width = 70, PlaceholderText = "X" };
        var otherY = new TextBox { Name = "TransitionMapYBox", Width = 70, PlaceholderText = "Y" };
        var toMap = Ui.TextButton("TransitionTargetMapButton", "Вести на ту карту", () =>
        {
            if (_editor.SelectedTransitionId is not { } id || _editor.Atlas is not { } atlas) return;
            var others = atlas.Maps.Where(m => m.Id != _editor.Session.Document.Id.Value).ToArray();
            if (otherMap.SelectedIndex < 0 || otherMap.SelectedIndex >= others.Length) return;
            double.TryParse(otherX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x);
            double.TryParse(otherY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y);
            _editor.SetTransitionTargetMap(id, others[otherMap.SelectedIndex].Id, x, y);
            SyncTransitions();
        }, "compact");
        var showFirst = Ui.TextButton("TransitionShowFromButton", "Показать первый конец", () => { if (_editor.SelectedTransitionId is { } id) _editor.GoToTransitionEnd(id, false); }, "compact");
        var showSecond = Ui.TextButton("TransitionShowToButton", "Показать второй конец", () => { if (_editor.SelectedTransitionId is { } id) _editor.GoToTransitionEnd(id, true); }, "compact");
        var delete = Ui.CommandButton("DeleteTransitionButton", "Удалить", MapIcons.Trash, () =>
        {
            if (_editor.SelectedTransitionId is { } id) _editor.DeleteTransition(id);
            SyncTransitions();
        }, "destructive", "compact");
        var otherMapRow = new StackPanel
        {
            Spacing = 4,
            Children = { otherMap, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { otherX, otherY, toMap } } },
        }.Named("TransitionOtherMapRow");
        var card = new Border
        {
            Name = "TransitionCard", Padding = new Avalonia.Thickness(10), CornerRadius = new Avalonia.CornerRadius(6), BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = Avalonia.Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    name, kind, direction,
                    new DockPanel { Children = { cost.Docked(Dock.Right), new TextBlock { Text = "Время, минут", VerticalAlignment = VerticalAlignment.Center } } },
                    save,
                    Ui.Subgroup("Куда ведёт", new StackPanel { Spacing = 4, Children = { target, here, otherMapRow } }),
                    new Ui.ChoiceGrid { MaxColumns = 2, Children = { showFirst, showSecond } },
                    delete,
                },
            },
        };

        var issues = new StackPanel { Name = "TransitionIssues", Spacing = 2 };
        var check = Ui.AsyncButton("CheckTransitionsButton", "Проверить концы", async () =>
        {
            issues.Children.Clear();
            var found = await _editor.CheckTransitionsAsync();
            if (found.Count == 0) issues.Children.Add(new TextBlock { Text = "Все концы на месте.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            foreach (var issue in found.Take(60))
            {
                var captured = issue;
                issues.Children.Add(Ui.TextButton("TransitionIssue_" + issues.Children.Count, issue.Name + ": " + issue.Problem, () =>
                {
                    _editor.SelectedTransitionId = captured.TransitionId;
                    _editor.GoToTransitionEnd(captured.TransitionId, false);
                    SyncTransitions();
                }, "compact"));
            }
        }, "compact");

        var levels = new StackPanel { Name = "AtlasLevels", Spacing = 4 };

        void Sync()
        {
            cancel.IsVisible = _editor.TransitionPickArmed;
            list.Children.Clear();
            if (_editor.Transitions.Length == 0)
                list.Children.Add(Ui.EmptyState("NoTransitionsText", MapIcons.Notes, "Переходов пока нет", "Лестница, вход в пещеру, шахта или портал: выберите вид и нажмите «Поставить»."));
            foreach (var t in _editor.Transitions)
            {
                var captured = t;
                var row = Ui.ListRow("Transition_" + t.Id.ToString("N"), MapIcons.HexPlayer, t.Name,
                    MapTransition.KindTitle(t.Kind) + " · " + _editor.TransitionTargetTitle(t), [],
                    () => { _editor.SelectedTransitionId = captured.Id; SyncTransitions(); }, t.Name);
                Ui.SetSelected(row, t.Id == _editor.SelectedTransitionId);
                list.Children.Add(row);
            }

            var selected = _editor.SelectedTransition;
            card.IsVisible = selected is not null;
            if (selected is not null)
            {
                if (!name.IsFocused) name.Text = selected.Name;
                kind.SelectedIndex = Array.IndexOf(TransitionKinds, selected.Kind);
                direction.SelectedIndex = Array.FindIndex(TransitionDirections, d => d.Direction == selected.Direction);
                if (!cost.IsFocused) cost.Text = selected.CostMinutes.ToString("0.#", CultureInfo.InvariantCulture);
                target.Text = "Сейчас: " + _editor.TransitionTargetTitle(selected) + ".";
            }

            var atlas = _editor.Atlas;
            var others = atlas?.Maps.Where(m => m.Id != _editor.Session.Document.Id.Value).ToArray() ?? [];
            otherMapRow.IsVisible = others.Length > 0;
            otherMap.ItemsSource = others.Select(m => m.Title + " · " + MapTransition.LevelTitle(m.Level)).ToArray();

            levels.Children.Clear();
            if (atlas is null)
                levels.Children.Add(new TextBlock { Text = "Карта не в атласе. Уровни мира (небо, поверхность, подземье) задаются картам атласа.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            else
                foreach (var map in atlas.Maps)
                {
                    var capturedMap = map;
                    var choice = new ComboBox { Name = "AtlasLevel_" + map.Id.ToString("N"), Width = 140, ItemsSource = WorldLevels.Select(MapTransition.LevelTitle).ToArray(),
                        SelectedIndex = Array.IndexOf(WorldLevels, map.Level) };
                    choice.SelectionChanged += async (_, _) =>
                    {
                        if (choice.SelectedIndex < 0 || WorldLevels[choice.SelectedIndex] == capturedMap.Level) return;
                        await _editor.SetAtlasMapLevelAsync(capturedMap.Id, WorldLevels[choice.SelectedIndex]);
                        SyncTransitions();
                    };
                    levels.Children.Add(new DockPanel { Children = { choice.Docked(Dock.Right), new TextBlock { Text = map.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } });
                }
        }
        _transitionSyncs.Add(Sync);
        Sync();
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Hint("Лестницы, входы в пещеры, шахты и порталы между уровнями и картами атласа: два конца, направление и время пути."),
                Ui.Subgroup("Новый переход", new DockPanel { Children = { create.Docked(Dock.Right), newKind } }),
                cancel, list, card,
                check, issues,
                Ui.Subgroup("Уровни мира в атласе", levels),
            },
        };
    }

    private void SyncTransitions()
    {
        foreach (var sync in _transitionSyncs) sync();
    }
}
