using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// The part «Варианты карты» at the top of «Вид» (R09e; owner 2026-11-11: «Плитки как у пакетов», «Вариант помнит
/// правки сам»): every named variant of the map as a picture of this map in it, drawn by the program like every picture
/// of the page; a click shows the map in the variant (one undo step). While a variant is chosen, the light, the style
/// and the shown layers set on the map belong to it. Under the tiles: the name of the chosen variant, «+ Запомнить» and,
/// on a map without variants, the starting three «День», «Ночь», «Политическая».
/// </summary>
public sealed partial class EditorView
{
    private const string VariantPrefix = "variant-";
    private UniformGrid? _variantGrid;
    private TextBox? _variantName;
    private Button? _variantStarters;
    private string? _variantSignature;

    private static Guid VariantOf(string key) => Guid.Parse(key.AsSpan(VariantPrefix.Length));

    private Border BuildVariantPart()
    {
        var state = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
        _lookState["variant"] = state;
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { MapIcons.Stroked(MapIcons.LineLayers, 16, Application.Current?.FindResource("MapAccentBrush") as IBrush ?? Brushes.Goldenrod),
                new TextBlock { Text = "Варианты карты", FontSize = 15, VerticalAlignment = VerticalAlignment.Center }.Classed("panel-title") },
        }.Docked(Dock.Left));
        head.Children.Add(state.Docked(Dock.Right));

        _variantGrid = new UniformGrid { Columns = 2, Name = "MapVariantGrid" };
        _variantName = new TextBox
        {
            Name = "MapVariantName", PlaceholderText = "Имя выбранного варианта", MaxLength = MapVariants.MaxNameLength, FontSize = 13,
        };
        AutomationProperties.SetName(_variantName, "Имя выбранного варианта");
        ToolTip.SetTip(_variantName, "Имя выбранного варианта. Enter — сохранить имя.");
        void CommitName()
        {
            if (_variantName.IsVisible && _editor.Session.Document.Variants.Active is { } active
                && _editor.RenameMapVariant(active, _variantName.Text ?? "")) SyncLookPage();
        }
        _variantName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        _variantName.LostFocus += (_, _) => CommitName();

        _variantStarters = Ui.TextButton("MapVariantStarters", "Создать «День», «Ночь», «Политическая»", () =>
        {
            if (_editor.AddStarterMapVariants()) AfterVariantsChanged();
        }, "compact");
        _variantStarters.HorizontalAlignment = HorizontalAlignment.Stretch;
        _variantStarters.WithTip("Три варианта из вида этой карты: «День» — свет днём, «Ночь» — свет ночью, «Политическая» — политический режим без стиля и света. Ctrl+Z уберёт их.");
        var add = Ui.TextButton("MapVariantAdd", "+ Запомнить текущий вид как вариант", () =>
        {
            if (_editor.AddMapVariant()) AfterVariantsChanged();
        }, "compact");
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.WithTip("Новый вариант: какие слои показаны, стиль, свет и политический режим этой карты. Рисунок карты общий для всех вариантов.");
        FillVariantGrid();
        return new Border
        {
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9, 12, 10),
            Child = new StackPanel { Spacing = 7, Children = { head, _variantGrid, _variantName, _variantStarters, add } },
        }.Named("LookPart_variant");
    }

    private void AfterVariantsChanged()
    {
        SyncAppearanceSections();
        SyncLookParts();
    }

    /// <summary>The tiles of the variants, rebuilt when a variant is added, removed or renamed.</summary>
    private void FillVariantGrid()
    {
        if (_variantGrid is null) return;
        var variants = _editor.Session.Document.Variants;
        var items = variants.Items.IsDefault ? [] : variants.Items;
        var signature = string.Join("|", items.Select(v => v.Id + ":" + v.Name));
        if (signature == _variantSignature) return;
        _variantSignature = signature;
        _variantGrid.Children.Clear();
        foreach (var key in _lookTiles.Keys.Where(k => k.StartsWith(VariantPrefix, StringComparison.Ordinal)).ToList())
        {
            _lookTiles.Remove(key);
            if (_lookPreviews.Remove(key, out var old)) old.Dispose();
            _lookPreviewDocuments.Remove(key);
        }
        foreach (var variant in items) _variantGrid.Children.Add(VariantTile(variant));
        _variantGrid.IsVisible = items.Length > 0;
        _lookPreviewSignature = null;
    }

    private Button VariantTile(MapVariant variant)
    {
        var key = VariantPrefix + variant.Id.ToString("D");
        var picture = new Border
        {
            Height = PackPictureHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            BorderThickness = new Thickness(1.5), BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        };
        var button = new Button
        {
            Name = "MapVariant_" + variant.Id.ToString("N"), Tag = key, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { picture, new TextBlock { Text = variant.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } },
            },
        };
        AutomationProperties.SetName(button, "Вариант карты «" + variant.Name + "»");
        ToolTip.SetTip(button, $"«{variant.Name}»: показанные слои, стиль, свет и политический режим. Щелчок — карта в этом варианте, Ctrl+Z вернёт. Правки вида, пока вариант выбран, идут в него. Правый щелчок — удалить.");
        button.Click += (_, _) => ApplyLook(key);
        var id = variant.Id;
        button.ContextMenu = new ContextMenu
        {
            Items = { MenuEntry("Удалить вариант «" + variant.Name + "»", () => { if (_editor.RemoveMapVariant(id)) AfterVariantsChanged(); }, "MapVariantRemove_" + variant.Id.ToString("N")) },
        };
        _lookTiles[key] = (button, picture);
        if (_lookPreviews.TryGetValue(key, out var ready)) picture.Background = new ImageBrush(ready) { Stretch = Stretch.UniformToFill };
        return button;
    }

    private void SyncVariantPart()
    {
        FillVariantGrid();
        var variants = _editor.Session.Document.Variants;
        var active = variants.Active is { } id ? variants.Find(id) : null;
        if (_lookState.TryGetValue("variant", out var state))
            state.Text = variants.IsDefault ? "нет" : active?.Name ?? "не выбран";
        if (_variantName is not null)
        {
            _variantName.IsVisible = active is not null;
            if (active is not null && !_variantName.IsFocused) _variantName.Text = active.Name;
        }
        if (_variantStarters is not null) _variantStarters.IsVisible = variants.Items.IsDefaultOrEmpty;
    }
}
