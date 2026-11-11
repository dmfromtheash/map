using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Views;

/// <summary>
/// The cards of Свет, Стиль and Море and how their pictures are drawn. They live on one page only, «Вид» of the
/// section «Оформление» (owner, 2026-10-31: «у нас дублируются функции на экране… свет и внизу и сбоку» - the ribbon
/// under the map that repeated the same cards is gone; its fine settings open under their part of «Вид»). A picture is
/// the open map in that light, style or sea, drawn by the same scene renderer as the export, off the UI thread, around
/// the middle of the view at a print-like scale, so it shows the medium rather than a blur of the whole map.
/// «Осмотреть…» opens a large preview with 50/100/200 % and the other styles beside it.
/// </summary>
public sealed partial class EditorView
{
    /// <summary>The three parts of «Вид».</summary>
    private enum LookPart { Light, Styles, Sea }

    private const int CardWidth = 150, CardHeight = 84;
    /// <summary>Preview pixels per document pixel: the medium at about the scale it is painted at.</summary>
    private const double CardScale = 0.6;

    /// <summary>The cards of Стиль: no style, then every preset of every family.</summary>
    private static IReadOnlyList<(string Key, string Title, string Family, MapStylePreset? Preset)> StyleCards { get; } =
        [("none", "Без стиля", "Все", null),
         .. Enum.GetValues<MapStylePreset>().Select(p => ("style-" + p, EditorViewModel.PresetTitle(p), EditorViewModel.StyleFamilyTitle(p), (MapStylePreset?)p))];

    private static IReadOnlyList<(string Key, string Title, double? Time)> LightCards { get; } =
        [("light-off", "Без света", null), ("light-day", "День", 0.0), ("light-evening", "Вечер", 0.5), ("light-night", "Ночь", 1.0)];

    /// <summary>
    /// Opens «Оформление» with the fine settings of Свет or Стиль under their part of «Вид» (the panel sections «Свет»
    /// and «Стили», Ctrl+K and the performance profiles come here).
    /// </summary>
    public void ShowAppearanceRibbon(bool styles) => ShowLookPart(styles ? LookPart.Styles : LookPart.Light, fine: true);

    /// <summary>Brings the fine settings of «Вид» in line with the document (after an edit, Undo or Redo).</summary>
    private void SyncLookParts()
    {
        foreach (var sync in _lightSyncs) sync();
        foreach (var sync in _styleSyncs) sync();
        foreach (var sync in _seaSyncs) sync();
        // Placing lights by click belongs to the open settings of Свет.
        var lightOpen = _workspace == EditorWorkspace.Appearance && _lookFineOpen == LookPart.Light;
        if (_editor.LightPlacementArmed && !lightOpen) _editor.ArmLightPlacement(false);
        // The open settings of Свет mark the map's lights; a click on a mark chooses that light (R14).
        _editor.ShowLightMarkers(lightOpen);
        SyncLookPage();
    }

    private static string TimeTitle(double t) => t < 0.25 ? "День" : t < 0.75 ? "Вечер" : "Ночь";

    /// <summary>A chosen pick: a brass frame and the class tests read.</summary>
    private static void MarkSelected(Button button, bool on)
    {
        Ui.SetClass(button, "selected", on);
        button.BorderBrush = on
            ? Application.Current?.FindResource("MapAccentBrush") as IBrush
            : Application.Current?.FindResource("MapBorderBrush") as IBrush;
    }

