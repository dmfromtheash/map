using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// «Вид» - the look of the whole map in one page of the right column (owner's pick 2026-10-31, variant 1 of
/// https://claude.ai/artifact/UA3ciWpYB3UVgFbX56uqNo, after a survey of about a hundred programs): three signed parts,
/// Свет (four), Стиль (sixteen, four by four, no scrolling) and Море (five), each a picture of this very map drawn by the
/// program. Pointing at a picture shows the whole map in it; a click applies it (one undo step); a second click on the
/// chosen one, or «Настроить» of the part, opens all its settings right under its pictures; «Сравнить» held shows the map
/// without style and light. This is the only place of these settings: the ribbon under the map that repeated the same
/// cards was removed (owner, 2026-10-31: «у нас дублируются функции на экране… свет и внизу и сбоку»). The page is the
/// section «Оформление»; the right column has no tab of its own for it.
/// </summary>
public sealed partial class EditorView
{
    private const int LookTileWidth = 72, LookTileHeight = 44;

    private Control? _lookPage;
    private readonly Dictionary<string, (Button Button, Border Picture)> _lookTiles = [];
    private readonly Dictionary<string, Bitmap> _lookPreviews = [];
    private readonly Dictionary<LookPart, RetainedSlot> _lookFine = [];
    private readonly Dictionary<LookPart, Button> _lookFineButtons = [];
    private LookPart? _lookFineOpen;
    private readonly Dictionary<string, TextBlock> _lookState = [];
    private DispatcherTimer? _lookPreviewTimer;
    private CancellationTokenSource? _lookPreviewWork;
    private string? _lookPreviewSignature;

