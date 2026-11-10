using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// The part «Пакет стиля» at the top of «Вид» (R09d, DEC-094; the owner's pick of the button mockup 2026-11-10: «Плитки
/// 2×2»): the four packs as large pictures of this very map in each pack, drawn by the program like every picture of the
/// page; a click puts the whole map into the pack in one undo step. Under them «Мой пакет» once saved, and the button
/// that keeps the look of this map as the owner's own pack.
/// </summary>
public sealed partial class EditorView
{
    private const string PackPrefix = "pack-";
    private const int PackPictureHeight = 84;
    private UniformGrid? _packGrid;

    private MapStylePack? PackOf(string key)
    {
        var id = key[PackPrefix.Length..];
        return id == MapStylePack.OwnId ? _editor.OwnStylePack : MapStylePack.Find(id);
    }

    private Control BuildPackPart()
    {
        var state = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
        _lookState["pack"] = state;
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { MapIcons.Stroked(MapIcons.LinePalette, 16, Application.Current?.FindResource("MapAccentBrush") as IBrush ?? Brushes.Goldenrod),
                new TextBlock { Text = "Пакет стиля", FontSize = 15, VerticalAlignment = VerticalAlignment.Center }.Classed("panel-title") },
        }.Docked(Dock.Left));
        head.Children.Add(state.Docked(Dock.Right));

        _packGrid = new UniformGrid { Columns = 2, Name = "StylePackGrid" };
        FillPackGrid();
        var save = Ui.TextButton("StylePackSaveOwn", "+ Сохранить текущий вид как свой пакет", () =>
        {
            if (!_editor.SaveOwnStylePack()) return;
            FillPackGrid();
            _lookPreviewSignature = null;
            QueueLookPreviews();
            SyncLookPage();
        }, "compact");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.WithTip("Запомнить стиль, обрывы, реки, дороги, море и подписи этой карты как «Мой пакет» — он появится плиткой выше и применяется на любой карте.");
        return new Border
        {
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9, 12, 10),
            Child = new StackPanel { Spacing = 7, Children = { head, _packGrid, save } },
        }.Named("LookPart_pack");
    }

    /// <summary>The tiles of the packs: the four ready ones, then «Мой пакет» when the owner has saved one.</summary>
    private void FillPackGrid()
    {
        if (_packGrid is null) return;
        _packGrid.Children.Clear();
        foreach (var key in _lookTiles.Keys.Where(k => k.StartsWith(PackPrefix, StringComparison.Ordinal)).ToList()) _lookTiles.Remove(key);
        var packs = Map.Presentation.ViewModels.EditorViewModel.StylePacks.ToList();
        if (_editor.OwnStylePack is { } own) packs.Add(own);
        foreach (var pack in packs) _packGrid.Children.Add(PackTile(pack));
    }

    private Button PackTile(MapStylePack pack)
    {
        var key = PackPrefix + pack.Id;
        var picture = new Border
        {
            Height = PackPictureHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            BorderThickness = new Thickness(1.5), BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        };
        var button = new Button
        {
            Name = "StylePack_" + pack.Id, Tag = key, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new StackPanel { Spacing = 4, Children = { picture, new TextBlock { Text = pack.Title, FontSize = 13, FontWeight = FontWeight.SemiBold } } },
        };
        AutomationProperties.SetName(button, "Пакет стиля «" + pack.Title + "»");
        ToolTip.SetTip(button, $"«{pack.Title}»: стиль рисунка, обрывы с осыпью, реки, дороги, море и подписи всей карты одним щелчком. Ctrl+Z вернёт прежний вид.");
        button.Click += (_, _) => ApplyLook(key);
        _lookTiles[key] = (button, picture);
        if (_lookPreviews.TryGetValue(key, out var ready)) picture.Background = new ImageBrush(ready) { Stretch = Stretch.UniformToFill };
        return button;
    }

    private void SyncPackState()
    {
        if (!_lookState.TryGetValue("pack", out var state)) return;
        var id = _editor.ActiveStylePackId;
        state.Text = id is null ? "не выбран" : (id == MapStylePack.OwnId ? _editor.OwnStylePack?.Title : MapStylePack.Find(id)?.Title) ?? "";
    }
}
