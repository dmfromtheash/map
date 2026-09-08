using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.Assets;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The asset catalog window: a navigator of collections on the left, one toolbar (search, upload,
/// filters, tile size, sort, grouping, view menu) on top and a grid of square tiles grouped by
/// collection underneath. A tile is chosen by one click and stays chosen when the window closes;
/// a double click chooses and returns to the canvas with the stamp armed. A set with several
/// variants carries a count badge; the badge unfolds the variants inline, right after the cover.
/// </summary>
public sealed partial class EditorView
{
    private sealed record GalleryCell(AssetSetSummary Set, AssetVariant? Variant, int SourceRow);
    private sealed record GalleryRow(string? GroupKey, string? Heading, int HeadingCount, IReadOnlyList<GalleryCell> Cells);

    private const double GalleryTileGap = 8;
    private const double GalleryMinTile = 64;
    private const double GalleryMaxTile = 176;
    private const double GalleryCaptionHeight = 18;

    private readonly HashSet<string> _expandedGallerySets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedGalleryGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedCollections = new(StringComparer.Ordinal);
    private readonly HashSet<Button> _realizedGalleryTiles = [];
    private IReadOnlyList<AssetLibraryRow>? _gallerySourceRows;
    private IReadOnlyList<GalleryRow> _galleryRows = [];
    private int _galleryColumns = 4;
    private double _galleryTileSize = 112;
    private double _lastBuiltTile = -1;
    private bool _galleryShowNames;
    private bool _galleryResizeQueued;
    private bool _galleryRangeQueued;
    private CancellationTokenSource? _librarySearchCancellation;
    private Button _librarySortMenu = null!;
    private Button _libraryGroupMenu = null!;
    private MenuItem _libraryShowNamesItem = null!;
    private WrapPanel _libraryFilterChips = null!;
    private Popup _libraryTagSuggestions = null!;
    private StackPanel _libraryTagSuggestionItems = null!;
    private Slider _libraryTileSizeSlider = null!;
    private Flyout _libraryFilterFlyout = null!;
    public Window? AssetCatalogWindowForSmoke => _assetCatalogWindow;
    public int GalleryRealizedCards => _realizedGalleryTiles.Count;
    private sealed record CompactVariant(AssetVariant Variant, int Index);
    private IReadOnlyList<AssetVariant>? _compactVariants;

    // ------------------------------------------------------------------ window layout

    private Control BuildAssetsTab()
    {
        BuildLibraryDiagnosticBanner();
        BuildLibraryGrid();
        _selectedSetBlock = BuildSelectedSetBlock();
        if (_editor.LibraryGrouping == AssetGrouping.None) _editor.LibraryGrouping = AssetGrouping.Collection;

        var filters = BuildLibraryFilterControls();
        _libraryFilterFlyout = new Flyout
        {
            Content = new ScrollViewer { Content = filters, MaxHeight = 460, Width = 300,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        // ---- navigator
        var navigatorMenu = new MenuFlyout();
        navigatorMenu.Items.Add(MenuEntry("Свернуть все", () => { foreach (var id in AllCollectionIds()) _collapsedCollections.Add(id); SyncCollectionTree(); }));
        navigatorMenu.Items.Add(MenuEntry("Развернуть все", () => { _collapsedCollections.Clear(); SyncCollectionTree(); }));
        var navigatorMore = new Button { Content = new TextBlock { Text = "⋯" }, Flyout = navigatorMenu }
            .Named("LibraryNavigatorMenu").Classed("icon");
        AutomationProperties.SetName(navigatorMore, "Меню навигатора");
        ToolTip.SetTip(navigatorMore, "Свернуть или развернуть все коллекции");
        var navigatorHeader = new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(6, 0, 0, 6),
            Children =
            {
                navigatorMore.Docked(Dock.Right),
                new TextBlock { Text = "Каталог", VerticalAlignment = VerticalAlignment.Center }.Classed("role-panel"),
            },
        };
        var navigator = new DockPanel
        {
            Width = 210, LastChildFill = true,
            Children =
            {
                navigatorHeader.Docked(Dock.Top),
                new ScrollViewer { Content = _collectionTree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            },
        };

        // ---- toolbar
        var uploadMenu = new MenuFlyout();
        uploadMenu.Items.Add(MenuEntry("Добавить файл…", () => _ = ImportAssetAsync(linked: false), "ImportAssetButton"));
        uploadMenu.Items.Add(MenuEntry("Добавить набор…", () => _ = ImportAssetSetAsync(), "ImportAssetSetButton"));
        uploadMenu.Items.Add(MenuEntry("Связать файл…", () => _ = ImportAssetAsync(linked: true), "LinkAssetButton"));
        var upload = ToolbarButton("LibraryUploadMenu", "Загрузить", MapIcons.Import, "Добавить свои PNG, JPEG, WebP или SVG");
        upload.Flyout = uploadMenu;

        var addFilter = ToolbarButton("LibraryAddFilterButton", "Фильтр", MapIcons.Plus, "Тег, избранное, недавние, на карте, формат, дата");
        addFilter.Flyout = _libraryFilterFlyout;

        _libraryFilterChips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _libraryTagSuggestionItems = new StackPanel { Spacing = 2 };
        _libraryTagSuggestions = new Popup
        {
            PlacementTarget = _assetSearch, Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Padding = new Thickness(6), MinWidth = 240, MaxHeight = 260,
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
                Child = new ScrollViewer { Content = _libraryTagSuggestionItems, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            },
        };
        _assetSearch.Width = 260;
        _assetSearch.VerticalAlignment = VerticalAlignment.Center;
        _assetSearch.KeyDown += (_, e) => { if (e.Key == Key.Escape && _libraryTagSuggestions.IsOpen) { _libraryTagSuggestions.IsOpen = false; e.Handled = true; } };
        var searchHost = new Grid { Children = { _assetSearch, _libraryTagSuggestions } };

        var sortMenu = new MenuFlyout();
        foreach (var (order, title) in new[]
        {
            (AssetSortOrder.NameAscending, "Название (А–Я)"), (AssetSortOrder.NameDescending, "Название (Я–А)"),
            (AssetSortOrder.ImportedNewest, "Дата (новые)"), (AssetSortOrder.ImportedOldest, "Дата (старые)"),
        })
        {
            var captured = order;
            sortMenu.Items.Add(MenuEntry(title, () => { _editor.LibrarySort = captured; SyncAssetList(); }, "LibrarySort" + order));
        }
        _librarySortMenu = ToolbarButton("LibrarySortMenu", "Сортировка", null, "Порядок карточек");
        _librarySortMenu.Flyout = sortMenu;

        var groupMenu = new MenuFlyout();
        foreach (var (grouping, title) in new[]
        {
            (AssetGrouping.Collection, "Коллекция"), (AssetGrouping.Tag, "Тег"), (AssetGrouping.None, "Без групп"),
        })
        {
            var captured = grouping;
            groupMenu.Items.Add(MenuEntry(title, () => { _editor.LibraryGrouping = captured; SyncAssetList(); }, "LibraryGroup" + grouping));
        }
        _libraryGroupMenu = ToolbarButton("LibraryGroupMenu", "Группировка", null, "Чем разделять карточки на группы");
        _libraryGroupMenu.Flyout = groupMenu;

        var viewMenu = new MenuFlyout();
        _libraryShowNamesItem = MenuEntry("Показывать названия", () => { _galleryShowNames = !_galleryShowNames; RebuildGalleryRows(); }, "LibraryShowNamesItem");
        _libraryShowNamesItem.ToggleType = MenuItemToggleType.CheckBox;
        viewMenu.Items.Add(_libraryShowNamesItem);
        viewMenu.Items.Add(new Separator());
        viewMenu.Items.Add(MenuEntry("Раскрыть все варианты", () =>
        {
            foreach (var set in _editor.LibrarySets) if (set.IsSet) _expandedGallerySets.Add(set.SetId);
            RebuildGalleryRows();
        }, "LibraryExpandVariantsItem"));
        viewMenu.Items.Add(MenuEntry("Свернуть все варианты", () => { _expandedGallerySets.Clear(); RebuildGalleryRows(); }, "LibraryCollapseVariantsItem"));
        viewMenu.Items.Add(MenuEntry("Раскрыть все группы", () => { _collapsedGalleryGroups.Clear(); RebuildGalleryRows(); }, "LibraryExpandGroupsItem"));
        viewMenu.Items.Add(MenuEntry("Свернуть все группы", () =>
        {
            foreach (var row in _editor.LibraryRows) if (row.IsHeader) _collapsedGalleryGroups.Add(row.Key);
            RebuildGalleryRows();
        }, "LibraryCollapseGroupsItem"));
        viewMenu.Items.Add(new Separator());
        viewMenu.Items.Add(MenuEntry("Загрузить свой ассет…", () => _ = ImportAssetAsync(linked: false), "LibraryUploadItem"));
        var view = new Button { Content = MapIcons.Stroked(MapIcons.Sort, 17), Flyout = viewMenu }.Named("LibraryViewMenu").Classed("icon");
        AutomationProperties.SetName(view, "Вид галереи");
        ToolTip.SetTip(view, "Названия, варианты, группы, загрузка");

        _libraryTileSizeSlider = new Slider
        {
            Minimum = GalleryMinTile, Maximum = GalleryMaxTile, Value = _galleryTileSize, Width = 110,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        }.Named("LibraryTileSizeSlider");
        AutomationProperties.SetName(_libraryTileSizeSlider, "Размер плиток");
        ToolTip.SetTip(_libraryTileSizeSlider, "Размер плиток галереи");
        _libraryTileSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            var size = Math.Round(_libraryTileSizeSlider.Value / 8) * 8;
            if (Math.Abs(size - _galleryTileSize) < 0.5) return;
            _galleryTileSize = size;
            QueueGalleryRelayout();
        };
        var toolsRight = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Child = MapIcons.Stroked(MapIcons.Search, 14), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) },
                _libraryTileSizeSlider, _librarySortMenu, _libraryGroupMenu, view,
            },
        };
        var row1 = new DockPanel { LastChildFill = true, Children = { toolsRight.Docked(Dock.Right), _breadcrumbs } };
        var row2 = new WrapPanel
        {
            Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6,
            Children = { searchHost, upload, addFilter, _libraryFilterChips, _clearFiltersButton },
        };
        _clearFiltersButton.VerticalAlignment = VerticalAlignment.Center;
        var top = new StackPanel { Spacing = 8, Children = { row1, row2, _libraryDiagnostic } };

        // ---- bottom: count and the organisation of the chosen set
        var managementScroll = new ScrollViewer { MaxHeight = 200, Content = _selectedSetBlock, ClipToBounds = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var management = Disclosure("LibraryOrganizationDetails", "Организация выбранного набора", managementScroll, expanded: false);
        _libraryCountText.VerticalAlignment = VerticalAlignment.Top;
        _libraryCountText.Margin = new Thickness(0, 8, 12, 0);
        _libraryCountText.Classes.Add("faint");
        var bottom = new DockPanel { LastChildFill = true, Children = { _libraryCountText.Docked(Dock.Left), management } };

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
            Children = { top, _libraryGrid, bottom } };
        Grid.SetRow(_libraryGrid, 1);
        Grid.SetRow(bottom, 2);
        var body = new Grid { Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12, Children = { navigator, main } };
        Grid.SetColumn(main, 1);
        body.SizeChanged += (_, _) =>
        {
            navigator.Width = body.Bounds.Width < 900 ? 170 : 210;
            managementScroll.MaxHeight = Math.Clamp(body.Bounds.Height * .18, 64, 220);
        };
        return body;
    }

    private static MenuItem MenuEntry(string title, Action action, string? name = null)
    {
        var item = new MenuItem { Header = title };
        if (name is not null) item.Name = name;
        AutomationProperties.SetName(item, title);
        item.Click += (_, _) => action();
        return item;
    }

    private static Button ToolbarButton(string name, string text, string? icon, string hint)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) content.Children.Add(MapIcons.Stroked(icon, 14));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }.Named(name + "Text"));
        content.Children.Add(MapIcons.Stroked(MapIcons.ChevronDown, 11));
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center }.Named(name).Classed("compact");
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, hint);
        return button;
    }

    private IEnumerable<string> AllCollectionIds()
    {
        var stack = new Stack<AssetCollectionNode>(_editor.LibraryCollections);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node.Id;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    // ------------------------------------------------------------------ navigator

    private void SyncCollectionTree()
    {
        _collectionTree.Children.Clear();
        _collectionTree.Children.Add(NavigatorRow("Collection_All", "Все ассеты", _editor.LibrarySets.Count, 0, null,
            _editor.LibraryCollectionId is null, () => { _editor.LibraryCollectionId = null; SyncAssetList(); }));
        Add(_editor.LibraryCollections, 0);
        if (_editor.LibraryCollections.Count == 0)
        {
            _collectionTree.Children.Add(new TextBlock
            {
                Text = "Коллекции появятся после первого импорта.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6),
            }.Classed("faint", "role-caption").Named("NoCollectionsText"));
        }

        void Add(IReadOnlyList<AssetCollectionNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var captured = node.Id;
                var collapsed = _collapsedCollections.Contains(node.Id);
                Action? toggle = node.Children.Count == 0 ? null : () =>
                {
                    if (!_collapsedCollections.Add(captured)) _collapsedCollections.Remove(captured);
                    SyncCollectionTree();
                };
                _collectionTree.Children.Add(NavigatorRow("Collection_" + node.Id, node.Name, node.TotalCount, depth,
                    toggle is null ? null : (toggle, collapsed), _editor.LibraryCollectionId == node.Id,
                    () => { _editor.LibraryCollectionId = captured; SyncAssetList(); }));
                if (!collapsed) Add(node.Children, depth + 1);
            }
        }
    }

    private static Control NavigatorRow(string name, string title, int count, int depth, (Action Toggle, bool Collapsed)? disclosure,
        bool selected, Action activate)
    {
        var content = new DockPanel { LastChildFill = true };
        content.Children.Add(new TextBlock { Text = count.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }
            .Classed("faint", "role-caption").Docked(Dock.Right));
        Control chevron;
        if (disclosure is { } d)
        {
            var toggle = new Button
            {
                Content = MapIcons.Stroked(d.Collapsed ? MapIcons.ChevronRight : MapIcons.ChevronDown, 11),
                Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0),
            }.Named("CollectionToggle_" + name["Collection_".Length..]).Classed("icon");
            AutomationProperties.SetName(toggle, d.Collapsed ? "Развернуть " + title : "Свернуть " + title);
            toggle.Click += (_, e) => { e.Handled = true; d.Toggle(); };
            chevron = toggle;
        }
        else
        {
            chevron = new Border { Width = 20, Height = 20 };
        }
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(depth * 14, 0, 4, 0);
        content.Children.Add(chevron.Docked(Dock.Left));
        content.Children.Add(Ui.Trimmed(title));
        var row = new ToggleButton
        {
            Content = content, MinHeight = 28, Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        }.Named(name).Classed("list-row");
        AutomationProperties.SetName(row, $"{title}, карточек: {count}");
        ToolTip.SetTip(row, $"{title} · {count}");
        row.Click += (_, _) => { activate(); row.IsChecked = true; };
        Ui.SetSelected(row, selected);
        return row;
    }

    // ------------------------------------------------------------------ toolbar sync

    /// <summary>Sort and grouping captions, the names toggle and the chips of every active filter.</summary>
    private void SyncLibraryToolbar()
    {
        SetToolbarCaption(_librarySortMenu, "LibrarySortMenuText", _editor.LibrarySort switch
        {
            AssetSortOrder.NameAscending => "Название (А–Я)",
            AssetSortOrder.NameDescending => "Название (Я–А)",
            AssetSortOrder.ImportedOldest => "Дата (старые)",
            _ => "Дата (новые)",
        });
        SetToolbarCaption(_libraryGroupMenu, "LibraryGroupMenuText", _editor.LibraryGrouping switch
        {
            AssetGrouping.Collection => "Коллекция",
            AssetGrouping.Tag => "Тег",
            _ => "Без групп",
        });
        _libraryShowNamesItem.IsChecked = _galleryShowNames;

        _libraryFilterChips.Children.Clear();
        void Chip(string name, string text, Action clear)
        {
            var chip = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4,
                    Children = { new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, MapIcons.Stroked(MapIcons.Close, 10) },
                },
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            }.Named("LibraryChip" + name).Classed("compact", "filter-chip-active");
            AutomationProperties.SetName(chip, "Снять фильтр: " + text);
            ToolTip.SetTip(chip, "Снять фильтр: " + text);
            chip.Click += (_, _) => { clear(); SyncAssetList(); };
            _libraryFilterChips.Children.Add(chip);
        }
        if (_editor.LibraryTag is { } tag) Chip("Tag", "Тег: " + tag, () => _editor.LibraryTag = null);
        if (_editor.LibraryUntaggedOnly) Chip("Untagged", "Без тегов", () => _editor.LibraryUntaggedOnly = false);
        if (_editor.LibraryFavoritesOnly) Chip("Favorites", "Избранное", () => _editor.LibraryFavoritesOnly = false);
        if (_editor.LibraryRecentOnly) Chip("Recent", "Недавние", () => _editor.LibraryRecentOnly = false);
        if (_editor.LibraryUsedInMapOnly) Chip("UsedInMap", "На карте", () => _editor.LibraryUsedInMapOnly = false);
        if (_editor.LibraryMediaType is { } media) Chip("Media", "Формат: " + media, () => _editor.LibraryMediaType = null);
        if (_editor.LibraryImportedFromText.Length > 0)
            Chip("From", "С " + _editor.LibraryImportedFromText, () => { _editor.LibraryImportedFromText = ""; _libraryImportedFrom.Text = ""; });
        if (_editor.LibraryImportedToText.Length > 0)
            Chip("To", "По " + _editor.LibraryImportedToText, () => { _editor.LibraryImportedToText = ""; _libraryImportedTo.Text = ""; });
        _libraryFilterSummary.IsVisible = false;
        _clearFiltersButton.IsVisible = _libraryFilterChips.Children.Count > 0 || _editor.HasActiveLibraryFilters;
    }

    private static void SetToolbarCaption(Button button, string textName, string value)
    {
        if (button.Content is StackPanel panel)
            foreach (var child in panel.Children)
                if (child is TextBlock text && text.Name == textName) text.Text = value;
    }

    private void SyncTagSuggestions()
    {
        var text = (_assetSearch.Text ?? "").Trim();
        _libraryTagSuggestionItems.Children.Clear();
        if (text.Length == 0 || !_assetSearch.IsFocused) { _libraryTagSuggestions.IsOpen = false; return; }
        var tags = _editor.LibraryTagChoices
            .Where(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();
        if (tags.Length == 0) { _libraryTagSuggestions.IsOpen = false; return; }
        foreach (var tag in tags)
        {
            var captured = tag;
            var item = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        new Border { Child = new TextBlock { Text = "Тег", FontSize = 11 }, Padding = new Thickness(5, 0) }.Classed("badge"),
                        new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            }.Named("LibraryTagSuggestion_" + Sanitize(tag)).Classed("compact");
            AutomationProperties.SetName(item, "Фильтр по тегу " + tag);
            item.Click += (_, _) =>
            {
                _libraryTagSuggestions.IsOpen = false;
                _assetSearch.Text = "";
                _editor.LibraryTag = captured;
                SyncAssetList();
            };
            _libraryTagSuggestionItems.Children.Add(item);
        }
        _libraryTagSuggestions.IsOpen = true;
    }

    // ------------------------------------------------------------------ grid

    /// <summary>
    /// One real virtualized list over a flat sequence of group headers and tile rows. Avalonia
    /// realises only the containers it can show, so grouping ten thousand cards by tag still
    /// costs the handful of rows on screen rather than a second control tree.
    /// </summary>
    private void BuildLibraryGrid()
    {
        AutomationProperties.SetName(_libraryGrid, "Галерея ассетов");
        _libraryGrid.ItemTemplate = new FuncDataTemplate<GalleryRow>(
            (row, _) => BuildGalleryRow(row), supportsRecycling: false);
        _libraryGrid.SizeChanged += (_, _) => QueueGalleryRelayout();
        _libraryGrid.TemplateApplied += (_, _) =>
        {
            if (_libraryGrid.Scroll is ScrollViewer scroll)
                scroll.ScrollChanged += (_, _) => QueueGalleryRange();
        };
    }

    private int ColumnsFor(double width) =>
        Math.Clamp((int)((width - 12) / (_galleryTileSize + GalleryTileGap)), 1, 24);

    private void QueueGalleryRelayout()
    {
        if (_galleryResizeQueued) return;
        _galleryResizeQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _galleryResizeQueued = false;
            var columns = ColumnsFor(_libraryGrid.Bounds.Width);
            if (columns == _galleryColumns && Math.Abs(_lastBuiltTile - _galleryTileSize) < 0.5) return;
            _galleryColumns = columns;
            RebuildGalleryRows();
        }, DispatcherPriority.Loaded);
    }

    private void RebuildGalleryRows()
    {
        var result = new List<GalleryRow>();
        var cells = new List<GalleryCell>(_galleryColumns);
        string? group = null;
        void Flush()
        {
            if (cells.Count == 0) return;
            result.Add(new(group, null, 0, cells.ToArray()));
            cells.Clear();
        }
        var rows = _editor.LibraryRows;
        var skipping = false;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.IsHeader)
            {
                Flush();
                group = row.Key;
                skipping = _collapsedGalleryGroups.Contains(row.Key);
                result.Add(new(row.Key, row.Title, row.Count, []));
                continue;
            }
            if (skipping || row.Card is not { } card) continue;
            cells.Add(new(card, null, index));
            if (cells.Count == _galleryColumns) Flush();
            if (!_expandedGallerySets.Contains(card.SetId)) continue;
            // The variants follow their cover in the same flow: no heading, no new line.
            foreach (var variant in _editor.VariantsForSet(card.SetId))
            {
                cells.Add(new(card, variant, index));
                if (cells.Count == _galleryColumns) Flush();
            }
        }
        Flush();
        var scroll = _libraryGrid.Scroll as ScrollViewer;
        var offset = scroll?.Offset ?? default;
        _galleryRows = result;
        _lastBuiltTile = _galleryTileSize;
        _libraryGrid.ItemsSource = result;
        _reportedLibraryRange = (-1, -1);
        Dispatcher.UIThread.Post(() =>
        {
            if (scroll is not null) scroll.Offset = offset;
            RefreshGallerySelection();
            QueueGalleryRange();
        }, DispatcherPriority.Loaded);
    }

    private Control BuildGalleryRow(GalleryRow? row)
    {
        // Virtualized presenters clear their content with null when a row is recycled.
        if (row is null) return new Border();
        if (row.Heading is { } heading) return BuildGalleryGroupHeader(row.GroupKey!, heading, row.HeadingCount);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = GalleryTileGap, Margin = new Thickness(6, 0, 6, GalleryTileGap) };
        foreach (var cell in row.Cells) strip.Children.Add(BuildGalleryCard(cell));
        return strip;
    }

    private Control BuildGalleryGroupHeader(string key, string title, int count)
    {
        var collapsed = _collapsedGalleryGroups.Contains(key);
        var chevron = new Button
        {
            Content = MapIcons.Stroked(collapsed ? MapIcons.ChevronRight : MapIcons.ChevronDown, 12),
            Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        }.Named("GalleryGroup_" + Sanitize(key)).Classed("icon");
        AutomationProperties.SetName(chevron, (collapsed ? "Развернуть группу " : "Свернуть группу ") + title);
        ToolTip.SetTip(chevron, collapsed ? "Развернуть" : "Свернуть");
        var header = new Border
        {
            Margin = new Thickness(0, collapsed ? 2 : 8, 0, collapsed ? 2 : 8), Padding = new Thickness(6, 3),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    chevron,
                    new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"({EditorLabels.AssetCount(count)})", VerticalAlignment = VerticalAlignment.Center }.Classed("faint", "role-caption"),
                },
            },
        }.Classed("gallery-group-header").Named("LibraryGroupHeader_" + Sanitize(key));
        void Toggle()
        {
            if (!_collapsedGalleryGroups.Add(key)) _collapsedGalleryGroups.Remove(key);
            RebuildGalleryRows();
        }
        chevron.Click += (_, e) => { e.Handled = true; Toggle(); };
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) { e.Handled = true; Toggle(); } };
        return header;
    }

    private Control BuildGalleryCard(GalleryCell cell)
    {
        var path = cell.Variant is { } variant ? _editor.LibraryThumbnailPathForVariant(variant.Id)
            : _editor.LibraryThumbnailPath(cell.Set);
        var title = cell.Variant?.Label ?? cell.Set.Name;
        var picture = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(5) };
        _thumbnails.Assign(picture, path);
        var face = new Grid { Children = { picture } };
        TextBlock? badgeText = null;
        if (cell.Variant is null && cell.Set.IsSet)
        {
            var expanded = _expandedGallerySets.Contains(cell.Set.SetId);
            badgeText = new TextBlock { Text = cell.Set.VariantCount.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Button
            {
                Content = badgeText, Padding = new Thickness(5, 1), MinHeight = 0, Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 3),
            }.Named("GalleryExpand_" + Sanitize(cell.Set.SetId)).Classed("gallery-badge");
            var badgeTitle = (expanded ? "Скрыть " : "Показать ") + EditorLabels.VariantCount(cell.Set.VariantCount);
            AutomationProperties.SetName(badge, badgeTitle);
            ToolTip.SetTip(badge, badgeTitle);
            badge.Click += (_, e) =>
            {
                e.Handled = true;
                if (!_expandedGallerySets.Add(cell.Set.SetId)) _expandedGallerySets.Remove(cell.Set.SetId);
                RebuildGalleryRows();
            };
            face.Children.Add(badge);
        }
        var caption = _galleryShowNames
            ? new TextBlock { Text = title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center, Height = GalleryCaptionHeight, Margin = new Thickness(2, 0) }
            : null;
        var tile = new Button
        {
            Name = cell.Variant is { } chosen ? "GalleryVariant_" + Sanitize(chosen.Id) : "GalleryCover_" + Sanitize(cell.Set.SetId),
            Tag = cell, Padding = new Thickness(0), Width = _galleryTileSize,
            Height = _galleryTileSize + (caption is null ? 0 : GalleryCaptionHeight),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = caption is null ? face : new DockPanel { Children = { caption.Docked(Dock.Bottom), face } },
        }.Classed("asset-tile");
        if (cell.Variant is not null) tile.Classes.Add("variant");
        if (cell.Set.Favorite && cell.Variant is null)
        {
            face.Children.Add(new TextBlock
            {
                Text = "★", FontSize = 12, Margin = new Thickness(4, 2, 0, 0), IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            }.Classed("accent"));
        }
        AutomationProperties.SetName(tile, "Выбрать " + title);
        ToolTip.SetTip(tile, BuildGalleryTooltip(cell));
        ToolTip.SetShowDelay(tile, 500);
        Ui.SetSelected(tile, GalleryCellSelected(cell));
        if (badgeText is not null)
        {
            var count = cell.Set.VariantCount;
            tile.PointerEntered += (_, _) => badgeText.Text = (_expandedGallerySets.Contains(cell.Set.SetId) ? "Скрыть " : "Показать ") + count;
            tile.PointerExited += (_, _) => badgeText.Text = count.ToString();
        }
        tile.Click += (_, e) =>
        {
            e.Handled = true;
            // One click chooses and keeps the window: the side panel already shows the choice.
            if (!_editor.ChooseGalleryVariant(cell.Set.SetId, cell.Variant?.Id)) return;
            RefreshGallerySelection();
            RefreshLibraryPanel();
        };
        tile.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            // The two clicks before this already chose the tile; choosing again is harmless and
            // keeps a synthetic double tap honest.
            if (!_editor.ChooseGalleryVariant(cell.Set.SetId, cell.Variant?.Id)) return;
            CloseAssetCatalogAndArmPlacement();
        };
        tile.AttachedToVisualTree += (_, _) => _realizedGalleryTiles.Add(tile);
        tile.DetachedFromVisualTree += (_, _) => _realizedGalleryTiles.Remove(tile);
        return tile;
    }

    private Control BuildGalleryTooltip(GalleryCell cell)
    {
        var panel = new StackPanel { Spacing = 4, MaxWidth = 280 };
        panel.Children.Add(new TextBlock { Text = cell.Variant?.Label ?? cell.Set.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var trail = "Каталог";
        if (cell.Set.CollectionId is { } collectionId) trail += " / " + _editor.LibraryCollectionTitle(collectionId);
        if (cell.Variant is not null) trail += " / " + cell.Set.Name;
        panel.Children.Add(new TextBlock { Text = trail, TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"));
        if (cell.Set.Tags.Count > 0)
        {
            var tags = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
            foreach (var tag in cell.Set.Tags)
                tags.Children.Add(new Border { Child = new TextBlock { Text = tag, FontSize = 11 } }.Classed("badge"));
            panel.Children.Add(tags);
        }
        if (cell.Set.CoverWidth > 0)
            panel.Children.Add(new TextBlock { Text = $"{cell.Set.CoverWidth}×{cell.Set.CoverHeight} · двойной щелчок — на холст" }.Classed("faint", "role-caption"));
        return panel;
    }

    private bool GalleryCellSelected(GalleryCell cell) => _editor.SelectedSetId == cell.Set.SetId
        && (cell.Variant is null || _editor.CurrentVariant?.Id == cell.Variant.Id);

    private void RefreshGallerySelection()
    {
        foreach (var button in _realizedGalleryTiles)
            if (button.Tag is GalleryCell cell) Ui.SetSelected(button, GalleryCellSelected(cell));
    }

    private void QueueGalleryRange()
    {
        if (_galleryRangeQueued) return;
        _galleryRangeQueued = true;
        Dispatcher.UIThread.Post(() => { _galleryRangeQueued = false; ReportGalleryRange(); }, DispatcherPriority.Background);
    }

    private void ReportGalleryRange()
    {
        var sourceRows = new HashSet<int>();
        foreach (var button in _realizedGalleryTiles)
            if (button.Tag is GalleryCell cell) sourceRows.Add(cell.SourceRow);
        _editor.ReportRealizedLibraryRows(sourceRows);
        var range = (_editor.VisibleLibraryTileWindow.FirstIndex, _editor.VisibleLibraryTileWindow.Count);
        if (_reportedLibraryRange == range) return;
        _reportedLibraryRange = range;
        _ = CheckVisibleAvailabilityAsync();
    }

    private async void QueueLibrarySearch()
    {
        _librarySearchCancellation?.Cancel();
        _librarySearchCancellation?.Dispose();
        var cancellation = _librarySearchCancellation = new CancellationTokenSource();
        try
        {
            SyncTagSuggestions();
            await Task.Delay(140, cancellation.Token);
            await _editor.SearchLibraryAsync((_assetSearch.Text ?? "").Trim(), cancellation.Token);
            if (!cancellation.IsCancellationRequested) SyncAssetList();
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ side panel strips

    private ListBox BuildCompactVariantList() => new()
    {
        Name = "StampVariantsList", MaxHeight = 150,
        ItemTemplate = new FuncDataTemplate<CompactVariant[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var cell in row ?? []) panel.Children.Add(BuildVariantChoice(cell.Variant, cell.Index, "StampVariant_", 44));
            return panel;
        }, supportsRecycling: false),
    };

    private ListBox BuildCompactSetList(string prefix) => new()
    {
        Name = prefix + "List", MaxHeight = 150,
        ItemTemplate = new FuncDataTemplate<AssetSetSummary[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var item in row ?? []) panel.Children.Add(CompactStampSetButton(item, prefix));
            return panel;
        }, supportsRecycling: false),
    };

    private void SyncCompactSetList(ListBox list, IReadOnlyList<AssetSetSummary> sets)
    {
        var signature = string.Join('|', sets.Select(s => s.SetId + s.Name + s.CoverSha256 + s.Favorite));
        if (!Equals(list.Tag, signature))
        {
            list.Tag = signature;
            list.ItemsSource = sets.Chunk(4).ToArray();
        }
        foreach (var button in list.GetVisualDescendants().OfType<Button>())
            Ui.SetSelected(button, button.Name?.EndsWith("_" + Sanitize(_editor.SelectedSetId ?? ""), StringComparison.Ordinal) == true);
    }
}
