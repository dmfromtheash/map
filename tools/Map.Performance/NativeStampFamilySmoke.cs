using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.Assets;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

public sealed partial class MountainFoundationApplication
{
    private void ImportOtherMountainFamilies()
    {
        _report.RequiredCount = 9;
        var previous = Environment.GetEnvironmentVariable("MAP_MOUNTAIN_PACK_ROOT");
        var root = Path.GetFullPath(Path.Combine(MountainFoundationPack.Root, "..", "Mountains.Images25"));
        try
        {
            Environment.SetEnvironmentVariable("MAP_MOUNTAIN_PACK_ROOT", root);
            MountainFoundationPack.Import(_catalog, MountainFoundationPack.Load());
        }
        finally { Environment.SetEnvironmentVariable("MAP_MOUNTAIN_PACK_ROOT", previous); }
    }

    private async Task ExecuteStampFamily()
    {
        await Settle();
        var style = AssetIdentity.CollectionId("Горы · Живописный рельеф");
        var family = _catalog.Sets.Where(s => s.CollectionId == style).OrderBy(s => s.ImportedUtc).ToArray();
        Check("same eighteen mountain assets and four styles", _catalog.Sets.Count == 18 && family.Length == 4);
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel();
        _view.OpenAssetCatalog(); await Settle();
        var catalogWindow = _view.AssetCatalogWindowForSmoke!;
        var tile = catalogWindow.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "GalleryCover_" + family[0].Id.Replace('-', '_'));
        tile.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!)); await Settle();
        Check("catalog choice returns to armed canvas", !_view.AssetCatalogVisible && _editor.PendingPlacement is not null);
        await Rendered(); Capture(_view, "family-first");
        var choices = _view.GetVisualDescendants().OfType<ToggleButton>()
            .Where(b => b.Name?.StartsWith("StampVariant_", StringComparison.Ordinal) == true).ToArray();
        var strip = _view.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "StampVariantsList");
        Check("four family thumbnails visible without clipping", choices.Length == 4 && choices.Count(b => b.IsChecked == true) == 1
            && _view.StampCompactCurrentThumbnailVisibleForSmoke && choices.All(b =>
                b.TranslatePoint(new Point(0, 0), strip) is { } p && p.X >= 0 && p.Y >= 0
                && p.X + b.Bounds.Width <= strip.Bounds.Width && p.Y + b.Bounds.Height <= strip.Bounds.Height));
        _editor.SetStampScalePercent(150, commit: true); _editor.SetStampRotationDegrees(15, commit: true);
        var before = _editor.Session.Document; var undo = _editor.Session.UndoDepth;
        var selected = family[^1];
        var choice = choices.Single(b => b.Name == "StampVariant_" + selected.CoverVariantId);
        choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("thumbnail switches exact mountain without changing map", _editor.SelectedSetId == selected.Id
            && ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == undo);
        Check("switch keeps scale and rotation", _editor.StampToolSettings.ScalePercent == 150
            && _editor.StampToolSettings.RotationDegrees == 15);
        await Rendered(); Capture(_view, "family-switched");
        Capture((Control)_view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StampPreviewButton").Parent!, "family-panel");
        _view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StampNextVariantButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        var next = _editor.SelectedSetId;
        _view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StampPreviousVariantButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("arrows cycle within family and back", next == family[0].Id && _editor.SelectedSetId == selected.Id
            && _view.StampCompactVariantCountForSmoke == 4);
        var count = before.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _editor.Session.Viewport.Set(0, 0, .65); await Rendered();
        var screen = _editor.Session.Viewport.WorldToScreen(900, 550);
        _view.Canvas.ClickForSmoke(screen.X, screen.Y); await Settle();
        var placed = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Where(o => !before.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Any(old => old.Id == o.Id)).ToArray();
        Check("canvas places the chosen family image", placed.Length == 1
            && placed[0].Stamp?.AssetSha256 == _catalog.FindVariant(selected.CoverVariantId)!.Sha256);
        await Rendered(); Capture(_view, "family-placed");
        _editor.Undo();
        Check("placement undo restores object count", _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count);
        _editor.Redo();
        var save = Path.Combine(Workspace, "family.mapproj");
        using var images = new ExportStampImageSource(_editor.Session.Document, null, _catalog);
        var thumbnail = ThumbnailRenderer.RenderPng(_editor.Session.Document, stamps: images);
        _store.Save(_editor.Session.Document, save, new SaveMetadata("Map stamp family", DateTimeOffset.UtcNow), thumbnail);
        var reopened = MapProjectStore.Open(save).Document;
        Check("save reopen keeps exact chosen image", reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Single(o => o.Id == placed.Single().Id).Stamp?.AssetSha256 == placed.Single().Stamp?.AssetSha256);
    }
}