    private void ApplyLookCard(string key)
    {
        if (key.StartsWith(PackPrefix, StringComparison.Ordinal)) { if (PackOf(key) is { } pack) _editor.ApplyStylePack(pack); return; }
        if (key.StartsWith(VariantPrefix, StringComparison.Ordinal)) { _editor.SwitchMapVariant(VariantOf(key)); return; }
        if (key.StartsWith("sea-", StringComparison.Ordinal)) { ApplySeaCard(key); return; }
        if (key == "none") { _editor.SetStyleEnabled(false); return; }
        if (key == "light-off") { _editor.SetLightingEnabled(false); return; }
        if (key.StartsWith("light-", StringComparison.Ordinal))
        {
            if (!_editor.ShownLighting.Enabled) _editor.SetLightingEnabled(true);
            _editor.SetTimeOfDay(LightCards.First(c => c.Key == key).Time ?? 0, true);
            return;
        }
        var preset = Enum.Parse<MapStylePreset>(key["style-".Length..]);
        // The picture of the style that was switched off brings it back with every setting it had (the former switch
        // «Стиль карты включён», removed as a second copy of «Без стиля»).
        if (!_editor.ShownStyle.Enabled && _editor.ShownStyle.Preset == preset) { _editor.SetStyleEnabled(true); return; }
        _editor.ApplyStylePreset(preset);
    }

    // ------------------------------------------------------------------ previews

    /// <summary>The document a card shows: the open map with that card's style or light.</summary>
    private MapDocument CardDocument(MapDocument document, string key)
    {
        if (key.StartsWith(PackPrefix, StringComparison.Ordinal)) return PackOf(key)?.ApplyTo(document) ?? document;
        if (key.StartsWith(VariantPrefix, StringComparison.Ordinal)) return Map.Presentation.ViewModels.EditorViewModel.VariantPreview(document, VariantOf(key));
        if (key.StartsWith("sea-", StringComparison.Ordinal)) return SeaCardDocument(document, key);
        if (key == "none") return document with { Style = null };
        if (key == "light-off") return document with { Lighting = null };
        if (key.StartsWith("light-", StringComparison.Ordinal))
        {
            var t = LightCards.First(c => c.Key == key).Time ?? 0;
            return document with { Lighting = (document.Lighting ?? new MapLighting()) with { Enabled = true, TimeOfDay = t } };
        }
        var preset = Enum.Parse<MapStylePreset>(key["style-".Length..]);
        var current = document.Style;
        var style = current is { Enabled: true } && current.Preset == preset ? current : MapStyle.For(preset) with { Enabled = true };
        return document with { Style = style };
    }

    /// <summary>The world area around the middle of the view that a card of the given size shows.</summary>
    private WorldBounds PreviewArea(int width, int height, double scale)
    {
        var (cx, cy) = PreviewCentre().Centre;
        double w = width / scale, h = height / scale;
        return new WorldBounds(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
    }

    /// <summary>
    /// What a card looks at: the asset nearest the middle of the view (a style shows itself on a mountain, not on
    /// empty ground), with a frame a little larger than that asset; the middle of the view when there is none.
    /// </summary>
    private ((double X, double Y) Centre, double? Span) PreviewCentre()
    {
        var viewport = _editor.Session.Viewport;
        var (cx, cy) = viewport.ScreenToWorld(_canvas.Bounds.Width / 2, _canvas.Bounds.Height / 2);
        var (left, top) = viewport.ScreenToWorld(0, 0);
        var (right, bottom) = viewport.ScreenToWorld(_canvas.Bounds.Width, _canvas.Bounds.Height);
        SceneObject? best = null;
        var bestScore = double.MaxValue;
        foreach (var layer in _editor.Session.Document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible) continue;
            foreach (var o in layer.Objects)
            {
                if (o.Kind != SceneObjectKind.Stamp || !o.Visible) continue;
                double ox = o.X + o.Width / 2, oy = o.Y + o.Height / 2;
                if (ox < left || ox > right || oy < top || oy > bottom) continue;
                // Near the middle, and rather a big asset than a pebble.
                var score = Math.Sqrt((ox - cx) * (ox - cx) + (oy - cy) * (oy - cy)) - 0.5 * Math.Max(o.Width, o.Height);
                if (score < bestScore) { bestScore = score; best = o; }
            }
        }
        return best is null ? ((cx, cy), null) : ((best.X + best.Width / 2, best.Y + best.Height / 2), 1.35 * Math.Max(best.Width, best.Height * 16 / 9.0));
    }

