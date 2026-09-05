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
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.Assets;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private sealed record GalleryCell(AssetSetSummary Set, AssetVariant? Variant, int SourceRow);
    private sealed record GalleryRow(string? Heading, IReadOnlyList<GalleryCell> Cells);
    private readonly HashSet<string> _expandedGallerySets = new(StringComparer.Ordinal);
    private IReadOnlyList<AssetLibraryRow>? _gallerySourceRows;
    private IReadOnlyList<GalleryRow> _galleryRows = [];
    private int _galleryColumns = 4;
    private bool _galleryResizeQueued;
    private CancellationTokenSource? _librarySearchCancellation;
    public Window? AssetCatalogWindowForSmoke => _assetCatalogWindow;
    public int GalleryRealizedCards => _libraryGrid.GetVisualDescendants().OfType<Button>().Count(b => b.Tag is GalleryCell);
    private sealed record CompactVariant(AssetVariant Variant, int Index);
    private IReadOnlyList<AssetVariant>? _compactVariants;

    private ListBox BuildCompactVariantList() => new()
    {
        Name = "StampVariantsList", MaxHeight = 112,
        ItemTemplate = new FuncDataTemplate<CompactVariant[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var cell in row ?? []) panel.Children.Add(BuildVariantChoice(cell.Variant, cell.Index, "StampVariant_", 40));
            return panel;
        }, supportsRecycling: false),
    };

    private ListBox BuildCompactSetList(string prefix) => new()
    {
        Name = prefix + "List", MaxHeight = 112,
        ItemTemplate = new FuncDataTemplate<AssetSetSummary[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
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
            list.ItemsSource = sets.Chunk(3).ToArray();
        }
        foreach (var button in list.GetVisualDescendants().OfType<Button>())
            Ui.SetSelected(button, button.Name?.EndsWith("_" + Sanitize(_editor.SelectedSetId ?? ""), StringComparison.Ordinal) == true);
    }

    private void RebuildGalleryRows()
    {
        var result = new List<GalleryRow>();
        var cells = new List<GalleryCell>(_galleryColumns);
        void Flush()
        {
            if (cells.Count == 0) return;
            result.Add(new(null, cells.ToArray()));
            cells.Clear();
        }
        var rows = _editor.LibraryRows;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.IsHeader) { Flush(); result.Add(new(row.Title, [])); continue; }
            if (row.Card is not { } card) continue;
            cells.Add(new(card, null, index));
            if (cells.Count == _galleryColumns) Flush();
            if (!_expandedGallerySets.Contains(card.SetId)) continue;
            Flush();
            result.Add(new(card.Name + " · варианты", []));
            foreach (var variant in _editor.VariantsForSet(card.SetId))
            {
                cells.Add(new(card, variant, index));
                if (cells.Count == _galleryColumns) Flush();
            }
            Flush();
        }
        Flush();
        var scroll = _libraryGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset ?? default;
        _galleryRows = result;
        _libraryGrid.ItemsSource = result;
        _reportedLibraryRange = (-1, -1);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (scroll is not null) scroll.Offset = offset;
            RefreshGallerySelection();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private Control BuildGalleryRow(GalleryRow? row)
    {
        // Virtualized presenters clear their content with null when a row is recycled.
        if (row is null) return new Border();
        if (row.Heading is { } heading)
            return new TextBlock { Text = heading, Margin = new Thickness(6, 12, 6, 6) }.Classed("role-panel");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", _galleryColumns))),
            ColumnSpacing = 10, Margin = new Thickness(4, 4, 4, 10) };
        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            var card = BuildGalleryCard(cell);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        return grid;
    }

    private Control BuildGalleryCard(GalleryCell cell)
    {
        var path = cell.Variant is { } variant ? _editor.LibraryThumbnailPathForVariant(variant.Id)
            : _editor.LibraryThumbnailPath(cell.Set);
        var preview = _thumbnails.Image(path);
        var choose = new Button
        {
            Name = cell.Variant is { } chosen ? "GalleryVariant_" + Sanitize(chosen.Id) : "GalleryCover_" + Sanitize(cell.Set.SetId),
            Tag = cell, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 7, Children =
                {
                    new Border { Height = 118, Background = new SolidColorBrush(Color.Parse("#FFB8BCC2")), Child = preview is null
                        ? new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 8, Children =
                            { MapIcons.Stroked(MapIcons.Missing, 24), new TextBlock { Text = "Нет превью", HorizontalAlignment = HorizontalAlignment.Center }.Classed("role-caption") } }
                        : new Image { Source = preview, Stretch = Stretch.Uniform } },
                    new TextBlock { Text = cell.Variant?.Label ?? cell.Set.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        }.Classed("asset-card");
        AutomationProperties.SetName(choose, "Выбрать " + (cell.Variant?.Label ?? cell.Set.Name));
        ToolTip.SetTip(choose, cell.Variant?.Label ?? cell.Set.Name);
        Ui.SetSelected(choose, GalleryCellSelected(cell));
        choose.Click += (_, e) =>
        {
            e.Handled = true;
            // The event finishes in its own window before it is hidden; it is never rerouted to the canvas.
            if (!_editor.ChooseGalleryVariant(cell.Set.SetId, cell.Variant?.Id, _assetCatalogAutoCloseForStamp)) return;
            RefreshGallerySelection();
            if (_assetCatalogAutoCloseForStamp) CloseAssetCatalogAndArmPlacement();
            else RefreshLibraryPanel();
        };
        var body = new StackPanel { Spacing = 5, Children = { choose } };
        if (cell.Variant is null)
        {
            var count = Ui.TextButton("GalleryExpand_" + Sanitize(cell.Set.SetId),
                $"{cell.Set.VariantCount} вариантов " + (_expandedGallerySets.Contains(cell.Set.SetId) ? "▴" : "▾"),
                () => { if (!_expandedGallerySets.Add(cell.Set.SetId)) _expandedGallerySets.Remove(cell.Set.SetId); RebuildGalleryRows(); }, "compact");
            count.Click += (_, e) => e.Handled = true;
            body.Children.Add(count);
            if (cell.Set.Favorite) body.Children.Add(new TextBlock { Text = "★ Избранное", HorizontalAlignment = HorizontalAlignment.Center }.Classed("faint"));
        }
        return body;
    }

    private bool GalleryCellSelected(GalleryCell cell) => _editor.SelectedSetId == cell.Set.SetId
        && (cell.Variant is null || _editor.CurrentVariant?.Id == cell.Variant.Id);

    private void RefreshGallerySelection()
    {
        foreach (var button in _libraryGrid.GetVisualDescendants().OfType<Button>())
            if (button.Tag is GalleryCell cell) Ui.SetSelected(button, GalleryCellSelected(cell));
    }

    private void ReportGalleryRange()
    {
        var sourceRows = new HashSet<int>();
        foreach (var container in _libraryGrid.GetRealizedContainers())
        {
            var index = _libraryGrid.IndexFromContainer(container);
            if (index < 0 || index >= _galleryRows.Count) continue;
            foreach (var cell in _galleryRows[index].Cells) sourceRows.Add(cell.SourceRow);
        }
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
            await Task.Delay(140, cancellation.Token);
            await _editor.SearchLibraryAsync((_assetSearch.Text ?? "").Trim(), cancellation.Token);
            if (!cancellation.IsCancellationRequested) SyncAssetList();
        }
        catch (OperationCanceledException) { }
    }
}
