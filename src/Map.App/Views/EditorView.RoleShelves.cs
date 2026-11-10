using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Assets;

namespace Map.App.Views;

public sealed partial class EditorView
{
    /// <summary>Width of the panel: the same as the right column of the editor, as the owner saw it in the prototype.</summary>
    private const double RoleShelvesWidth = 340;
    private const int RoleGroupsPerPage = 3;

    private readonly StackPanel _roleShelvesBody = new() { Spacing = 8 };
    private Border? _roleShelvesPanel;
    private ToggleButton? _roleShelvesToggle;
    private int _roleGroupPage;
    private string _roleGroupId = AssetRoles.Groups[0].Id;
    private string? _roleHeldSetId;

    private static readonly IReadOnlyDictionary<AssetRole, string> RoleIcons = new Dictionary<AssetRole, string>
    {
        [AssetRole.Peak] = "M3 20 L10 7 L14 14 L16 11 L21 20 Z M8 11 L10 13 L12 11",
        [AssetRole.Slope] = "M3 19 C8 18 12 14 15 8 L21 19 Z",
        [AssetRole.Tree] = "M6 9 A6 6 0 1 0 18 9 A6 6 0 1 0 6 9 M12 15 V21",
        [AssetRole.Spruce] = "M12 3 L17 10 H14 L18 16 H6 L10 10 H7 Z M12 16 V21",
        [AssetRole.Bush] = "M4 18 A4 4 0 0 1 7 11 A5 5 0 0 1 17 11 A4 4 0 0 1 20 18 Z",
        [AssetRole.House] = "M4 11 L12 4 L20 11 M6 10 V20 H18 V10 M10 20 V15 H14 V20",
        [AssetRole.Tower] = "M8 21 V8 L7 7 V3 H9 V5 H11 V3 H13 V5 H15 V3 H17 V7 L16 8 V21 Z M11 21 V17 H13 V21",
        [AssetRole.Sign] = "M12 21 V4 M12 5 H19 L21 7.5 L19 10 H12 Z M12 12 H6 L4 14 L6 16 H12",
        [AssetRole.Wall] = "M3 7 H21 V19 H3 Z M3 13 H21 M9 7 V13 M15 7 V13 M6 13 V19 M12 13 V19 M18 13 V19",
        [AssetRole.Door] = "M6 21 V7 A6 6 0 0 1 18 7 V21 Z M12 3 V21",
    };

    /// <summary>The picture of a group: its first role, and for the forest a tree beside a spruce.</summary>
    private static string RoleGroupIcon(AssetRoleGroup group) => group.Id == "forest"
        ? "M4 10 A4 4 0 1 0 12 10 A4 4 0 1 0 4 10 M8 14 V20 M17 5 L21 11 H19 L22 16 H13 L16 11 H13 Z M17 16 V20"
        : RoleIcons[group.Roles[0]];

