using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The right panel as working sections: Земля, Реки и озёра, Ассеты, Дороги, Прочее and Слои.
/// Each section lists one kind of map content with the actions that fit it, so a road's
/// settings never sit next to a river's outline or the land's dressing. The list itself is one
/// virtualized control shared by the sections; the section decides its rows, its empty state
/// and the property block under it. Слои keeps the real stack of the map with every layer's
/// properties, because the drawing order is one thing and is edited in one place.
/// </summary>
public sealed partial class EditorView
{
    private static readonly PanelSection[] StandardSections =
    [
        PanelSection.Land, PanelSection.Water, PanelSection.Assets,
        PanelSection.Roads, PanelSection.Other, PanelSection.Layers, PanelSection.Light, PanelSection.Styles,
    ];

    // A hex map has no land, water, assets or roads of the standard constructor: its hexes live
    // in hex layers (Слои), and labels and notes are its other objects (Прочее).
    private static readonly PanelSection[] HexSections = [PanelSection.Layers, PanelSection.Other];

    private PanelSection[] Sections => _isHexWorkshop ? HexSections : StandardSections;

    private readonly Dictionary<PanelSection, ToggleButton> _sectionButtons = [];
    private readonly ComboBox _compactSectionPicker = new() { Name = "CompactPanelSectionPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly RetainedSlot _sectionHost = new() { Name = "PanelSectionHost" };
    private readonly RetainedSlot _sectionFooter = new() { Name = "PanelSectionFooter" };
    private readonly TextBlock _sectionCaption = new TextBlock { Name = "PanelSectionCaption", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
    private Control _objectsBody = null!;
    private Control _layersBody = null!;
    private Control _otherFilterStrip = null!;
    private Control _compositionsBlock = null!;
    private readonly Dictionary<PanelSection, Control> _sectionFooters = [];
    private readonly List<Action> _sectionSyncs = [];

    /// <summary>The number of working sections of the right panel.</summary>
    public int WorkspaceTabCount => Sections.Length;

    /// <summary>The section currently open in the right panel.</summary>
    public PanelSection CurrentPanelSection => _editor.PanelSection;

    private static string SectionLineIcon(PanelSection section) => section switch
    {
        PanelSection.Land => MapIcons.LineLand,
        PanelSection.Water => MapIcons.LineWater,
        PanelSection.Assets => MapIcons.LineAssets,
        PanelSection.Roads => MapIcons.LineRoad,
        PanelSection.Other => MapIcons.LineOther,
        PanelSection.Light => MapIcons.LineSun,
        PanelSection.Styles => MapIcons.LinePalette,
        _ => MapIcons.LineLayers,
    };

    /// <summary>One short word per tab, so all seven fit in one row; the tooltip keeps the full name.</summary>
    private static string SectionShortTitle(PanelSection section) => section switch
    {
        PanelSection.Water => "Вода",
        PanelSection.Light => "Свет",
        PanelSection.Styles => "Стили",
        _ => ObjectTreeRows.SectionTitle(section),
    };

    private static string SectionIcon(PanelSection section) => section switch
    {
        PanelSection.Land => MapIcons.LayerLand,
        PanelSection.Water => MapIcons.River,
        PanelSection.Assets => MapIcons.ObjectStamp,
        PanelSection.Roads => MapIcons.Road,
        PanelSection.Other => MapIcons.Objects,
        _ => MapIcons.Layers,
    };

    private static string SectionHint(PanelSection section) => section switch
    {
        PanelSection.Land => "Каждая суша — отдельный слой земли. Выбор строки выделяет её на холсте; цвет, материал и берег — в инструменте «Земля».",
        PanelSection.Water => "Реки и озёра карты. Строка выделяет водную систему; форма и оформление — в инструменте «Река».",
        PanelSection.Assets => "Ассеты, уже размещённые на карте. Новые берутся из каталога ассетов.",
        PanelSection.Roads => "Дорожные сети карты. Строка выделяет сеть; узлы и оформление — в инструменте «Дорога».",
        PanelSection.Other => "Текст, линии, фигуры, композиции и прочие объекты карты.",
        PanelSection.Light => "Время суток, объём гор, тени, туман, огни и цвет — у каждого свой переключатель и сила.",
        PanelSection.Styles => "Вся карта в другой технике: тушь кистью или пером. Подписи остаются чёткими; у каждого эффекта свой переключатель и сила.",
        _ => "Настоящий порядок слоёв карты снизу вверх и свойства выбранного слоя. Реки и дороги лежат в слоях объектов.",
    };

    /// <summary>The section switcher and the body below it, in place of the former two tabs.</summary>
    private Control BuildSectionedPanel()
    {
        // One row of six tabs: a glyph over a short word. The old 3×2 grid of filled buttons and
        // the paragraph under it took a third of the panel before the first row of the list.
        var switcher = new Avalonia.Controls.Primitives.UniformGrid { Rows = 1, Columns = Sections.Length }.Named("PanelSectionSwitcher");
        foreach (var section in Sections)
        {
            var captured = section;
            var title = ObjectTreeRows.SectionTitle(section);
            var button = new ToggleButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        // Свет is the map's own category, not a kind of object: its sun is always brass,
                        // so the tab is found at a glance among the six glyphs (owner 2026-10-24).
                        new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Child = section == PanelSection.Light
                                ? MapIcons.Grid24(SectionLineIcon(section), 20, Application.Current?.FindResource("MapAccentBrush") as IBrush, 1.9)
                                : MapIcons.Grid24(SectionLineIcon(section), 18),
                        },
                        new TextBlock { Text = SectionShortTitle(section) },
                    },
                },
            }.Named("PanelSection" + section).Classed("section-tab", "panel-section");
            AutomationProperties.SetName(button, "Раздел панели: " + title);
            ToolTip.SetTip(button, title + ". " + SectionHint(section));
            button.Click += (_, _) => ShowPanelSection(captured);
            _sectionButtons[section] = button;
            switcher.Children.Add(button);
        }

        var switcherHost = new Border
        {
            Padding = new Thickness(6, 6, 6, 0),
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = switcher,
        };

        _compactSectionPicker.ItemsSource = Sections.Select(ObjectTreeRows.SectionTitle).ToArray();
        _compactSectionPicker.Margin = new Thickness(12, 8, 12, 8);
        AutomationProperties.SetName(_compactSectionPicker, "Раздел рабочей панели");
        _compactSectionPicker.SelectionChanged += (_, _) =>
        {
            var index = _compactSectionPicker.SelectedIndex;
            if (index >= 0 && index < Sections.Length && Sections[index] != _editor.PanelSection)
                ShowPanelSection(Sections[index]);
        };

        _objectsBody = BuildObjectsTab();
        _layersBody = BuildLayersTab();
        _sectionFooters[PanelSection.Land] = BuildLandSectionFooter();
        _sectionFooters[PanelSection.Water] = BuildWaterSectionFooter();
        _sectionFooters[PanelSection.Assets] = BuildAssetsSectionFooter();
        _sectionFooters[PanelSection.Roads] = BuildRoadsSectionFooter();
        _sectionFooters[PanelSection.Other] = _compositionsBlock;

        var body = new DockPanel
        {
            Children =
            {
                switcherHost.Docked(Dock.Top),
                _compactSectionPicker.Docked(Dock.Top),
                new Border { Padding = new Thickness(12, 0, 12, 4), Child = _sectionCaption, IsVisible = false }.Docked(Dock.Top),
                _sectionHost,
            },
        };
        // A short window needs room for the map rows; the same six sections remain available
        // in one picker instead of three rows of buttons.
        SizeChanged += (_, _) =>
        {
            // One row of tabs fits almost any height; the picker is left for very short windows.
            var compact = Bounds.Height < 560;
            switcherHost.IsVisible = !compact;
            _compactSectionPicker.IsVisible = compact;
        };
        SyncPanelSection();
        return body;
    }

    /// <summary>
    /// Opens a section. The view model owns the choice (the row filter and Ctrl+A follow it);
    /// here the body, the footer and the switcher are brought in line. Nothing on the canvas
    /// changes: no layer, no document, no history entry.
    /// </summary>
    public void ShowPanelSection(PanelSection section)
    {
        var searchChanged = section != _editor.PanelSection && !string.IsNullOrEmpty(_objectSearch.Text);
        _editor.ShowPanelSection(section);
        // The search box follows the view model without a second rebuild through TextChanged.
        if (searchChanged) _objectSearch.Text = "";
        SyncPanelSection();
        SyncObjectList();
    }

    private void SyncPanelSection()
    {
        _compactSectionPicker.SelectedIndex = Array.IndexOf(Sections, _editor.PanelSection);
        var section = _editor.PanelSection;
        foreach (var (candidate, button) in _sectionButtons)
        {
            var active = candidate == section;
            button.IsChecked = active;
            Ui.SetClass(button, "active", active);
        }
        // The tabs show only glyphs, so the caption names the open section first.
        _sectionCaption.Text = ObjectTreeRows.SectionTitle(section) + " · " + SectionHint(section);
        var body = section switch
        {
            PanelSection.Layers => _layersBody,
            PanelSection.Light => LightBody(),
            PanelSection.Styles => StyleBody(),
            _ => _objectsBody,
        };
        if (!ReferenceEquals(_sectionHost.Content, body)) _sectionHost.Content = body;
        if (section != PanelSection.Light && _editor.LightPlacementArmed) _editor.ArmLightPlacement(false);
        if (section == PanelSection.Light) SyncLightSection();
        if (section == PanelSection.Styles) SyncStyleSection();
        _otherFilterStrip.IsVisible = section == PanelSection.Other;
        var footer = _sectionFooters.GetValueOrDefault(section);
        if (!ReferenceEquals(_sectionFooter.Content, footer)) _sectionFooter.Content = footer;
        SyncSectionFooter();
    }

    /// <summary>Property blocks read the current selection; every one refreshes here.</summary>
    private void SyncSectionFooter()
    {
        foreach (var sync in _sectionSyncs) sync();
    }

    /// <summary>
    /// A selection made on the canvas opens the section that lists it, so a click on a river
    /// shows the river's row and its actions, not a list of assets. Clearing the selection
    /// leaves the panel where it is.
    /// </summary>
    private string? _followedSelection;

    private void FollowSelectionToSection()
    {
        // Only a change of selection moves the panel; a tool switch or a property refresh with
        // the same selection leaves the user's chosen section alone.
        var signature = string.Join(",", _editor.Session.SelectedObjectIds.Select(id => id.ToString()))
            + "|" + string.Join(",", _editor.WaterSelectionIds) + "|" + string.Join(",", _editor.RoadSelectionIds)
            + "|" + _editor.SelectedLand?.Id;
        if (signature == _followedSelection) return;
        _followedSelection = signature;
        _scrollListToSelection = true;
        if (_editor.SectionForSelection() is { } section && section != _editor.PanelSection)
        {
            ShowPanelSection(section);
        }
        // In Assets the selection opens the asset it belongs to, then the list scrolls to it.
        var openBefore = _editor.OpenAssetKey;
        if (_editor.PanelSection == PanelSection.Assets) _editor.RevealSelectionInAssetKinds();
        if (_editor.OpenAssetKey != openBefore) SyncObjectList();
        else Dispatcher.UIThread.Post(ScrollObjectListToSelection, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ footers

    /// <summary>A "−  value  +" row for a terrain level, driven by the view model's shift commands.</summary>
    private Control LevelRow(string name, string label, Func<int?> read, Func<bool> enabled, Action<int> shift)
    {
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 34, TextAlignment = Avalonia.Media.TextAlignment.Center }
            .Named(name + "Value");
        var down = Ui.IconButton(name + "Down", label + " ниже", MapIcons.ArrowDown, () => { shift(-1); RefreshAfterSelectionCommand(); });
        var up = Ui.IconButton(name + "Up", label + " выше", MapIcons.ArrowUp, () => { shift(+1); RefreshAfterSelectionCommand(); });
        _sectionSyncs.Add(() =>
        {
            var on = enabled();
            var level = read();
            value.Text = !on ? "—" : level is { } number ? EditorViewModel.FormatSublayer(number) : "разные";
            down.IsEnabled = on; up.IsEnabled = on;
            AutomationProperties.SetName(value, label + ": " + value.Text);
        });
        return new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                Ui.ParameterLabel(label, 92).Docked(Dock.Left),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4,
                    Children = { down, value, up },
                },
            },
        };
    }

    private Control OrderRow(string name, string label, Func<bool> enabled, Action<int> reorder)
    {
        var down = Ui.IconButton(name + "Down", label + ": ниже", MapIcons.ArrowDown, () => { reorder(-1); RefreshAfterSelectionCommand(); });
        var up = Ui.IconButton(name + "Up", label + ": выше", MapIcons.ArrowUp, () => { reorder(+1); RefreshAfterSelectionCommand(); });
        _sectionSyncs.Add(() => { var on = enabled(); down.IsEnabled = on; up.IsEnabled = on; });
        return new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                Ui.ParameterLabel(label, 92).Docked(Dock.Left),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { up, down } },
            },
        };
    }

    private Button FooterButton(string name, string text, string icon, Action action, params string[] classes)
    {
        var button = Ui.CommandButton(name, text, icon, action, [.. classes, "compact"]);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private Control BuildLandSectionFooter()
    {
        var summary = new TextBlock { Name = "LandSectionSummary", TextWrapping = TextWrapping.Wrap };
        var level = LevelRow("LandSectionLevel", "Уровень", () => _editor.LandTargetLevel,
            () => _editor.LandTarget is { Locked: false }, delta => _editor.ShiftLandLevel(delta));
        var order = OrderRow("LandSectionOrder", "Слой в стопке", () => _editor.SelectedLand is not null,
            direction => { if (_editor.SelectedLand is { } land) { _editor.SelectLayer(land.Id); _editor.ReorderActiveLayer(direction > 0); } });
        var visibility = FooterButton("LandSectionVisibility", "Скрыть эту сушу", MapIcons.Eye,
            () => { if (_editor.SelectedLand is { } land) { _editor.ToggleLayerVisibility(land.Id); RefreshAfterSelectionCommand(); } });
        var locking = FooterButton("LandSectionLock", "Заблокировать эту сушу", MapIcons.Lock,
            () => { if (_editor.SelectedLand is { } land) { _editor.ToggleLayerLock(land.Id); RefreshAfterSelectionCommand(); } });
        var style = FooterButton("LandSectionStyle", "Цвет, материал и берег…", MapIcons.LayerLand,
            () => { if (_editor.SelectedLand is { } land) _editor.SelectLayer(land.Id); _editor.ActiveTool = EditorTool.Land; SyncSidePanels(); SyncContextArea(); });
        var add = FooterButton("LandSectionAdd", "Новый слой земли", MapIcons.LayerLand,
            () => { _editor.AddLandLayer(); SyncSidePanels(); SyncContextArea(); }, "accent", "create-hero");
        _sectionSyncs.Add(() =>
        {
            var land = _editor.SelectedLand;
            summary.Text = land is null
                ? "Суша не выбрана. Выберите строку или щёлкните землю на холсте."
                : $"«{land.Name}» · уровень {EditorViewModel.FormatSublayer(land.Level)}" + (land.Locked ? " · заблокирована" : "") + (land.Visible ? "" : " · скрыта");
            var has = land is not null;
            visibility.IsEnabled = has; locking.IsEnabled = has; style.IsEnabled = has || _editor.ActiveLayer is LandLayer;
            Ui.SetCommandText(visibility, land is { Visible: false } ? "Показать эту сушу" : "Скрыть эту сушу");
            Ui.SetCommandText(locking, land is { Locked: true } ? "Разблокировать эту сушу" : "Заблокировать эту сушу");
        });
        foreach (var hidden in new Control[] { level, order, visibility, locking }) hidden.IsVisible = false;
        summary.Classes.Add("faint"); summary.Classes.Add("role-caption");
        return new Border
        {
            Child = new StackPanel { Spacing = 4, Children = { summary, level, order, visibility, locking, style, add } },
        }.Classed("panel-footer").Named("LandSectionFooter");
    }

    private Control BuildWaterSectionFooter()
    {
        var summary = new TextBlock { Name = "WaterSectionSummary", TextWrapping = TextWrapping.Wrap };
        var level = LevelRow("WaterSectionLevel", "Уровень", () => _editor.WaterSelectionLevelValue,
            () => _editor.WaterSelectionCount > 0, delta => _editor.ShiftSelectedWaterLevel(delta));
        var order = OrderRow("WaterSectionOrder", "Порядок воды", () => _editor.WaterSelectionCount > 0,
            direction => _editor.ReorderSelectedWater(direction));
        var visibility = FooterButton("WaterSectionVisibility", "Скрыть только эту воду", MapIcons.Eye,
            () => { _editor.ToggleSelectedWaterVisible(); RefreshAfterSelectionCommand(); });
        var locking = FooterButton("WaterSectionLock", "Заблокировать только эту воду", MapIcons.Lock,
            () => { _editor.ToggleSelectedWaterLock(); RefreshAfterSelectionCommand(); });
        var edit = FooterButton("WaterSectionEdit", "Изменить в панели слева", MapIcons.River,
            () => { _editor.ActiveTool = EditorTool.River; SyncContextArea(); });
        var create = FooterButton("WaterSectionCreate", "Новая река или озеро…", MapIcons.River,
            () => { _editor.ClearWaterSelection(); _editor.ActiveTool = EditorTool.River; SyncContextArea(); }, "accent", "create-hero");
        var delete = FooterButton("WaterSectionDelete", "Удалить выбранную воду", MapIcons.Trash,
            () => { _editor.DeleteSelectedWaters(); RefreshAfterSelectionCommand(); }, "destructive");
        DynamicTip(delete, () => $"Удалить только выбранные реки и озёра; слой и его объекты остаются. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");
        _sectionSyncs.Add(() =>
        {
            var count = _editor.WaterSelectionCount;
            summary.Text = count == 0 ? "Вода не выбрана. Выберите строку или щёлкните реку на холсте." : _editor.WaterSelectionSummary;
            visibility.IsEnabled = count > 0; locking.IsEnabled = count > 0; edit.IsEnabled = count > 0; delete.IsEnabled = count > 0;
            Ui.SetCommandText(visibility, count > 0 && !_editor.SelectedWaterVisible ? "Показать только эту воду" : "Скрыть только эту воду");
            Ui.SetCommandText(locking, count > 0 && _editor.SelectedWaterLocked ? "Разблокировать только эту воду" : "Заблокировать только эту воду");
        });
        foreach (var hidden in new Control[] { level, order, visibility, locking }) hidden.IsVisible = false;
        // Deleting whole selected networks is not the level-aware delete of the left panel: it stays.
        delete.Classes.Remove("destructive"); delete.Classes.Add("danger-text");
        summary.Classes.Add("faint"); summary.Classes.Add("role-caption");
        return new Border
        {
            Child = new StackPanel { Spacing = 4, Children = { summary, level, order, visibility, locking, edit, create, delete } },
        }.Classed("panel-footer").Named("WaterSectionFooter");
    }

    private Control BuildRoadsSectionFooter()
    {
        var summary = new TextBlock { Name = "RoadSectionSummary", TextWrapping = TextWrapping.Wrap };
        var level = LevelRow("RoadSectionLevel", "Уровень", () => _editor.RoadSelectionLevelValue,
            () => _editor.RoadSelectionCount > 0, delta => _editor.ShiftSelectedRoadLevel(delta));
        var order = OrderRow("RoadSectionOrder", "Порядок с водой", () => _editor.RoadSelectionCount > 0,
            direction => _editor.ReorderSelectedSurfaces(direction));
        var visibility = FooterButton("RoadSectionVisibility", "Скрыть только эту дорогу", MapIcons.Eye,
            () => { _editor.ToggleSelectedRoadVisible(); RefreshAfterSelectionCommand(); });
        var locking = FooterButton("RoadSectionLock", "Заблокировать только эту дорогу", MapIcons.Lock,
            () => { _editor.ToggleSelectedRoadLock(); RefreshAfterSelectionCommand(); });
        var edit = FooterButton("RoadSectionEdit", "Изменить в панели слева", MapIcons.Road,
            () => { if (_editor.RoadSelectionIds.Count > 0) _editor.SelectRoad(_editor.RoadSelectionIds[0]); else _editor.ActiveTool = EditorTool.Road; SyncContextArea(); });
        var create = FooterButton("RoadSectionCreate", "Новая дорога…", MapIcons.Road,
            () => { _editor.ClearRoadSelection(); _editor.ActiveTool = EditorTool.Road; SyncContextArea(); }, "accent");
        var delete = FooterButton("RoadSectionDelete", "Удалить выбранные дороги", MapIcons.Trash,
            () => { _editor.DeleteSelectedRoads(); RefreshAfterSelectionCommand(); }, "destructive");
        DynamicTip(delete, () => $"Удалить только выбранные дорожные сети; слой, вода и объекты остаются. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");
        _sectionSyncs.Add(() =>
        {
            var count = _editor.RoadSelectionCount;
            summary.Text = count == 0 ? "Дорога не выбрана. Выберите строку или щёлкните дорогу на холсте." : _editor.RoadSelectionSummary;
            visibility.IsEnabled = count > 0; locking.IsEnabled = count > 0; edit.IsEnabled = count > 0; delete.IsEnabled = count > 0;
            Ui.SetCommandText(visibility, count > 0 && !_editor.SelectedRoadVisible ? "Показать только эту дорогу" : "Скрыть только эту дорогу");
            Ui.SetCommandText(locking, count > 0 && _editor.SelectedRoadLocked ? "Разблокировать только эту дорогу" : "Заблокировать только эту дорогу");
        });
        foreach (var hidden in new Control[] { level, order, visibility, locking }) hidden.IsVisible = false;
        // Deleting whole selected networks is not the level-aware delete of the left panel: it stays.
        delete.Classes.Remove("destructive"); delete.Classes.Add("danger-text");
        summary.Classes.Add("faint"); summary.Classes.Add("role-caption");
        return new Border
        {
            Child = new StackPanel { Spacing = 4, Children = { summary, level, order, visibility, locking, edit, create, delete } },
        }.Classed("panel-footer").Named("RoadsSectionFooter");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<SceneObject>, System.Runtime.CompilerServices.StrongBox<int>> StampCounts = [];

    /// <summary>
    /// Stamps in one object list, counted once per list: lists are immutable, and the footer is
    /// refreshed with every panel refresh (a scan of 100 000 objects each time).
    /// </summary>
    private static int StampCount(IReadOnlyList<SceneObject> objects) =>
        StampCounts.GetValue(objects, static list => new(list.Count(o => o.Kind == SceneObjectKind.Stamp))).Value;

    private Control BuildAssetsSectionFooter()
    {
        var open = FooterButton("AssetsSectionCatalog", "Каталог ассетов…", MapIcons.Assets,
            () => { _editor.ActiveTool = EditorTool.Stamp; OpenAssetCatalog(focusSearch: true); }, "accent");
        var summary = new TextBlock { Name = "AssetsSectionSummary", TextWrapping = TextWrapping.Wrap };
        _sectionSyncs.Add(() =>
        {
            var stamps = _editor.Layers.OfType<ObjectLayer>().Sum(layer => StampCount(layer.Objects));
            summary.Text = stamps == 0
                ? "На карте пока нет размещённых ассетов."
                : $"Размещено: {EditorLabels.AssetCount(stamps)}. Свойства выбранного — в панели слева.";
        });
        summary.Classes.Add("faint"); summary.Classes.Add("role-caption");
        return new Border { Child = new StackPanel { Spacing = 4, Children = { summary, open } } }
            .Classed("panel-footer").Named("AssetsSectionFooter");
    }

    /// <summary>What the list says when a section has nothing to show, and what to do about it.</summary>
    private Control SectionEmptyState(PanelSection section, bool searching)
    {
        if (searching)
            return Ui.EmptyState("NoObjectsText", SectionIcon(section), "Ничего не найдено", "Измените запрос или откройте другой раздел.");
        return section switch
        {
            PanelSection.Land => Ui.EmptyState("NoObjectsText", MapIcons.LayerLand, "На карте нет земли",
                "Инструмент «Земля» рисует сушу; кнопка ниже добавляет пустой слой земли."),
            PanelSection.Water => Ui.EmptyState("NoObjectsText", MapIcons.River, "На карте нет рек и озёр",
                "Инструмент «Река» создаёт русло кликами или озеро по контуру."),
            PanelSection.Assets => Ui.EmptyState("NoObjectsText", MapIcons.ObjectStamp, "На карте нет ассетов",
                "Выберите ассет в каталоге и укажите место на холсте."),
            PanelSection.Roads => Ui.EmptyState("NoObjectsText", MapIcons.Road, "На карте нет дорог",
                "Инструмент «Дорога» ведёт дорогу узлами или от руки."),
            _ => Ui.EmptyState("NoObjectsText", MapIcons.Objects, "Нет прочих объектов",
                "Линии, фигуры и текст добавляются одноимёнными инструментами."),
        };
    }
}