    /// <summary>The area and the scale of a preview of the given pixel size: the asset's frame, else the fixed scale.</summary>
    private (WorldBounds Area, double Scale) PreviewFrame(int width, int height, double fallbackScale)
    {
        var (centre, span) = PreviewCentre();
        var scale = span is { } s ? Math.Clamp(width / s, 0.2, 2.5) : fallbackScale;
        double w = width / scale, h = height / scale;
        return (new WorldBounds(centre.X - w / 2, centre.Y - h / 2, centre.X + w / 2, centre.Y + h / 2), scale);
    }

    /// <summary>
    /// One preview, drawn like an export (CPU raster, no editor chrome), as RGBA premultiplied bytes. It runs off the
    /// UI thread, so its pictures are leased from the canvas's source for the time it draws (audit 2026-11-01).
    /// </summary>
    private static byte[] RenderPreviewPixels(MapDocument document, WorldBounds area, int width, int height, double scale,
        IStampImageSource? source)
    {
        using var stamps = source?.Lease();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("No preview surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x1A, 0x14, 0x0E));
        canvas.Scale((float)scale);
        canvas.Translate((float)-area.Left, (float)-area.Top);
        SceneRenderer.Render(canvas, document, new Map.Rendering.RenderOptions
        {
            IsExport = true, Stamps = stamps, ViewScale = scale, VisibleWorld = area,
        });
        canvas.Flush();
        var bytes = new byte[info.BytesSize];
        using var pixmap = surface.PeekPixels();
        Marshal.Copy(pixmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    private static Bitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(Avalonia.Platform.PixelFormats.Rgba8888, Avalonia.Platform.AlphaFormat.Premul,
                handle.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally { handle.Free(); }
    }

    // ------------------------------------------------------------------ inspection

    private Window? _styleInspectorWindow;

    /// <summary>
    /// A large preview of a style on the open map at 50, 100 or 200 %, dragged with the mouse, with
    /// every other style beside it for comparison on the same spot. «Применить» applies the shown style.
    /// </summary>
    private void OpenStyleInspector()
    {
        if (_styleInspectorWindow is not null || TopLevel.GetTopLevel(this) is not Window owner) return;
        const int ViewWidth = 1040, ViewHeight = 600;
        var style = _editor.ShownStyle;
        var shown = style.Enabled ? "style-" + style.Preset : "style-" + StyleCards[1].Preset;
        var zoom = 1.0;
        var center = PreviewArea(2, 2, 1);
        var cx = (center.Left + center.Right) / 2;
        var cy = (center.Top + center.Bottom) / 2;
        var image = new Image { Width = ViewWidth, Height = ViewHeight, Stretch = Stretch.None }.Named("StyleInspectorImage");
        var frame = new Border
        {
            Width = ViewWidth, Height = ViewHeight, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.SizeAll),
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x0E)), Child = image,
        };
        var title = new TextBlock { FontSize = 20 };
        var status = new TextBlock().Classed("faint", "role-caption");
        var window = new Window
        {
            Name = "StyleInspectorWindow", Title = "Осмотр стиля", Width = ViewWidth + 300, Height = ViewHeight + 140,
            MinWidth = 900, MinHeight = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowActivated = owner.ShowActivated, ShowInTaskbar = false,
        };
        _styleInspectorWindow = window;
        Bitmap? bitmap = null;
        CancellationTokenSource? work = null;
        var zoomButtons = new List<(ToggleButton Button, double Zoom)>();
        var compare = new StackPanel { Spacing = 6 };

        void Render()
        {
            work?.Cancel();
            var current = work = new CancellationTokenSource();
            var token = current.Token;
            var document = CardDocument(_editor.Session.Document, shown);
            var area = new WorldBounds(cx - ViewWidth / 2.0 / zoom, cy - ViewHeight / 2.0 / zoom, cx + ViewWidth / 2.0 / zoom, cy + ViewHeight / 2.0 / zoom);
            var stamps = _editor.StampImages;
            var card = StyleCards.First(c => c.Key == shown);
            title.Text = card.Title + " · " + card.Family;
            status.Text = "Рисуется…";
            foreach (var (button, z) in zoomButtons) { button.IsChecked = Math.Abs(z - zoom) < 1e-6; Ui.SetClass(button, "active", button.IsChecked == true); }
            UiTasks.Forget(this, () => Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                var pixels = RenderPreviewPixels(document, area, ViewWidth, ViewHeight, zoom, stamps);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    var next = ToBitmap(pixels, ViewWidth, ViewHeight);
                    image.Source = next;
                    bitmap?.Dispose();
                    bitmap = next;
                    status.Text = $"Масштаб {zoom * 100:0} % · тяните мышью, колесо — масштаб";
                });
            }, token));
        }

        foreach (var z in new[] { 0.5, 1.0, 2.0 })
        {
            var captured = z;
            var button = new ToggleButton { Content = $"{z * 100:0} %", Padding = new Thickness(10, 3) }.Named($"StyleInspectorZoom{z * 100:0}");
            button.Click += (_, _) => { zoom = captured; Render(); };
            zoomButtons.Add((button, z));
        }

        foreach (var card in StyleCards.Skip(1))
        {
            var captured = card.Key;
            var thumb = new Border { Width = 120, Height = 68, CornerRadius = new CornerRadius(4), ClipToBounds = true };
            if (_lookPreviews.TryGetValue(card.Key, out var preview)) thumb.Background = new ImageBrush(preview) { Stretch = Stretch.UniformToFill };
            var button = new Button
            {
                Name = "StyleInspectorPick_" + card.Key, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { thumb, new StackPanel { Children = { new TextBlock { Text = card.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 120 }, new TextBlock { Text = card.Family, FontSize = 11 }.Classed("faint") } } } },
            }.Classed("swatch");
            button.Click += (_, _) => { shown = captured; Render(); };
            compare.Children.Add(button);
        }

        Point? dragFrom = null;
        frame.PointerPressed += (_, e) => dragFrom = e.GetPosition(frame);
        frame.PointerReleased += (_, e) =>
        {
            if (dragFrom is not { } from) return;
            var to = e.GetPosition(frame);
            dragFrom = null;
            cx -= (to.X - from.X) / zoom; cy -= (to.Y - from.Y) / zoom;
            Render();
        };
        frame.PointerMoved += (_, e) =>
        {
            if (dragFrom is not { } from) return;
            var to = e.GetPosition(frame);
            image.RenderTransform = new TranslateTransform(to.X - from.X, to.Y - from.Y);
        };
        image.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) image.RenderTransform = null; };
        frame.PointerWheelChanged += (_, e) =>
        {
            zoom = e.Delta.Y > 0 ? Math.Min(2, zoom * 2) : Math.Max(0.5, zoom / 2);
            Render();
            e.Handled = true;
        };

        var apply = Ui.CommandButton("StyleInspectorApply", "Применить", MapIcons.Check, () =>
        {
            _editor.ApplyStylePreset(Enum.Parse<MapStylePreset>(shown["style-".Length..]));
            SyncLookParts();
            window.Close();
        }, "primary");
        var close = Ui.TextButton("StyleInspectorClose", "Закрыть", () => window.Close(), "compact");
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (button, _) in zoomButtons) zoomRow.Children.Add(button);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,260"), RowDefinitions = new RowDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 10 };
        var top = new DockPanel { Children = { zoomRow.Docked(Dock.Right), title } };
        Grid.SetColumnSpan(top, 2); layout.Children.Add(top);
        Grid.SetRow(frame, 1); layout.Children.Add(frame);
        var side = new ScrollViewer { Content = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "На том же месте" }.Classed("faint"), compare } } };
        Grid.SetRow(side, 1); Grid.SetColumn(side, 1); layout.Children.Add(side);
        var bottom = new DockPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { close, apply } }.Docked(Dock.Right), status } };
        Grid.SetRow(bottom, 2); Grid.SetColumnSpan(bottom, 2); layout.Children.Add(bottom);
        window.Content = new Border { Padding = new Thickness(16), Child = layout };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        window.Closed += (_, _) => { work?.Cancel(); bitmap?.Dispose(); _styleInspectorWindow = null; };
        Render();
        UiTasks.Forget(this, () => window.ShowDialog(owner));
    }
}