    /// <summary>
    /// «Роли наборов» of the catalog (R11b, the owner's «Полки по ролям»): three large group tiles at a time with
    /// arrows to the next three; under the chosen group one shelf per role. A set is taken with a click from
    /// «Остальные наборы» and put on a shelf with another; a click on a set on a shelf takes it off. Catalog data
    /// only - the map and its history do not change.
    /// </summary>
    private Control BuildRoleShelvesPanel()
    {
        var title = new TextBlock { Text = "Роли наборов", VerticalAlignment = VerticalAlignment.Center }.Classed("role-panel");
        _roleShelvesPanel = new Border
        {
            Width = RoleShelvesWidth, IsVisible = false, Padding = new Thickness(10, 0, 0, 0),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new Border { Child = title, Margin = new Thickness(0, 0, 0, 6) }.Docked(Dock.Top),
                    new ScrollViewer { Content = _roleShelvesBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                },
            },
        }.Named("RoleShelvesPanel");
        return _roleShelvesPanel;
    }

    private ToggleButton BuildRoleShelvesToggle()
    {
        _roleShelvesToggle = new ToggleButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { MapIcons.Stroked(RoleIcons[AssetRole.Peak], 14), new TextBlock { Text = "Роли" } },
            },
            VerticalAlignment = VerticalAlignment.Center,
        }.Named("RoleShelvesToggle");
        AutomationProperties.SetName(_roleShelvesToggle, "Роли наборов");
        ToolTip.SetTip(_roleShelvesToggle, "Какими наборами ставят «Лес», «Хребет» и «Поселение»: полки по ролям");
        _roleShelvesToggle.Click += (_, _) => ShowRoleShelves(_roleShelvesToggle.IsChecked == true);
        return _roleShelvesToggle;
    }

    /// <summary>Opens or closes the panel; with a set, opens it on that set's group (or keeps the group) holding the set.</summary>
    private void ShowRoleShelves(bool show, string? holdSetId = null)
    {
        if (_roleShelvesPanel is null) return;
        _roleShelvesPanel.IsVisible = show;
        if (_roleShelvesToggle is not null) _roleShelvesToggle.IsChecked = show;
        _roleHeldSetId = null;
        if (show && holdSetId is not null)
        {
            if (AssetRoles.GroupOf(_editor.RoleOfSet(holdSetId)) is { } group) ChooseRoleGroup(group.Id);
            else _roleHeldSetId = holdSetId;
        }
        SyncRoleShelves();
        QueueGalleryRelayout();
    }

    private void ChooseRoleGroup(string groupId)
    {
        var index = Math.Max(0, AssetRoles.Groups.ToList().FindIndex(group => group.Id == groupId));
        _roleGroupId = AssetRoles.Groups[index].Id;
        _roleGroupPage = index / RoleGroupsPerPage;
    }

    private void SetRoleFromShelves(string setId, AssetRole role)
    {
        _roleHeldSetId = null;
        _editor.SetSetRole(setId, role);
        SyncAssetList(); SyncAppearanceSections(); SyncStatus();
    }

    private void SyncRoleShelves()
    {
        if (_roleShelvesPanel is not { IsVisible: true }) return;
        var sets = _editor.LibrarySets.Where(set => !set.Archived).ToArray();
        var roles = sets.ToDictionary(set => set.SetId, set => _editor.RoleOfSet(set.SetId));
        if (_roleHeldSetId is not null && !roles.ContainsKey(_roleHeldSetId)) _roleHeldSetId = null;
        var group = AssetRoles.Groups.First(candidate => candidate.Id == _roleGroupId);
        var pages = (AssetRoles.Groups.Count + RoleGroupsPerPage - 1) / RoleGroupsPerPage;
        _roleShelvesBody.Children.Clear();

        // The carousel: three groups, arrows to the others, and how many sets each holds.
        Button Arrow(string name, string glyph, string label, int step)
        {
            var arrow = new Button { Content = new TextBlock { Text = glyph, FontSize = 18 }, Padding = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Stretch, IsEnabled = _roleGroupPage + step >= 0 && _roleGroupPage + step < pages }.Named(name);
            AutomationProperties.SetName(arrow, label); ToolTip.SetTip(arrow, label);
            arrow.Click += (_, _) =>
            {
                _roleGroupPage += step; _roleGroupId = AssetRoles.Groups[_roleGroupPage * RoleGroupsPerPage].Id;
                SyncRoleShelves();
            };
            return arrow;
        }
        var tiles = new UniformGrid { Columns = RoleGroupsPerPage, Margin = new Thickness(4, 0) };
        foreach (var candidate in AssetRoles.Groups.Skip(_roleGroupPage * RoleGroupsPerPage).Take(RoleGroupsPerPage))
        {
            var count = roles.Values.Count(candidate.Roles.Contains);
            var tile = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0), Padding = new Thickness(2, 8, 2, 6),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Border { Child = MapIcons.Stroked(RoleGroupIcon(candidate), 28), HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = candidate.Title, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = count == 0 ? "пусто" : "наборов: " + count, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center }.Classed("faint"),
                    },
                },
            }.Named("RoleGroup_" + candidate.Id).Classed("asset-tile");
            var tip = candidate.Title + ": " + string.Join(", ", candidate.Roles.Select(AssetRoles.Title)).ToLowerInvariant();
            AutomationProperties.SetName(tile, candidate.Title); ToolTip.SetTip(tile, tip);
            Ui.SetSelected(tile, candidate.Id == group.Id);
            var chosen = candidate.Id;
            tile.Click += (_, _) => { _roleGroupId = chosen; SyncRoleShelves(); };
            tiles.Children.Add(tile);
        }
        _roleShelvesBody.Children.Add(new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Arrow("RoleGroupsPrevious", "‹", "Предыдущие группы", -1).Docked(Dock.Left),
                Arrow("RoleGroupsNext", "›", "Следующие группы", 1).Docked(Dock.Right),
                tiles,
            },
        });
        _roleShelvesBody.Children.Add(new TextBlock { Text = $"{_roleGroupPage + 1} из {pages}", FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center }.Classed("faint"));
        if (!group.HasTool)
            _roleShelvesBody.Children.Add(Ui.Hint($"У группы «{group.Title}» пока нет своего инструмента: роль запомнится, но ставить ею нечем."));

        var held = _roleHeldSetId is not null ? sets.First(set => set.SetId == _roleHeldSetId) : null;
        // One shelf per role of the group.
        foreach (var role in group.Roles)
        {
            var onShelf = sets.Where(set => roles[set.SetId] == role).ToArray();
            var heading = new DockPanel { LastChildFill = true };
            if (held is not null)
            {
                var put = Ui.TextButton("RoleShelf_" + role, "Положить сюда", () => SetRoleFromShelves(held.SetId, role), "compact");
                AutomationProperties.SetName(put, $"Положить «{held.Name}» на полку «{AssetRoles.Title(role)}»");
                ToolTip.SetTip(put, $"Набор «{held.Name}» получит роль «{AssetRoles.Title(role)}»");
                heading.Children.Add(put.Docked(Dock.Right));
            }
            else
            {
                heading.Children.Add(new TextBlock { Text = onShelf.Length == 0 ? "пусто" : onShelf.Length.ToString(),
                    VerticalAlignment = VerticalAlignment.Center }.Classed("faint").Docked(Dock.Right));
            }
            heading.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { MapIcons.Stroked(RoleIcons[role], 16), new TextBlock { Text = AssetRoles.Title(role), FontWeight = FontWeight.SemiBold } },
            });
            var shelf = new StackPanel { Spacing = 6, Children = { heading } };
            if (onShelf.Length > 0)
            {
                var row = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var set in onShelf)
                    row.Children.Add(RoleSetTile("RoleOnShelf_", set, false, $"Снять «{set.Name}» с полки «{AssetRoles.Title(role)}»",
                        () => SetRoleFromShelves(set.SetId, AssetRole.None)));
                shelf.Children.Add(row);
            }
            _roleShelvesBody.Children.Add(new Border
            {
                Child = shelf, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource(held is null ? "MapBorderBrush" : "MapAccentBorderBrush") as IBrush,
            }.Named("RoleShelfBox_" + role));
        }

        // Everything not on the shelves of this group: a click takes a set, a second click lets it go.
        var free = sets.Where(set => !group.Roles.Contains(roles[set.SetId])).ToArray();
        var rest = new StackPanel { Spacing = 6 };
        rest.Children.Add(Ui.Hint(held is not null ? $"Взят «{held.Name}» — выберите полку выше."
            : free.Length == 0 ? "Все наборы уже на полках этой группы." : "Щёлкните набор, затем полку."));
        if (free.Length > 0)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var set in free)
            {
                var elsewhere = roles[set.SetId] is var other && other != AssetRole.None
                    ? $" (сейчас: {AssetRoles.GroupOf(other)?.Title} · {AssetRoles.Title(other)})" : "";
                row.Children.Add(RoleSetTile("RoleFree_", set, set.SetId == _roleHeldSetId, $"Взять «{set.Name}»{elsewhere}", () =>
                {
                    _roleHeldSetId = _roleHeldSetId == set.SetId ? null : set.SetId;
                    SyncRoleShelves();
                }));
            }
            rest.Children.Add(row);
        }
        _roleShelvesBody.Children.Add(Ui.Subgroup("Остальные наборы", rest));
    }

    /// <summary>A set on a shelf or below them: its cover, its name, and one click.</summary>
    private Button RoleSetTile(string prefix, AssetSetSummary set, bool selected, string tip, Action click)
    {
        var picture = new Image { Stretch = Stretch.Uniform, Height = 52 };
        _thumbnails.Assign(picture, _editor.LibraryThumbnailPath(set));
        var tile = new Button
        {
            Width = 98, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(4),
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    picture,
                    new TextBlock { Text = set.Name, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = Avalonia.Media.TextAlignment.Center },
                },
            },
        }.Named(prefix + Sanitize(set.SetId)).Classed("asset-tile");
        AutomationProperties.SetName(tile, tip); ToolTip.SetTip(tile, tip);
        Ui.SetSelected(tile, selected);
        tile.Click += (_, _) => click();
        return tile;
    }
}
