using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// R26: «Сущности» in the Notes tool - the cards of the world. A list with search (this map, and
/// the atlas on request), a new card of a kind bound to the selection, and the card itself: name,
/// kind, text with [[links]], places on this and other maps, backlinks, and whether players see it.
/// Also the R26 settings of a note: category, «Видно игрокам», «Ведёт на карту».
/// </summary>
public sealed partial class EditorView
{
    private Guid? _selectedEntityId;
    private readonly List<Action> _entitySyncs = [];
    private ComboBox? _noteCategory, _noteLinkedMap;
    private ToggleButton? _notePlayerVisible;
    private bool _syncingNoteWiki;

    private static readonly (EntityKind Kind, string Icon)[] EntityKinds =
    [
        (EntityKind.Place, MapIcons.LineTarget), (EntityKind.River, MapIcons.LineRiver), (EntityKind.Region, MapIcons.LineLand),
        (EntityKind.Character, MapIcons.LineText), (EntityKind.Faction, MapIcons.LineLayers), (EntityKind.Item, MapIcons.LineStamp),
        (EntityKind.Other, MapIcons.LineOther),
    ];

    private static string KindIcon(EntityKind kind) => EntityKinds.First(k => k.Kind == kind).Icon;

    private Control BuildEntitiesPage()
    {
        var search = new TextBox { Name = "EntitySearchBox", PlaceholderText = "Найти карточку" };
        var list = new StackPanel { Name = "EntityList", Spacing = 2 };
        var atlasHits = new StackPanel { Name = "EntityAtlasHits", Spacing = 2 };
        var newKind = new ComboBox { Name = "NewEntityKind", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = EntityKinds.Select(k => MapEntities.KindTitle(k.Kind)).ToArray(), SelectedIndex = 0 };
        var create = Ui.CommandButton("NewEntityButton", "Новая карточка", MapIcons.Plus, () =>
        {
            _selectedEntityId = _editor.CreateEntity(EntityKinds[Math.Max(0, newKind.SelectedIndex)].Kind);
            SyncEntities();
        }, "compact");
        ToolTip.SetTip(create, "Карточка получает имя и привязку выделенного объекта, реки или дороги; без выделения — точку в центре вида.");
        var atlasSearch = Ui.AsyncButton("EntityAtlasSearchButton", "Искать по атласу", async () =>
        {
            atlasHits.Children.Clear();
            var hits = await _editor.SearchAtlasEntitiesAsync(search.Text);
            if (hits.Count == 0) atlasHits.Children.Add(new TextBlock { Text = _editor.Atlas is null ? "Карта не в атласе." : "На других картах атласа ничего не найдено.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            foreach (var hit in hits.Take(40))
            {
                var captured = hit;
                atlasHits.Children.Add(Ui.ListRow("AtlasEntity_" + hit.Entity.Id.ToString("N"), KindIcon(hit.Entity.Kind), hit.Entity.Name,
                    "на карте «" + hit.MapTitle + "»", [], () => _editor.OpenAtlasMap(captured.MapId), hit.Entity.Name));
            }
        }, "compact");
        search.TextChanged += (_, _) => SyncEntities();

        // The card.
        var name = new TextBox { Name = "EntityNameBox", PlaceholderText = "Имя" };
        var kind = new ComboBox { Name = "EntityKindBox", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = EntityKinds.Select(k => MapEntities.KindTitle(k.Kind)).ToArray() };
        var text = new TextBox { Name = "EntityMarkdownBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
            PlaceholderText = "Текст карточки. [[Имя]] — ссылка на другую карточку." };
        var players = Ui.PropertyToggle("EntityPlayerVisibleToggle", "Видно игрокам");
        ToolTip.SetTip(players, "Без галочки карточка — только для ведущего.");
        var links = new StackPanel { Name = "EntityLinks", Spacing = 2 };
        var places = new StackPanel { Name = "EntityBindings", Spacing = 2 };
        var backlinks = new StackPanel { Name = "EntityBacklinks", Spacing = 2 };
        var save = Ui.CommandButton("SaveEntityButton", "Сохранить", MapIcons.Check, () =>
        {
            if (_selectedEntityId is { } id)
                _editor.EditEntity(id, name.Text ?? "", EntityKinds[Math.Max(0, kind.SelectedIndex)].Kind, text.Text ?? "", players.IsChecked == true);
            SyncEntities();
        }, "compact");
        var bind = Ui.CommandButton("BindEntityButton", "Привязать выделенное", MapIcons.LineTarget, () =>
        {
            if (_selectedEntityId is { } id) _editor.BindSelectionToEntity(id);
            SyncEntities();
        }, "compact");
        var delete = Ui.CommandButton("DeleteEntityButton", "Удалить", MapIcons.Trash, () =>
        {
            if (_selectedEntityId is { } id) { _editor.DeleteEntity(id); _selectedEntityId = null; }
            SyncEntities();
        }, "destructive", "compact");
        var card = new Border
        {
            Name = "EntityCard", Padding = new Thickness(10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new DockPanel { Children = { kind.Docked(Dock.Right), name } },
                    Ui.Subgroup("Текст", text),
                    Ui.Subgroup("Ссылки", links),
                    Ui.Subgroup("Где на картах", places),
                    Ui.Subgroup("Упоминают", backlinks),
                    players,
                    new UniformGrid { Rows = 1, Columns = 3, Children = { save, bind, delete } },
                },
            },
        };
        kind.Width = 130;

        void Sync()
        {
            list.Children.Clear();
            var found = _editor.SearchEntities(search.Text);
            if (found.Count == 0)
                list.Children.Add(Ui.EmptyState("NoEntitiesText", MapIcons.Notes, _editor.Entities.Length == 0 ? "Карточек пока нет" : "Ничего не найдено",
                    _editor.Entities.Length == 0 ? "Выделите город, реку или подпись и нажмите «Новая карточка»." : "Измените запрос или поищите по атласу."));
            foreach (var entity in found)
            {
                var captured = entity;
                var row = Ui.ListRow("Entity_" + entity.Id.ToString("N"), KindIcon(entity.Kind), entity.Name,
                    MapEntities.KindTitle(entity.Kind) + (entity.PlayerVisible ? " · игрокам" : ""), [],
                    () => { _selectedEntityId = captured.Id; SyncEntities(); }, entity.Name);
                Ui.SetSelected(row, entity.Id == _selectedEntityId);
                list.Children.Add(row);
            }
            var selected = _editor.Entities.FirstOrDefault(e => e.Id == _selectedEntityId);
            card.IsVisible = selected is not null;
            if (selected is null) return;
            if (!name.IsFocused) name.Text = selected.Name;
            kind.SelectedIndex = Array.FindIndex(EntityKinds, k => k.Kind == selected.Kind);
            if (!text.IsFocused) text.Text = selected.Markdown;
            players.IsChecked = selected.PlayerVisible;
            links.Children.Clear();
            foreach (var linked in selected.LinkedNames())
            {
                var target = MapEntities.Resolve(_editor.Entities, linked);
                var label = target is null ? linked + " — карточки нет" : linked;
                var button = Ui.TextButton("EntityLink_" + linked, label, () =>
                {
                    if (target is not null) { _selectedEntityId = target.Id; SyncEntities(); }
                    else { _selectedEntityId = _editor.CreateEntity(EntityKind.Other); _editor.EditEntity(_selectedEntityId.Value, linked, EntityKind.Other, "", false); SyncEntities(); }
                }, "compact");
                ToolTip.SetTip(button, target is null ? "Создать карточку «" + linked + "»" : "Открыть карточку");
                links.Children.Add(button);
            }
            if (links.Children.Count == 0) links.Children.Add(new TextBlock { Text = "Нет ссылок. Напишите [[Имя]] в тексте.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            places.Children.Clear();
            for (var i = 0; i < selected.Bindings.Length; i++)
            {
                var binding = selected.Bindings[i];
                var index = i;
                var show = Ui.IconButton("EntityShow" + i, "Показать", MapIcons.LineTarget, () => _editor.GoToBinding(binding));
                var unbind = Ui.IconButton("EntityUnbind" + i, "Отвязать", MapIcons.LineReset, () => { _editor.UnbindEntity(selected.Id, index); SyncEntities(); });
                places.Children.Add(new DockPanel
                {
                    Children =
                    {
                        unbind.Docked(Dock.Right), show.Docked(Dock.Right),
                        new TextBlock { Text = _editor.BindingTitle(binding), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
            backlinks.Children.Clear();
            foreach (var from in _editor.EntityBacklinks(selected))
            {
                var captured = from;
                backlinks.Children.Add(Ui.TextButton("EntityBacklink_" + from.Id.ToString("N"), from.Name, () => { _selectedEntityId = captured.Id; SyncEntities(); }, "compact"));
            }
            if (backlinks.Children.Count == 0) backlinks.Children.Add(new TextBlock { Text = "Пока никто не ссылается на эту карточку.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
        }
        _entitySyncs.Add(Sync);
        Sync();
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Hint("Карточки мира: город, река, персонаж, фракция. Одна карточка может стоять на нескольких картах атласа."),
                search, list,
                Ui.Subgroup("Новая карточка", new DockPanel { Children = { create.Docked(Dock.Right), newKind } }),
                card,
                atlasSearch, atlasHits,
            },
        };
    }

    private void SyncEntities()
    {
        foreach (var sync in _entitySyncs) sync();
    }

    /// <summary>The R26 settings of the chosen note, shown under its text.</summary>
    private Control BuildNoteWiki()
    {
        _noteCategory = new ComboBox { Name = "NoteCategoryBox", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Другое", "Место", "Встреча", "Предание", "Задание", "Тайна" } };
        _notePlayerVisible = Ui.PropertyToggle("NotePlayerVisibleToggle", "Видно игрокам");
        _noteLinkedMap = new ComboBox { Name = "NoteLinkedMapBox", HorizontalAlignment = HorizontalAlignment.Stretch };
        void Apply()
        {
            if (_syncingNoteWiki || _selectedNoteId is not { } id) return;
            var maps = _editor.Atlas?.Maps.Where(m => m.Id != _editor.Session.Document.Id.Value).ToList() ?? [];
            Guid? linked = _noteLinkedMap.SelectedIndex > 0 && _noteLinkedMap.SelectedIndex - 1 < maps.Count ? maps[_noteLinkedMap.SelectedIndex - 1].Id : null;
            _editor.SetNoteWiki(id, linked, (NoteCategory)Math.Max(0, _noteCategory.SelectedIndex), _notePlayerVisible.IsChecked == true);
        }
        _noteCategory.SelectionChanged += (_, _) => Apply();
        _noteLinkedMap.SelectionChanged += (_, _) => Apply();
        _notePlayerVisible.IsCheckedChanged += (_, _) => Apply();
        return Ui.Subgroup("Свойства", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DockPanel { Children = { new TextBlock { Text = "Категория", Width = 92, VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Left), _noteCategory } },
                new DockPanel { Children = { new TextBlock { Text = "Ведёт на карту", Width = 92, VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Left), _noteLinkedMap } },
                _notePlayerVisible,
            },
        });
    }

    private void SyncNoteWiki()
    {
        if (_noteCategory is null || _noteLinkedMap is null || _notePlayerVisible is null) return;
        var note = _editor.Session.Document.Notes.FirstOrDefault(n => n.Id == _selectedNoteId);
        _syncingNoteWiki = true;
        try
        {
            var maps = _editor.Atlas?.Maps.Where(m => m.Id != _editor.Session.Document.Id.Value).ToList() ?? [];
            _noteLinkedMap.ItemsSource = new[] { "Никуда" }.Concat(maps.Select(m => m.Title)).ToArray();
            _noteLinkedMap.IsEnabled = maps.Count > 0;
            if (note is null) return;
            _noteCategory.SelectedIndex = (int)note.Category;
            _notePlayerVisible.IsChecked = note.PlayerVisible;
            var index = note.LinkedMap is { } map ? maps.FindIndex(m => m.Id == map) + 1 : 0;
            _noteLinkedMap.SelectedIndex = Math.Max(0, index);
        }
        finally { _syncingNoteWiki = false; }
    }
}