    private Control BuildLookPage()
    {
        if (_lookPage is not null) return _lookPage;
        var compare = Ui.TextButton("LookCompareButton", "Сравнить", () => { }, "compact", "accent");
        compare.WithTip("Держите — карта без стиля и света, как нарисована. Отпустите — снова выбранный вид.");
        compare.AddHandler(PointerPressedEvent, (_, _) => _editor.PreviewLook(_editor.LookWithoutEffects()), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        compare.PointerReleased += (_, _) => _editor.PreviewLook(null);
        compare.PointerCaptureLost += (_, _) => _editor.PreviewLook(null);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Margin = new Thickness(12, 8, 12, 6),
            Children = { new TextBlock { Text = "Вся карта", FontSize = 17, VerticalAlignment = VerticalAlignment.Center }.Classed("panel-title"), compare },
        };
        Grid.SetColumn(compare, 1);

        Control Part(string group, string icon, string title, string? offKey, string? offTitle, IEnumerable<(string Key, string Title)> cards, int columns)
        {
            var part = PartOf(group);
            var state = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
            _lookState[group] = state;
            var head = new DockPanel { LastChildFill = false };
            head.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { MapIcons.Stroked(icon, 14), new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeight.SemiBold, LetterSpacing = .8 }.Classed("accent-text") },
            }.Docked(Dock.Left));
            // Every setting of this part opens right under its pictures: one place for the light, one for the style,
            // one for the sea (owner, 2026-10-31: no second copy of the same cards under the map).
            var fine = Ui.TextButton("LookFine_" + group, "Настроить", () => ShowLookPart(part, fine: _lookFineOpen != part), "compact")
                .WithTip("Все настройки: " + title.ToLowerInvariant() + (group switch
                {
                    "light" => " — время суток, объём, тени, туман, огни и цвет.",
                    "style" => " — сила, эффекты и большой осмотр стиля.",
                    _ => " — сила и размах от берега.",
                }));
            _lookFineButtons[part] = fine;
            head.Children.Add(fine.Docked(Dock.Right));
            if (offKey is not null)
            {
                var off = Ui.TextButton("LookOff_" + group, offTitle!, () => ApplyLook(offKey), "compact").WithTip(offTitle + " — один шаг отмены.");
                off.Margin = new Thickness(0, 0, 6, 0);
                head.Children.Add(off.Docked(Dock.Right));
                _lookTiles[offKey] = (off, new Border());
            }
            else { state.Margin = new Thickness(0, 0, 8, 0); head.Children.Add(state.Docked(Dock.Right)); }
            var grid = new UniformGrid { Columns = columns, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (key, cardTitle) in cards) grid.Children.Add(LookTile(group, key, cardTitle));
            var settings = new RetainedSlot { Name = "LookFineBody_" + group, IsVisible = false };
            _lookFine[part] = settings;
            return new Border
            {
                BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 9, 12, 10),
                Child = new StackPanel { Spacing = 7, Children = { head, grid, settings } },
            }.Named("LookPart_" + group);
        }

        var page = new StackPanel
        {
            Children =
            {
                header,
                BuildVariantPart(),
                BuildPackPart(),
                Part("light", MapIcons.LineSun, "Свет", null, null, LightCards.Select(c => (c.Key, c.Title)), 4),
                Part("style", MapIcons.LinePalette, "Стиль", "none", "Без стиля", StyleCards.Where(c => c.Preset is not null).Select(c => (c.Key, c.Title)), 4),
                Part("sea", MapIcons.LineWater, "Море", "sea-none", "Без моря", SeaCards.Where(c => c.Manner is not null).Select(c => (c.Key, c.Title)), 5),
            },
        };
        _lookPage = new ScrollViewer
        {
            Name = "LookPage", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = page,
        }.Classed("panel");
        _appearanceSyncs.Add(SyncLookPage);
        _lookPage.DetachedFromVisualTree += (_, _) => { _lookHoverTimer?.Stop(); _editor.PreviewLook(null); };
        return _lookPage;
    }

    private Button LookTile(string group, string key, string title)
    {
        var width = group == "sea" ? 56 : LookTileWidth;
        var picture = new Border
        {
            Width = width, Height = group == "sea" ? 36 : LookTileHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            BorderThickness = new Thickness(1.5), BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        };
        // Two lines of a fixed height for every name, so the rows keep one size whatever the name.
        var caption = new TextBlock
        {
            Text = title, FontSize = 10.5, LineHeight = 12.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Width = width, Height = 26,
        }.Classed("faint");
        var button = new Button
        {
            Name = "LookCard_" + key, Tag = key, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 3),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
            Content = new StackPanel { Spacing = 3, Children = { picture, caption } },
        };
        AutomationProperties.SetName(button, title);
        // What the style or the sea is, as the ribbon's cards said it, then how the picture works.
        var what = key.StartsWith("style-", StringComparison.Ordinal) ? " " + PresetHint(Enum.Parse<MapStylePreset>(key["style-".Length..]))
            : key.StartsWith("sea-", StringComparison.Ordinal) ? " " + SeaHint((SeaManner)int.Parse(key["sea-".Length..], System.Globalization.CultureInfo.InvariantCulture))
            : "";
        ToolTip.SetTip(button, title + "." + what + (key.StartsWith("style-", StringComparison.Ordinal)
            ? " Щелчок — вся карта в этом стиле (Ctrl+Z вернёт); ещё щелчок — настройки."
            : " Задержите указатель — вся карта в этом виде; щелчок — применить (один шаг отмены); ещё щелчок — настройки."));
        // Pointing tries the look on the whole map; leaving brings the map back - both after a short rest (HoverLook).
        button.PointerEntered += (_, _) => HoverLook(key);
        button.PointerExited += (_, _) => HoverLook(null);
        button.Click += (_, _) =>
        {
            // A second click on the chosen picture opens (or closes) its settings under the part.
            if (LookActive(key)) { var part = PartOf(group); ShowLookPart(part, fine: _lookFineOpen != part); return; }
            ApplyLook(key);
        };
        _lookTiles[key] = (button, picture);
        if (_lookPreviews.TryGetValue(key, out var ready)) picture.Background = new ImageBrush(ready) { Stretch = Stretch.UniformToFill };
        return button;
    }

    private DispatcherTimer? _lookHoverTimer;
    private string? _lookHoverKey;

    /// <summary>
    /// The trial look follows the pointer only once it rests (owner, 2026-10-31: «оно жесть как лагает»). A style shown
    /// on the whole new-art map took 3,3 s to draw and 1,8 s to take back, so a pointer crossing the pictures queued a
    /// redraw per picture (12 pictures - the map still 3,3 s later). Now the map changes after 300 ms on one picture; a
    /// move from picture to picture never draws the plain map in between (it waits 150 ms for the next picture).
    /// </summary>
    private void HoverLook(string? key)
    {
        // A style is not tried on the whole map: one style frame costs 0,6-1 s even on a map of 40 mountains, built 2-3
        // times on the way to the finished view, and the first time a style is used up to 16 s (paint-perf
        // MAP_NEW_ART_STYLE, 2026-10-31). Its picture shows it; a click applies it. Light and sea (40-120 ms) are tried.
        if (key is not null && (key == "none" || key.StartsWith("style-", StringComparison.Ordinal) || key.StartsWith(PackPrefix, StringComparison.Ordinal)
            || key.StartsWith(VariantPrefix, StringComparison.Ordinal))) key = null;
        _lookHoverKey = key;
        _lookHoverTimer ??= new DispatcherTimer(DispatcherPriority.Input);
        _lookHoverTimer.Stop();
        _lookHoverTimer.Interval = TimeSpan.FromMilliseconds(key is null ? 150 : 300);
        _lookHoverTimer.Tick -= OnLookHoverRested;
        _lookHoverTimer.Tick += OnLookHoverRested;
        _lookHoverTimer.Start();
    }

    private void OnLookHoverRested(object? sender, EventArgs e)
    {
        _lookHoverTimer?.Stop();
        _editor.PreviewLook(_lookHoverKey is { } key ? LookDocument(key) : null);
    }

    private void ApplyLook(string key)
    {
        _lookHoverTimer?.Stop();
        _lookHoverKey = null;
        _editor.PreviewLook(null);
        ApplyLookCard(key);
        SyncAppearanceSections();
        SyncLookParts();
    }

    private static LookPart PartOf(string group) => group switch { "style" => LookPart.Styles, "sea" => LookPart.Sea, _ => LookPart.Light };

    /// <summary>
    /// Opens «Оформление» with <paramref name="part"/> in sight; with <paramref name="fine"/> its settings open under its
    /// pictures (one part at a time), without it they close.
    /// </summary>
    private void ShowLookPart(LookPart part, bool fine)
    {
        if (_workspace != EditorWorkspace.Appearance) ShowWorkspace(EditorWorkspace.Appearance);
        else if (_rightTab != RightColumnTab.Look) SetRightTab(RightColumnTab.Look);
        _lookFineOpen = fine ? part : null;
        foreach (var (candidate, slot) in _lookFine)
        {
            var open = candidate == _lookFineOpen;
            if (open && slot.Content is null) slot.Content = candidate switch
            {
                LookPart.Styles => new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Ui.CommandButton("RibbonInspectButton", "Осмотреть…", MapIcons.FitCanvas, OpenStyleInspector, "compact")
                            .WithTip("Большой просмотр стиля на вашей карте: 50, 100 и 200 %, другие стили рядом."),
                        StyleBody(),
                    },
                },
                LookPart.Sea => SeaBody(),
                _ => LightBody(),
            };
            slot.IsVisible = open;
            if (_lookFineButtons.TryGetValue(candidate, out var button)) Ui.SetClass(button, "active", open);
        }
        SyncLookParts();
        var target = _lookPage is null ? null : Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_lookPage).OfType<Control>()
            .FirstOrDefault(c => c.Name == "LookPart_" + (part switch { LookPart.Styles => "style", LookPart.Sea => "sea", _ => "light" }));
        if (target is not null) Dispatcher.UIThread.Post(() => target.BringIntoView(), DispatcherPriority.Loaded);
    }

    private MapDocument LookDocument(string key) => CardDocument(_editor.Session.Document, key);

    private bool LookActive(string key)
    {
        var style = _editor.ShownStyle;
        var lighting = _editor.ShownLighting;
        return key switch
        {
            _ when key.StartsWith(PackPrefix, StringComparison.Ordinal) => _editor.ActiveStylePackId == key[PackPrefix.Length..],
            _ when key.StartsWith(VariantPrefix, StringComparison.Ordinal) => _editor.Session.Document.Variants.Active == VariantOf(key),
            "none" => !style.Enabled,
            "light-off" => !lighting.Enabled,
            _ when key.StartsWith("sea-", StringComparison.Ordinal) => SeaCardActive(key),
            _ when key.StartsWith("light-", StringComparison.Ordinal) => lighting.Enabled
                && LightCards.First(c => c.Key == key).Time is { } t && Math.Abs(lighting.TimeOfDay - t) < 0.02,
            _ => style.Enabled && "style-" + style.Preset == key,
        };
    }

    /// <summary>Marks the chosen picture of each part and names it; draws the pictures when the map changed.</summary>
    private void SyncLookPage()
    {
        if (_lookPage is null) return;
        SyncVariantPart();
        var accent = Application.Current?.FindResource("MapAccentBrush") as IBrush;
        var line = Application.Current?.FindResource("MapBorderBrush") as IBrush;
        foreach (var (key, (button, picture)) in _lookTiles)
        {
            var on = LookActive(key);
            Ui.SetClass(button, "active", on);
            picture.BorderBrush = on ? accent : line;
            picture.BorderThickness = new Thickness(on ? 2.5 : 1.5);
        }
        SyncPackState();
        if (_lookState.TryGetValue("light", out var light))
            light.Text = _editor.ShownLighting.Enabled ? TimeTitle(_editor.ShownLighting.TimeOfDay) : "Без света";
        if (_rightTab == RightColumnTab.Look) QueueLookPreviews();
    }

    private CancellationTokenSource? _styleAssetWork;
    private MapDocument? _styleAssetsFor;

    /// <summary>
    /// What the first frame of a style needs per mountain (form and height maps) is made in the background as soon as
    /// «Вид» opens, not on the click: the first «Акварель» froze the map for 12,8 s, «Атлас Блау» for 4,5 s.
    /// </summary>
    private void PrepareStyleAssets()
    {
        var document = _editor.Session.Document;
        // The maps belong to the pictures of the mountains, so a new document matters only when its mountains differ;
        // asking again for a document already prepared costs one pass over its stamps.
        if (ReferenceEquals(_styleAssetsFor, document) || Environment.GetEnvironmentVariable("MAP_STYLE_PREPARE") == "0") return;
        _styleAssetsFor = document;
        _styleAssetWork?.Cancel();
        var work = _styleAssetWork = new CancellationTokenSource();
        var token = work.Token;
        var stamps = _editor.StampImages;
        UiTasks.Forget(this, () => Task.Run(() => Map.Rendering.Styles.StyleRenderer.PrepareAssets(document, stamps, token), token));
    }

    private void QueueLookPreviews()
    {
        // The map changed (a style or a light was chosen): the pictures still being drawn for the old map stop now, so
        // the map's new frame has the cores; they go on, for what changed only, once that frame is finished.
        var pause = Environment.GetEnvironmentVariable("MAP_LOOK_PREVIEW_PAUSE") != "0"; // "0": the old way, for the A/B only
        if (pause && !ReferenceEquals(_lookPreviewFor, _editor.Session.Document))
        {
            _lookPreviewWork?.Cancel();
            _lookPreviewSignature = null;
        }
        _lookPreviewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _lookPreviewTimer!.Stop();
            // The pictures wait for the map: drawn while the map's own frame of a just chosen style was built, they
            // took the cores it needed (11-12 style frames during the first «Акварель», 5 s on DM; R09 А2 step 5).
            if (Environment.GetEnvironmentVariable("MAP_LOOK_PREVIEW_PAUSE") != "0" && !_canvas.FrameSettled) { _lookPreviewTimer.Start(); return; }
            StartLookPreviews();
        });
        _lookPreviewTimer.Stop();
        _lookPreviewTimer.Start();
    }

    /// <summary>Every picture of the page: this map in that light, style or sea, drawn by the program off the UI thread.</summary>
    private void StartLookPreviews()
    {
        if (_lookPage is null || _canvas.Bounds.Width < 1) return;
        var document = _editor.Session.Document;
        // Drawn at twice the picture's own size (72×44 DIP), not the former ribbon card's 150×84: a quarter of the
        // pixels (the 25 pictures of the new-art map took 32 s of a core and were redrawn after every edit). The same
        // piece of the map is shown: the scale shrinks with the size.
        const int width = LookTileWidth * 2, height = LookTileHeight * 2;
        const double shrink = width / (CardWidth * 2.0);
        var (styleArea, styleScale) = PreviewFrame(width, height, CardScale * 2 * shrink);
        var seaScale = CardScale * 0.8 * shrink;
        var seaArea = PreviewArea(width, height, seaScale);
        var signature = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(document)}|{styleArea.Left:0}|{styleArea.Top:0}|{styleScale:0.000}";
        if (signature == _lookPreviewSignature) return;
        // Another piece of the map or another scale: every picture is drawn again; otherwise only the pictures whose
        // own map changed (a style click leaves the pictures of the other styles as they are).
        var area = signature[(signature.IndexOf('|') + 1)..];
        if (_lookPreviewArea != area) { _lookPreviewArea = area; _lookPreviewDocuments.Clear(); }
        _lookPreviewSignature = signature;
        _lookPreviewFor = document;
        _lookPreviewWork?.Cancel();
        var work = _lookPreviewWork = new CancellationTokenSource();
        var token = work.Token;
        var stamps = _editor.StampImages;
        var jobs = _lookTiles.Keys.Where(k => k is not ("none" or "sea-none")).Select(k => (Key: k, Document: CardDocument(document, k),
            Sea: k.StartsWith("sea-", StringComparison.Ordinal)))
            .Where(job => !(_lookPreviews.ContainsKey(job.Key) && _lookPreviewDocuments.TryGetValue(job.Key, out var drawn) && SameCard(drawn, job.Document)))
            .ToArray();
        UiTasks.Forget(this, () => Task.Run(() =>
        {
            foreach (var (key, cardDocument, sea) in jobs)
            {
                if (token.IsCancellationRequested) return;
                byte[] pixels;
                try { pixels = RenderPreviewPixels(cardDocument, sea ? seaArea : styleArea, width, height, sea ? seaScale : styleScale, stamps); }
                catch (Exception) when (!token.IsCancellationRequested) { continue; }
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    var bitmap = ToBitmap(pixels, width, height);
                    if (_lookPreviews.Remove(key, out var old)) old.Dispose();
                    _lookPreviews[key] = bitmap;
                    _lookPreviewDocuments[key] = cardDocument;
                    if (_lookTiles.TryGetValue(key, out var tile)) tile.Picture.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                }, DispatcherPriority.Background);
            }
        }, token));
    }

    private readonly Dictionary<string, MapDocument> _lookPreviewDocuments = [];
    private string? _lookPreviewArea;
    private MapDocument? _lookPreviewFor;

    /// <summary>Two maps of a picture draw the same: the same layers, style, light and page.</summary>
    private static bool SameCard(MapDocument drawn, MapDocument wanted) =>
        ReferenceEquals(drawn.Layers, wanted.Layers) && Equals(drawn.Style, wanted.Style)
        && Equals(drawn.Lighting, wanted.Lighting) && drawn.Canvas == wanted.Canvas && drawn.Timeline == wanted.Timeline;

    private void DisposeLookPage()
    {
        _lookPreviewTimer?.Stop();
        _lookHoverTimer?.Stop();
        _lookPreviewWork?.Cancel();
        _styleAssetWork?.Cancel();
        foreach (var bitmap in _lookPreviews.Values) bitmap.Dispose();
        _lookPreviews.Clear();
        _lookPreviewDocuments.Clear();
    }
}
