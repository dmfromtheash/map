using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
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
/// Свет and Стили as a ribbon under the map (DEC-064): two small tabs that show what is on, and,
/// when one is open, a row of cards with a preview of the open map in each style or time of day
/// (a click applies it, one Undo step) beside that tab's strength and effects. The cards are
/// drawn by the same scene renderer as the export, off the UI thread, around the middle of the
/// view at a fixed print-like scale, so a card shows the medium itself rather than a blur of the
/// whole map. «Осмотреть…» opens a large preview with 50/100/200 % and the other styles beside it.
/// </summary>
public sealed partial class EditorView
{
    private enum RibbonTab { None, Light, Styles }

    private const int CardWidth = 150, CardHeight = 84;
    /// <summary>Preview pixels per document pixel: the medium at about the scale it is painted at.</summary>
    private const double CardScale = 0.6;

    private RibbonTab _ribbonTab;
    private Border _ribbon = null!;
    private Control _ribbonBody = null!;
    // Tabs and cards are plain buttons marked by a brass frame: a checked toggle fills with brass and
    // its faint captions became unreadable on it.
    private Button _ribbonLightTab = null!, _ribbonStyleTab = null!;
    private TextBlock _ribbonLightState = null!, _ribbonStyleState = null!;
    private StackPanel _ribbonFamilies = null!;
    private StackPanel _ribbonCards = null!;
    private RetainedSlot _ribbonControls = null!;
    private Button _ribbonInspect = null!;
    private string _ribbonFamily = "Все";
    private readonly Dictionary<string, (Button Button, Border Picture)> _ribbonCardViews = [];
    private readonly Dictionary<string, Bitmap> _ribbonPreviews = [];
    private CancellationTokenSource? _ribbonPreviewWork;
    private string _ribbonPreviewSignature = "";
    private DispatcherTimer? _ribbonPreviewTimer;

    /// <summary>The cards of the Стили tab: no style, then every preset of every family.</summary>
    private static IReadOnlyList<(string Key, string Title, string Family, MapStylePreset? Preset)> StyleCards { get; } =
        [("none", "Без стиля", "Все", null),
         .. Enum.GetValues<MapStylePreset>().Select(p => ("style-" + p, EditorViewModel.PresetTitle(p), EditorViewModel.StyleFamilyTitle(p), (MapStylePreset?)p))];

    private static IReadOnlyList<(string Key, string Title, double? Time)> LightCards { get; } =
        [("light-off", "Без света", null), ("light-day", "День", 0.0), ("light-evening", "Вечер", 0.5), ("light-night", "Ночь", 1.0)];

    /// <summary>Opens the ribbon on Свет or Стили (also used by the performance profile).</summary>
    public void ShowAppearanceRibbon(bool styles)
    {
        _ribbonTab = styles ? RibbonTab.Styles : RibbonTab.Light;
        SyncAppearanceRibbon();
    }

