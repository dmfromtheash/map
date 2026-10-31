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
/// chosen one opens its strength; «Сравнить» held shows the map without style and light. The former ribbon stays, for
/// fine settings, behind «Тонко настроить».
/// </summary>
public sealed partial class EditorView
{
    private const int LookTileWidth = 72, LookTileHeight = 44;

    private Control? _lookPage;
    private readonly Dictionary<string, (Button Button, Border Picture)> _lookTiles = [];
    private readonly Dictionary<string, Bitmap> _lookPreviews = [];
    private readonly Dictionary<string, Control> _lookStrength = [];
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

        var fine = Ui.TextButton("LookFineTuneButton", "Настроить ▸", () => ShowLookFineTune(true), "compact")
            .WithTip("Тонко настроить: все настройки света, стиля и моря лентой под картой — эффекты стиля, огни, размах моря.");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(12, 8, 12, 6),
            Children = { new TextBlock { Text = "Вся карта", FontSize = 17, VerticalAlignment = VerticalAlignment.Center }.Classed("panel-title"), fine, compare },
        };
        Grid.SetColumn(fine, 1);
        Grid.SetColumn(compare, 2);

        Control Part(string group, string icon, string title, string? offKey, string? offTitle, IEnumerable<(string Key, string Title)> cards, int columns)
        {
            var state = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
            _lookState[group] = state;
            var head = new DockPanel { LastChildFill = false };
            head.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { MapIcons.Stroked(icon, 14), new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeight.SemiBold, LetterSpacing = .8 }.Classed("accent-text") },
            }.Docked(Dock.Left));
            if (offKey is not null)
            {
                var off = Ui.TextButton("LookOff_" + group, offTitle!, () => ApplyLook(offKey), "compact").WithTip(offTitle + " — один шаг отмены.");
                head.Children.Add(off.Docked(Dock.Right));
                _lookTiles[offKey] = (off, new Border());
            }
            else head.Children.Add(state.Docked(Dock.Right));
            var grid = new UniformGrid { Columns = columns, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (key, cardTitle) in cards) grid.Children.Add(LookTile(group, key, cardTitle));
            var strength = LookStrengthRow(group);
            strength.IsVisible = false;
            _lookStrength[group] = strength;
            return new Border
            {
                BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 9, 12, 10),
                Child = new StackPanel { Spacing = 7, Children = { head, grid, strength } },
            }.Named("LookPart_" + group);
        }

        var page = new StackPanel
        {
            Children =
            {
                header,
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
        _lookPage.DetachedFromVisualTree += (_, _) => _editor.PreviewLook(null);
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
        ToolTip.SetTip(button, title + ". Наведите — вся карта в этом виде; щелчок — применить (один шаг отмены); ещё щелчок — сила.");
        // Pointing tries the look on the whole map; leaving brings the map back.
        button.PointerEntered += (_, _) => _editor.PreviewLook(LookDocument(key));
        button.PointerExited += (_, _) => _editor.PreviewLook(null);
        button.Click += (_, _) =>
        {
            if (LookActive(key) && _lookStrength.TryGetValue(group, out var row)) { row.IsVisible = !row.IsVisible; return; }
            ApplyLook(key);
        };
        _lookTiles[key] = (button, picture);
        if (_lookPreviews.TryGetValue(key, out var ready)) picture.Background = new ImageBrush(ready) { Stretch = Stretch.UniformToFill };
        return button;
    }

    private void ApplyLook(string key)
    {
        _editor.PreviewLook(null);
        ApplyRibbonCard(key);
        SyncAppearanceSections();
    }

    /// <summary>The strength of the chosen look: time of day for the light, strength for the style and the sea.</summary>
    private Control LookStrengthRow(string group) => group switch
    {
        "light" => ValueRow("LookLightTimeSlider", "LookLightTimeBox", null, "Время суток", 0, 100, "%",
            () => _editor.ShownLighting.Enabled ? _editor.ShownLighting.TimeOfDay * 100 : null,
            (value, commit) => _editor.SetTimeOfDay(value / 100, commit), step: 5,
            description: "0 — день, 50 — вечер, 100 — ночь."),
        "style" => ValueRow("LookStyleStrengthSlider", "LookStyleStrengthBox", null, "Сила стиля", 0, 100, "%",
            () => _editor.ShownStyle.Enabled ? _editor.ShownStyle.Strength * 100 : null,
            (value, commit) => _editor.SetStyleStrength(value / 100, commit), step: 5, defaultValue: 100),
        _ => ValueRow("LookSeaStrengthSlider", "LookSeaStrengthBox", null, "Сила моря", 0, 100, "%",
            () => _editor.Sea is { } sea ? sea.Strength * 100 : null,
            (value, commit) => { if (commit) _editor.SetSeaStrength(value); }, step: 5, defaultValue: 100),
    };

    private MapDocument LookDocument(string key) => CardDocument(_editor.Session.Document, key);

    private bool LookActive(string key)
    {
        var style = _editor.ShownStyle;
        var lighting = _editor.ShownLighting;
        return key switch
        {
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
        var accent = Application.Current?.FindResource("MapAccentBrush") as IBrush;
        var line = Application.Current?.FindResource("MapBorderBrush") as IBrush;
        foreach (var (key, (button, picture)) in _lookTiles)
        {
            var on = LookActive(key);
            Ui.SetClass(button, "active", on);
            picture.BorderBrush = on ? accent : line;
            picture.BorderThickness = new Thickness(on ? 2.5 : 1.5);
        }
        if (_lookState.TryGetValue("light", out var light))
            light.Text = _editor.ShownLighting.Enabled ? TimeTitle(_editor.ShownLighting.TimeOfDay) : "Без света";
        if (_rightTab == RightColumnTab.Look) QueueLookPreviews();
    }

    private void QueueLookPreviews()
    {
        _lookPreviewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _lookPreviewTimer!.Stop();
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
        const int width = CardWidth * 2, height = CardHeight * 2;
        var (styleArea, styleScale) = PreviewFrame(width, height, CardScale * 2);
        var seaScale = CardScale * 0.8;
        var seaArea = PreviewArea(width, height, seaScale);
        var signature = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(document)}|{styleArea.Left:0}|{styleArea.Top:0}|{styleScale:0.000}";
        if (signature == _lookPreviewSignature) return;
        _lookPreviewSignature = signature;
        _lookPreviewWork?.Cancel();
        var work = _lookPreviewWork = new CancellationTokenSource();
        var token = work.Token;
        var stamps = _editor.StampImages;
        var jobs = _lookTiles.Keys.Where(k => k is not ("none" or "sea-none")).Select(k => (Key: k, Document: CardDocument(document, k),
            Sea: k.StartsWith("sea-", StringComparison.Ordinal))).ToArray();
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
                    if (_lookTiles.TryGetValue(key, out var tile)) tile.Picture.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                }, DispatcherPriority.Background);
            }
        }, token));
    }

    /// <summary>«Тонко настроить»: the former ribbon under the map with every setting of light, style and sea.</summary>
    private void ShowLookFineTune(bool on)
    {
        _ribbonFineTune = on;
        if (on && _ribbonTab == RibbonTab.None) _ribbonTab = RibbonTab.Styles;
        if (on && _workspace != EditorWorkspace.Appearance) ShowWorkspace(EditorWorkspace.Appearance);
        SyncAppearanceRibbon();
    }

    private void DisposeLookPage()
    {
        _lookPreviewTimer?.Stop();
        _lookPreviewWork?.Cancel();
        foreach (var bitmap in _lookPreviews.Values) bitmap.Dispose();
        _lookPreviews.Clear();
    }
}
