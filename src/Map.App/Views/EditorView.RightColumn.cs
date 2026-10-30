using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>Which page the right column shows (DEC-070, the owner's pick «Б» of 2026-10-30).</summary>
public enum RightColumnTab { Properties, Content, Layers }

/// <summary>
/// DEC-070 variant 1 with the owner's pick «Б» (2026-10-30): the right of the editor is one column with three tabs -
/// «Свойства» (what will change now), «Содержимое» (land, water, assets, roads, other) and «Слои». «Содержимое» and
/// «Слои» are also two buttons at the right end of the row of sections. Opening a section of the map panel shows its
/// tab; selecting something on the map or in the list brings «Свойства» back.
/// </summary>
public sealed partial class EditorView
{
    private DockPanel _rightColumn = null!;
    private Border _rightTabsRow = null!;
    private RightColumnTab _rightTab = RightColumnTab.Properties;
    private bool _settingRightTab;
    private Map.Core.Documents.LayerId? _lastContextLand;
    private readonly List<(RightColumnTab Tab, Button Button)> _rightTabButtons = [];

    /// <summary>The page the right column shows now.</summary>
    public RightColumnTab RightTab => _rightTab;

    private Control BuildRightColumn(Control contextPanel, Control sidePanel)
    {
        Button Tab(RightColumnTab tab, string name, string text, string tip)
        {
            var button = Ui.TextButton(name, text, () => SetRightTab(tab), "panel-tab");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(2, 4);
            button.WithTip(tip);
            AttachInsideCard(button, () => RightTabCard(tab), PlacementMode.Left);
            _rightTabButtons.Add((tab, button));
            return button;
        }
        _rightTabsRow = new Border
        {
            Child = new UniformGrid
            {
                Rows = 1, Columns = 3,
                Children =
                {
                    Tab(RightColumnTab.Properties, "RightTabProperties", "Свойства", "Что сейчас изменится: выделенное или следующий инструмент."),
                    Tab(RightColumnTab.Content, "RightTabContent", "Содержимое", "Земля, вода, ассеты, дороги и прочее на карте."),
                    Tab(RightColumnTab.Layers, "RightTabLayers", "Слои", "Слои карты снизу вверх и свойства выбранного слоя."),
                },
            },
            Padding = new Thickness(0),
        }.Classed("panel-header").Named("RightColumnTabs");

        // Both pages fill the same column; only the chosen one is shown.
        contextPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        sidePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        contextPanel.Width = double.NaN;
        sidePanel.Width = double.NaN;
        _rightColumn = new DockPanel
        {
            Name = "RightColumn",
            Children = { _rightTabsRow.Docked(Dock.Top), new Panel { Children = { contextPanel, sidePanel } } },
        };
        _editor.PropertyChanged += OnRightColumnEditorChanged;
        SetRightTab(RightColumnTab.Properties);
        return _rightColumn;
    }

    /// <summary>«Содержимое» and «Слои» at the right end of the row of sections.</summary>
    private Control BuildRightColumnButtons()
    {
        var content = Ui.TextButton("RowContentButton", "Содержимое", () => SetRightTab(RightColumnTab.Content), "compact")
            .WithTip("Открыть справа «Содержимое»: земля, вода, ассеты, дороги и прочее на карте.");
        var layers = Ui.TextButton("RowLayersButton", "Слои", () => SetRightTab(RightColumnTab.Layers), "compact")
            .WithTip("Открыть справа «Слои» карты.");
        _rightTabButtons.Add((RightColumnTab.Content, content));
        _rightTabButtons.Add((RightColumnTab.Layers, layers));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 4), Children = { content, layers },
        };
    }

    /// <summary>Shows a page of the right column; the map panel opens on the section that belongs to it.</summary>
    public void SetRightTab(RightColumnTab tab)
    {
        if (_settingRightTab) return;
        _settingRightTab = true;
        try
        {
            if (tab != RightColumnTab.Properties && _contextCollapsed) ToggleContextPanel();
            _rightTab = tab;
            _rightColumn.IsVisible = true;
            _contextPanel.IsVisible = tab == RightColumnTab.Properties;
            _sidePanel.IsVisible = tab != RightColumnTab.Properties;
            ApplyRightColumnWidth();
            if (tab == RightColumnTab.Layers && _editor.PanelSection != PanelSection.Layers)
                ShowPanelSection(PanelSection.Layers);
            else if (tab == RightColumnTab.Content && _editor.PanelSection == PanelSection.Layers)
                ShowPanelSection(_editor.SectionForSelection() ?? PanelSection.Assets);
            foreach (var (candidate, button) in _rightTabButtons) Ui.SetClass(button, "active", candidate == tab);
        }
        finally { _settingRightTab = false; }
    }

    private void OnRightColumnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A section opened while nothing is selected (a link, a script) shows its page. The editor also follows a new
        // selection to its section; then the selection's properties stay in front - that is what will change now.
        // Another tool: its settings are what will change now.
        if (e.PropertyName == nameof(EditorViewModel.ActiveTool) && !_settingRightTab && _rightTab != RightColumnTab.Properties)
        {
            SetRightTab(RightColumnTab.Properties);
            return;
        }
        if (e.PropertyName != nameof(EditorViewModel.PanelSection) || _settingRightTab) return;
        var selected = _editor.SelectionCount > 0 || _editor.SurfaceSelectionCount > 0 || _editor.SelectedLand is not null;
        if (_rightTab == RightColumnTab.Properties && selected) return;
        SetRightTab(_editor.PanelSection == PanelSection.Layers ? RightColumnTab.Layers : RightColumnTab.Content);
    }

    /// <summary>The column of the owner's mockup (340 px); narrower in a narrow window; a thin strip when folded.</summary>
    private const double RightColumnWidth = 340, RightColumnNarrowWidth = 280;

    private void ApplyRightColumnWidth() =>
        _rightColumn.Width = _contextCollapsed ? 42 : Classes.Contains("narrow-window") ? RightColumnNarrowWidth : RightColumnWidth;

    /// <summary>Something new is selected: its properties are what will change now.</summary>
    private void ShowPropertiesForNewSelection()
    {
        if (_rightTab != RightColumnTab.Properties) SetRightTab(RightColumnTab.Properties);
    }
}