    private Control BuildAppearanceRibbon()
    {
        Button Tab(string name, string icon, string title, out TextBlock state, RibbonTab tab)
        {
            state = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
            var button = new Button
            {
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(9, 3),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        MapIcons.Grid24(icon, 15, Application.Current?.FindResource("MapAccentBrush") as IBrush, 1.9),
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        state,
                    },
                },
            }.Named(name).Classed("ribbon-tab");
            AutomationProperties.SetName(button, title);
            button.Click += (_, _) => { _ribbonTab = _ribbonTab == tab ? RibbonTab.None : tab; SyncAppearanceRibbon(); };
            return button;
        }
        _ribbonLightTab = Tab("RibbonLightTab", MapIcons.LineSun, "Свет", out _ribbonLightState, RibbonTab.Light);
        _ribbonStyleTab = Tab("RibbonStyleTab", MapIcons.LinePalette, "Стили", out _ribbonStyleState, RibbonTab.Styles);
        ToolTip.SetTip(_ribbonLightTab, "Время суток, объём гор, тени, туман, огни и цвет.");
        ToolTip.SetTip(_ribbonStyleTab, "Вся карта в другой технике. Подписи остаются чёткими.");

        _ribbonFamilies = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 0, 0, 0) }.Named("RibbonFamilies");
        foreach (var family in new[] { "Все" }.Concat(StyleCards.Skip(1).Select(c => c.Family).Distinct()))
        {
            var captured = family;
            var chip = new ToggleButton { Content = family, Padding = new Thickness(9, 1), MinHeight = 22 }.Named("RibbonFamily" + family).Classed("ribbon-chip");
            chip.Click += (_, _) => { _ribbonFamily = captured; SyncAppearanceRibbon(); };
            _ribbonFamilies.Children.Add(chip);
        }

        _ribbonInspect = Ui.CommandButton("RibbonInspectButton", "Осмотреть…", MapIcons.FitCanvas, OpenStyleInspector, "compact");
        ToolTip.SetTip(_ribbonInspect, "Большой просмотр стиля на вашей карте: 50, 100 и 200 %, другие стили рядом.");
        var collapse = Ui.IconButton("RibbonCollapseButton", "Свернуть ленту", MapIcons.ArrowDown,
            () => { _ribbonTab = RibbonTab.None; SyncAppearanceRibbon(); });

        var header = new DockPanel
        {
            Margin = new Thickness(8, 4, 8, 4),
            Children =
            {
                collapse.Docked(Dock.Right),
                _ribbonInspect.Docked(Dock.Right),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _ribbonLightTab, _ribbonStyleTab, _ribbonFamilies } },
            },
        };

        _ribbonCards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }.Named("RibbonCards");
        _ribbonControls = new RetainedSlot { Name = "RibbonControls" };
        var cards = new ScrollViewer
        {
            Content = _ribbonCards, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 6),
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), ColumnSpacing = 10, Height = 196, Margin = new Thickness(8, 0, 8, 8) };
        body.Children.Add(cards);
        var controls = new Border
        {
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = _ribbonControls,
        };
        Grid.SetColumn(controls, 1);
        body.Children.Add(controls);
        _ribbonBody = body;

        _ribbon = new Border
        {
            BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
            BorderThickness = new Thickness(0, 1.5, 0, 0),
            Background = Application.Current?.FindResource("MapPanelBrush") as IBrush,
            Child = new DockPanel { Children = { header.Docked(Dock.Top), body } },
        }.Named("AppearanceRibbon").Classed("appearance-ribbon");

        _canvas.ViewportChanged += (_, _) => { if (_ribbonTab != RibbonTab.None) QueueRibbonPreviews(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _ribbonPreviewWork?.Cancel();
            foreach (var bitmap in _ribbonPreviews.Values) bitmap.Dispose();
            _ribbonPreviews.Clear();
        };
        SyncAppearanceRibbon();
        return _ribbon;
    }

    /// <summary>Brings the ribbon in line with the document: tab states, cards, the active card, the controls.</summary>
    private void SyncAppearanceRibbon()
    {
        if (_ribbon is null) return;
        _ribbon.IsVisible = !_isHexWorkshop;
        var lighting = _editor.ShownLighting;
        _ribbonLightState.Text = lighting.Enabled ? TimeTitle(lighting.TimeOfDay) : "выкл.";
        var style = _editor.ShownStyle;
        _ribbonStyleState.Text = style.Enabled ? EditorViewModel.PresetTitle(style.Preset) : "без стиля";
        MarkSelected(_ribbonLightTab, _ribbonTab == RibbonTab.Light);
        MarkSelected(_ribbonStyleTab, _ribbonTab == RibbonTab.Styles);
        _ribbonBody.IsVisible = _ribbonTab != RibbonTab.None;
        // Placing lights by click belongs to the open Свет tab.
        if (_ribbonTab != RibbonTab.Light && _editor.LightPlacementArmed) _editor.ArmLightPlacement(false);
        _ribbonFamilies.IsVisible = _ribbonTab == RibbonTab.Styles;
        _ribbonInspect.IsVisible = _ribbonTab == RibbonTab.Styles;
        foreach (var chip in _ribbonFamilies.Children.OfType<ToggleButton>())
        {
            var on = (string?)chip.Content == _ribbonFamily;
            chip.IsChecked = on;
            Ui.SetClass(chip, "active", on);
        }
        if (_ribbonTab == RibbonTab.None) return;

        RebuildRibbonCards();
        var controls = _ribbonTab == RibbonTab.Styles ? StyleBody() : LightBody();
        if (!ReferenceEquals(_ribbonControls.Content, controls)) _ribbonControls.Content = controls;
        if (_ribbonTab == RibbonTab.Styles) { foreach (var sync in _styleSyncs) sync(); }
        else { foreach (var sync in _lightSyncs) sync(); }
        QueueRibbonPreviews();
    }

    private static string TimeTitle(double t) => t < 0.25 ? "День" : t < 0.75 ? "Вечер" : "Ночь";

    private void RebuildRibbonCards()
    {
        var wanted = _ribbonTab == RibbonTab.Styles
            ? StyleCards.Where(c => _ribbonFamily == "Все" || c.Family == _ribbonFamily || c.Preset is null)
                .Select(c => (c.Key, c.Title, Caption: c.Preset is null ? "как нарисовано" : c.Family)).ToArray()
            : LightCards.Select(c => (c.Key, c.Title, Caption: c.Time is null ? "как нарисовано" : "время суток")).ToArray();
        var keys = wanted.Select(w => w.Key).ToArray();
        var shown = _ribbonCards.Children.OfType<Control>().Select(c => c.Tag as string).ToArray();
        if (!shown.SequenceEqual(keys))
        {
            _ribbonCards.Children.Clear();
            foreach (var (key, title, caption) in wanted)
            {
                var view = CardView(key, title, caption);
                _ribbonCards.Children.Add(view.Button);
            }
        }
        var style = _editor.ShownStyle;
        var lighting = _editor.ShownLighting;
        foreach (var key in keys)
        {
            if (!_ribbonCardViews.TryGetValue(key, out var view)) continue;
            var active = key switch
            {
                "none" => !style.Enabled,
                "light-off" => !lighting.Enabled,
                _ when key.StartsWith("light-", StringComparison.Ordinal) => lighting.Enabled
                    && LightCards.First(c => c.Key == key).Time is { } t && Math.Abs(lighting.TimeOfDay - t) < 0.02,
                _ => style.Enabled && "style-" + style.Preset == key,
            };
            MarkSelected(view.Button, active);
            view.Picture.BorderThickness = new Thickness(active ? 2 : 1);
            view.Picture.BorderBrush = active
                ? Application.Current?.FindResource("MapAccentBrush") as IBrush
                : Application.Current?.FindResource("MapBorderBrush") as IBrush;
            if (_ribbonPreviews.TryGetValue(key, out var bitmap)) view.Picture.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
    }

    /// <summary>The open tab or the applied card: a brass frame and the class tests read.</summary>
    private static void MarkSelected(Button button, bool on)
    {
        Ui.SetClass(button, "selected", on);
        button.BorderBrush = on
            ? Application.Current?.FindResource("MapAccentBrush") as IBrush
            : Application.Current?.FindResource("MapBorderBrush") as IBrush;
    }

    private (Button Button, Border Picture) CardView(string key, string title, string caption)
    {
        if (_ribbonCardViews.TryGetValue(key, out var existing)) return existing;
        var picture = new Border
        {
            Width = CardWidth, Height = CardHeight, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = new TextBlock { Text = "…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Classed("faint"),
        };
        var button = new Button
        {
            Tag = key, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1.5),
            Content = new StackPanel
            {
                Spacing = 3, Width = CardWidth,
                Children =
                {
                    picture,
                    new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = caption, FontSize = 11 }.Classed("faint"),
                },
            },
        }.Classed("swatch", "ribbon-card");
        // The style cards keep the names of the former preset buttons (the profile and the tests find them by name).
        button.Name = key switch
        {
            "none" => "StyleNone",
            _ when key.StartsWith("style-", StringComparison.Ordinal) => "StylePreset" + key["style-".Length..],
            _ => "LightCard" + key["light-".Length..],
        };
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title + (key.StartsWith("style-", StringComparison.Ordinal)
            ? ". " + PresetHint(Enum.Parse<MapStylePreset>(key["style-".Length..])) + " Щелчок применяет стиль — один шаг отмены."
            : ". Щелчок применяет — один шаг отмены."));
        button.Click += (_, _) => { ApplyRibbonCard(key); SyncAppearanceRibbon(); };
        var view = (button, picture);
        _ribbonCardViews[key] = view;
        return view;
    }

    private void ApplyRibbonCard(string key)
    {
        if (key == "none") { _editor.SetStyleEnabled(false); return; }
        if (key == "light-off") { _editor.SetLightingEnabled(false); return; }
        if (key.StartsWith("light-", StringComparison.Ordinal))
        {
            if (!_editor.ShownLighting.Enabled) _editor.SetLightingEnabled(true);
            _editor.SetTimeOfDay(LightCards.First(c => c.Key == key).Time ?? 0, true);
            return;
        }
        _editor.ApplyStylePreset(Enum.Parse<MapStylePreset>(key["style-".Length..]));
    }

    // ------------------------------------------------------------------ previews

    /// <summary>The document a card shows: the open map with that card's style or light.</summary>
    private MapDocument CardDocument(MapDocument document, string key)
    {
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

    private void QueueRibbonPreviews()
    {
        _ribbonPreviewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _ribbonPreviewTimer!.Stop();
            StartRibbonPreviews();
        });
        _ribbonPreviewTimer.Stop();
        _ribbonPreviewTimer.Start();
    }

    private void StartRibbonPreviews()
    {
        if (_ribbonTab == RibbonTab.None || _canvas.Bounds.Width < 1) return;
        var document = _editor.Session.Document;
        var keys = _ribbonCards.Children.OfType<Control>().Select(c => (string)c.Tag!).ToArray();
        var (area, scale) = PreviewFrame(CardWidth * 2, CardHeight * 2, CardScale * 2);
        // A card is redrawn only when the map, the area or the cards change - not on every sync.
        var signature = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(document)}|{area.Left:0}|{area.Top:0}|{scale:0.000}|{string.Join(",", keys)}";
        if (signature == _ribbonPreviewSignature) return;
        _ribbonPreviewSignature = signature;
        _ribbonPreviewWork?.Cancel();
        var work = _ribbonPreviewWork = new CancellationTokenSource();
        var token = work.Token;
        var stamps = _editor.StampImages;
        var docs = keys.Select(k => (Key: k, Document: CardDocument(document, k))).ToArray();
        _ = Task.Run(() =>
        {
            foreach (var (key, cardDocument) in docs)
            {
                if (token.IsCancellationRequested) return;
                byte[] pixels;
                try { pixels = RenderPreviewPixels(cardDocument, area, CardWidth * 2, CardHeight * 2, scale, stamps); }
                catch (Exception) when (!token.IsCancellationRequested) { continue; }
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    var bitmap = ToBitmap(pixels, CardWidth * 2, CardHeight * 2);
                    if (_ribbonPreviews.Remove(key, out var old)) old.Dispose();
                    _ribbonPreviews[key] = bitmap;
                    if (_ribbonCardViews.TryGetValue(key, out var view))
                    {
                        view.Picture.Child = null;
                        view.Picture.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                    }
                }, DispatcherPriority.Background);
            }
        }, token);
    }

    /// <summary>One preview, drawn like an export (CPU raster, no editor chrome), as RGBA premultiplied bytes.</summary>
    private static byte[] RenderPreviewPixels(MapDocument document, WorldBounds area, int width, int height, double scale,
        IStampImageSource? stamps)
    {
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
            _ = Task.Run(() =>
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
            }, token);
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
            if (_ribbonPreviews.TryGetValue(card.Key, out var preview)) thumb.Background = new ImageBrush(preview) { Stretch = Stretch.UniformToFill };
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
            SyncAppearanceRibbon();
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
        _ = window.ShowDialog(owner);
    }
}
