using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private ComboBox? _pathPicker;
    private TextBlock? _pathSummary;
    private Ui.SectionPanel? _pathPanel;
    private Button? _pathFinish, _pathDetach, _pathCalcCancel, _pathRecalc;
    private bool _syncingPaths;

    // Small line glyphs of the path commands, on the 24-unit grid of MapIcons.
    private const string GlyphNewPath = "M4 18C8 9 14 19 20 8M17 15V21M14 18H20";
    private const string GlyphAddNode = "M3 16H21M12 4V11M8.5 7.5H15.5M9.6 16A2.4 2.4 0 1 0 14.4 16A2.4 2.4 0 1 0 9.6 16";
    private const string GlyphDeleteNode = "M3 16H21M8.5 7.5H15.5M9.6 16A2.4 2.4 0 1 0 14.4 16A2.4 2.4 0 1 0 9.6 16";
    private const string GlyphPick = "M5 3L17 9L12 10.5L10 16ZM14 14L19 19";
    private const string GlyphDetach = "M3 19C6 14 9 14 12 16M14 4H20V10M20 4L13 11";
    private const string GlyphApply = "M3 19L8 11L11 15L13 12L17 19ZM15 4H21M18 1V7";
    private const string GlyphBake = "M2 19L6 12L9 16M9 19L13 12L16 16M16 19L19 14L22 19";

    /// <summary>
    /// The path of the asset panel as one section (owner, 2026-11-11, variant 1 of
    /// https://claude.ai/artifact/PNyW4CtTbatoHGn7wESxgn): counts at the right of its title, «Шаг» and «Рисунок» as
    /// rows, the eight commands as a grid of small captioned icons. What each one does is on its card beside the panel.
    /// </summary>
    private Control BuildEditablePathPanel()
    {
        _pathPicker = new ComboBox { Name = "EditablePathPicker", HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Пути этой карты" };
        _pathPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingPaths && _pathPicker.SelectedItem is PathChoice item) _editor.SelectEditablePath(item.Id);
        };
        // Shown only while the path is drawn, calculated or refused: the steady counts live in the title.
        _pathSummary = new TextBlock { Name = "EditablePathSummary", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _pathFinish = Ui.TextButton("FinishEditablePathButton", "Завершить путь", () => _editor.FinishEditablePath(), "compact", "accent");
        // R20b: the full path is calculated in the background; these show only while it runs or after a conflict.
        _pathCalcCancel = Ui.TextButton("PathCalculationCancelButton", "Отменить расчёт пути", () => _editor.CancelPathCalculation(), "compact");
        _pathRecalc = Ui.TextButton("PathRecalculateButton", "Пересчитать путь", _editor.RecalculatePath, "compact", "accent");

        var spacing = Explain(AppearanceNumberBox("EditablePathSpacingBox", "Шаг", "ед.", () => _editor.DisplayedEditablePath?.Spacing,
            _editor.SetEditablePathSpacing), PanelDemoKind.Spacing, "Шаг", "Расстояние между ассетами вдоль пути, в единицах карты.");
        var reroll = new Button { Content = MapIcons.Grid24(MapIcons.LineReset, 14), Width = 26, Height = 26, MinHeight = 26,
            Padding = new Thickness(0) }.Named("EditablePathRerollButton").Classed("icon");
        AutomationProperties.SetName(reroll, "Другой рисунок");
        reroll.Click += (_, _) =>
        {
            if (_editor.DisplayedEditablePath is not { } path) return;
            var next = Random.Shared.Next(1, 999);
            _editor.SetEditablePathSeed(next >= path.Seed ? next + 1 : next); // never the same number again
        };
        var seedRow = AppearanceNumberBox("EditablePathSeedBox", "Рисунок", null, () => _editor.DisplayedEditablePath?.Seed, _editor.SetEditablePathSeed);
        var seed = Explain(new DockPanel { Children = { reroll.Docked(Dock.Right), seedRow } },
            PanelDemoKind.Seed, "Рисунок", "Номер раскладки: другое число — другие сдвиги и варианты, то же число — та же раскладка. Кнопка справа — другой номер.");
        // The numbers stand at the right edge in the width of the panel's other number boxes, like «Масштаб … 120 %».
        foreach (var box in new[] { spacing, seedRow }.SelectMany(r => r.GetLogicalDescendants()).OfType<TextBox>())
        {
            box.MinWidth = 48;
            box.Width = box.Name == "EditablePathSeedBox" ? 96 : 52;
            box.HorizontalAlignment = HorizontalAlignment.Right;
            box.HorizontalContentAlignment = HorizontalAlignment.Right;
        }

        _pathDetach = PathAction("DetachPathObjectButton", "Отделить", GlyphDetach, _editor.DetachSelectedPathObject,
            PanelDemoKind.Detach, "Отделить выбранный объект", "Выбранный ассет становится обычным объектом и больше не следует за путём.");
        var actions = new UniformGrid { Columns = 4, Children =
        {
            PathAction("StartEditablePathButton", "Новый путь", GlyphNewPath, _editor.StartEditablePath,
                PanelDemoKind.ModePath, "Новый путь", "Щелчки по карте ставят узлы, Enter завершает, Esc останавливает — нарисованное остаётся."),
            PathAction("InsertEditablePathNodeButton", "+ узел", GlyphAddNode, _editor.InsertEditablePathNode,
                PanelDemoKind.AddNode, "Добавить узел", "Новый узел на отрезке пути. Перетаскивание узла меняет изгиб, ассеты следуют за линией."),
            PathAction("DeleteEditablePathNodeButton", "− узел", GlyphDeleteNode, _editor.DeleteEditablePathNode,
                PanelDemoKind.DeleteNode, "Удалить узел", "Выбранный узел убирается, линия спрямляется. В черновике — последний узел."),
            PathAction("SelectPathObjectButton", "Выбрать", GlyphPick, _editor.PickEditablePathObject,
                PanelDemoKind.PickObject, "Выбрать объект пути", "Следующий щелчок по карте выделяет один ассет внутри пути."),
            _pathDetach,
            PathAction("ApplyEditablePathSettingsButton", "Применить", GlyphApply, _editor.ApplyStampSettingsToEditablePath,
                PanelDemoKind.ApplyToPath, "Применить ассет к пути", "Ассеты пути получают масштаб, поворот, уровень и отражение из «Нового ассета»."),
            PathAction("BakeEditablePathButton", "В объекты", GlyphBake, _editor.BakeEditablePath,
                PanelDemoKind.Bake, "В обычные объекты", "Линия пути исчезает, ассеты остаются отдельными объектами."),
            PathAction("DeleteEditablePathButton", "Удалить", MapIcons.LineTrash, _editor.DeleteEditablePath,
                PanelDemoKind.DeletePath, "Удалить путь", "Убирает линию и все её ассеты. Ctrl+Z возвращает.", danger: true),
        } };

        var content = new StackPanel
        {
            Spacing = 6, Children =
            {
                _pathPicker, _pathSummary, _pathFinish, _pathCalcCancel, _pathRecalc,
                spacing, seed, actions,
            },
        };
        _pathPanel = new Ui.SectionPanel("EditablePathSection", "Путь", content, true);
        return _pathPanel;
    }

    /// <summary>One command of the path: a small glyph over a one-word caption, the full name on its card.</summary>
    private Button PathAction(string name, string caption, string glyph, Action action, PanelDemoKind demo, string title, string text,
        bool danger = false)
    {
        var button = new Button
        {
            Content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, Children =
            {
                MapIcons.Grid24(glyph, 17, danger ? Application.Current?.FindResource("MapDangerBrush") as IBrush : null),
                new TextBlock { Text = caption, FontSize = 10.5, TextAlignment = Avalonia.Media.TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 44, Padding = new Thickness(2, 4), Margin = new Thickness(0, 0, 4, 4),
        }.Named(name).Classed("compact");
        if (danger) button.Classes.Add("destructive");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => action();
        return Explain(button, demo, title, text);
    }

    private void SyncEditablePathPanel()
    {
        if (_pathPanel is null || _pathPicker is null || _pathSummary is null) return;
        _syncingPaths = true;
        try
        {
            var choices = _editor.EditablePaths.Select(p => new PathChoice(p.Id, p.Name)).ToArray();
            if (_pathPicker.ItemsSource is not PathChoice[] previous || !previous.SequenceEqual(choices))
                _pathPicker.ItemsSource = choices;
            _pathPicker.SelectedItem = choices.FirstOrDefault(p => p.Id == _editor.SelectedEditablePath?.Id);
            _pathPicker.IsVisible = choices.Length > 1 || _editor.SelectedEditablePath is null && choices.Length > 0;
            _pathSummary.Text = _editor.EditablePathSummary;
            var steady = _editor.SelectedEditablePath is { } shown && !_editor.IsCreatingEditablePath
                && !_editor.PathCalculationBusy && !_editor.PathCalculationConflict;
            _pathSummary.IsVisible = !steady;
            _pathPanel.SetOpenText(steady && _editor.DisplayedEditablePath is { } p
                ? $"узлов {p.Nodes.Length} · объектов {p.Members.Length}" : "");
            _stampGestureBlock.IsVisible = _editor.SelectedSet is not null || _editor.SelectedEditablePath is not null || _editor.CurrentAreaSource is not null;
            _pathFinish!.IsVisible = _editor.IsCreatingEditablePath;
            _pathDetach!.IsEnabled = _editor.CanDetachPathObject;
            _pathCalcCancel!.IsVisible = _editor.PathCalculationBusy;
            _pathRecalc!.IsVisible = _editor.PathCalculationConflict;
            _pathPanel.IsVisible = _editor.StampPlacementMode == StampPlacementMode.Path
                || _editor.IsCreatingEditablePath || _editor.SelectedEditablePath is not null;
            // «Как ставить» stays with a selected path: its randomness (variants, angle, jitters) is the path's too.
            SyncAppearanceSections();
        }
        finally { _syncingPaths = false; }
    }

    private sealed record PathChoice(Guid Id, string Name)
    { public override string ToString() => Name; }
}
