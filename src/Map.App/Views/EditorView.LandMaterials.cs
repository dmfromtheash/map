using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control? _landShapeControls, _landPaintControls, _landShapeAdvanced, _landCliffMaskControls;
    private IReadOnlyList<Button>? _landModeButtons;
    private Window? _materialCatalogWindow;
    private readonly Dictionary<string, Bitmap> _materialPreviewImages = [];

    private Control BuildLandModePicker()
    {
        var (row, buttons) = Ui.Segmented("LandToolMode",
            [("Shape", "Форма земли", null), ("Paint", "Раскрасить", null), ("Cliffs", "Маска обрыва", null)],
            key =>
            {
                if (key == "Cliffs") _editor.SetLandCliffMaskMode(true);
                else { _editor.SetLandPaintMode(key == "Paint"); _editor.SetLandCliffMaskMode(false); }
                SyncLandMaterialPanel();
            });
        ToolTip.SetTip(buttons[2], "Кисть по берегу поднятой земли: где обрыв обязан стоять, а где вместо него пологий пляж.");
        _landModeButtons = buttons;
        return row;
    }

    private void SyncLandMaterialPanel()
    {
        var paint = _editor.LandPaintMode;
        var cliffs = _editor.LandCliffMaskMode;
        var mode = cliffs ? 2 : paint ? 1 : 0;
        if (_landShapeControls is not null) _landShapeControls.IsVisible = !paint;
        if (_landShapeAdvanced is not null) _landShapeAdvanced.IsVisible = !paint;
        if (_landPaintControls is not null) _landPaintControls.IsVisible = paint && !cliffs;
        if (_landCliffMaskControls is not null) _landCliffMaskControls.IsVisible = cliffs;
        if (_landModeButtons is not null)
            for (var i = 0; i < _landModeButtons.Count; i++)
            {
                Ui.SetClass(_landModeButtons[i], "active", i == mode);
                if (_landModeButtons[i] is ToggleButton toggle) toggle.IsChecked = i == mode;
            }
    }

    /// <summary>
    /// R12: the «маска обрыва» brush. Three actions - wall, beach, erase - the brush size and
    /// softness, and one button that clears the whole mask of the active land.
    /// </summary>
    private Control BuildLandCliffMaskPanel()
    {
        var (actions, buttons) = Ui.Segmented("LandCliffMaskAction",
            [(nameof(Map.Presentation.ViewModels.CliffMaskAction.Wall), "Здесь стена", null),
             (nameof(Map.Presentation.ViewModels.CliffMaskAction.Beach), "Здесь пляж", null),
             (nameof(Map.Presentation.ViewModels.CliffMaskAction.Erase), "Стереть", null)],
            key => { _editor.SetLandCliffMaskAction(Enum.Parse<Map.Presentation.ViewModels.CliffMaskAction>(key)); SyncAppearanceSections(); });
        ToolTip.SetTip(buttons[0], "Обрыв стоит здесь, даже если в стиле на этом месте разрыв.");
        ToolTip.SetTip(buttons[1], "Здесь нет стены: земля спускается к морю или нижней земле пологим берегом.");
        ToolTip.SetTip(buttons[2], "Убрать маску: берег снова решает стиль обрыва.");
        _appearanceSyncs.Add(() =>
        {
            var index = (int)_editor.LandCliffMaskAction;
            for (var i = 0; i < buttons.Count; i++)
            {
                Ui.SetClass(buttons[i], "active", i == index);
                if (buttons[i] is ToggleButton toggle) toggle.IsChecked = i == index;
            }
        });
        return Ui.Category("LandCliffMaskCategory", "Маска обрыва", "accent-create", new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Ui.Hint("Проведите кистью по берегу поднятой земли. Кирпичный цвет — здесь обрыв, песочный — пляж. Стена меняется, когда отпускаете кнопку мыши."),
                actions,
                AppearanceSlider("LandCliffMaskSizeSlider", "Размер", 1, 256, "ед.", () => _editor.BrushSettings.Radius,
                    (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Radius = v }),
                AppearanceSlider("LandCliffMaskSoftnessSlider", "Мягкость", 0, 100, "%", () => _editor.BrushSettings.Softness * 100,
                    (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Softness = v / 100 }, 5),
                Ui.TextButton("ClearLandCliffMaskButton", "Очистить маску этой земли", () => { _editor.ClearCliffMask(); Canvas.InvalidateVisual(); }, "compact"),
            },
        });
    }

    private Control BuildLandPaintPanel()
    {
        var (actions, buttons) = Ui.Segmented("LandPaintAction",
            [("Texture", "Материал", null), ("Color", "Цвет", null), ("Erase", "Ластик", null)],
            key => { _editor.SetLandPaintAction(key); SyncAppearanceSections(); });
        buttons[0].IsEnabled = MapFillTextures.Of(MapFillTextureKind.Ground)
            .Any(texture => MapTextureLibrary.Image(texture.Id) is not null);
        // The brush lays the raw tile down as paint: no land colour takes part, so the sample
        // shows the material itself at the brush's own scale.
        MaterialPreviewContext BrushContext() => MaterialPreviewContext.RawTexture(_editor.LandMaterialScale);
        var sample = TextureSwatch(_editor.LandMaterialId, "", BrushContext());
        // This live preview has its own title; an initial empty-state label must not cover later materials.
        sample.Child = null;
        sample.Name = "LandMaterialPreview"; sample.Width = double.NaN; sample.Height = 80;
        var title = new TextBlock { Name = "LandMaterialTitle", TextWrapping = TextWrapping.Wrap }.Classed("role-panel");
        var choose = Ui.AsyncButton("LandMaterialCatalogButton", "Каталог материалов…",
            () => OpenMaterialCatalog(MapFillTextureKind.Ground, _editor.LandMaterialId,
                id => { if (id is not null) _editor.ChooseLandMaterial(id); }, BrushContext), "compact");
        var color = AppearanceColorBox("LandPaintColorBox", "Цвет кисти",
            () => _editor.BrushSettings.Color,
            value => _editor.BrushSettings = _editor.BrushSettings with { Color = value });
        var materials = new StackPanel { Spacing = 5, Children = { title, sample, choose } };
        var recent = new Ui.ChoiceGrid { Name = "LandMaterialRecents", MaxColumns = 6, MinimumItemWidth = 30, Gap = 4 };
        var recentLabel = new TextBlock { Text = "Недавние материалы" }.Classed("faint", "role-caption");
        string? lastMaterial = null, lastRecents = null;
        _appearanceSyncs.Add(() =>
        {
            SyncLandMaterialPanel();
            var index = _editor.LandPaintEraser ? 2 : _editor.LandPaintWithColor ? 1 : 0;
            for (var i = 0; i < buttons.Count; i++)
            {
                Ui.SetClass(buttons[i], "active", i == index);
                if (buttons[i] is ToggleButton toggle) toggle.IsChecked = i == index;
            }
            materials.IsVisible = index == 0; color.IsVisible = index == 1;
            recent.IsVisible = recentLabel.IsVisible = index == 0 && _editor.RecentLandMaterials.Count > 0;
            var brushContext = BrushContext();
            var materialSignature = _editor.LandMaterialId + "|" + brushContext.Signature;
            if (lastMaterial != materialSignature)
            {
                lastMaterial = materialSignature;
                title.Text = MapFillTextures.Find(_editor.LandMaterialId)?.Title ?? "Материалы пока не добавлены";
                sample.Background = MaterialPreviewBrush(_editor.LandMaterialId, brushContext);
            }
            var signature = string.Join("|", _editor.RecentLandMaterials) + "|" + brushContext.Signature;
            if (signature == lastRecents) return;
            lastRecents = signature; recent.Children.Clear();
            foreach (var id in _editor.RecentLandMaterials)
            {
                var swatch = TextureSwatch(id, "", brushContext); swatch.Width = 30; swatch.Height = 28;
                var button = new Button { Name = "LandRecent_" + id, Content = swatch, Padding = new Thickness(2) }.Classed("swatch");
                AutomationProperties.SetName(button, MapFillTextures.Find(id)?.Title ?? id);
                ToolTip.SetTip(button, MapFillTextures.Find(id)?.Title ?? id);
                button.Click += (_, _) => { _editor.ChooseLandMaterial(id); SyncAppearanceSections(); };
                recent.Children.Add(button);
            }
        });
        return Ui.Category("LandMaterialBrushCategory", "Кисть", "accent-create", new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Ui.Hint("Раскрашивайте сушу разными материалами. Мягкая кисть смешивает покрытия; ластик возвращает основу."),
                actions, materials, color,
                AppearanceSlider("LandPaintSizeSlider", "Размер", 1, 256, "ед.", () => _editor.BrushSettings.Radius,
                    (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Radius = v }),
                AppearanceSlider("LandPaintOpacitySlider", "Непрозрачность", 5, 100, "%", () => _editor.BrushSettings.Opacity * 100,
                    (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Opacity = v / 100 }, 5),
                AppearanceSlider("LandPaintSoftnessSlider", "Мягкость", 0, 100, "%", () => _editor.BrushSettings.Softness * 100,
                    (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Softness = v / 100 }, 5),
                new Ui.ChoiceGrid
                {
                    MaxColumns = 3, MinimumItemWidth = 64, Children =
                    {
                        Ui.TextButton("LandSoftBrushButton", "Мягкая", () => _editor.ApplyLandBrushPreset("Soft"), "compact"),
                        Ui.TextButton("LandCrispBrushButton", "Чёткая", () => _editor.ApplyLandBrushPreset("Crisp"), "compact"),
                        Ui.TextButton("LandDryBrushButton", "Сухая", () => _editor.ApplyLandBrushPreset("Dry"), "compact"),
                    },
                },
                AppearanceSlider("LandMaterialScaleSlider", "Масштаб фактуры", 32, 2048, "ед.", () => _editor.LandMaterialScale,
                    (v, _) => _editor.LandMaterialScale = v),
                AppearanceSlider("LandMaterialRotationSlider", "Поворот фактуры", -180, 180, "°", () => _editor.LandMaterialRotation,
                    (v, _) => _editor.LandMaterialRotation = v),
                recentLabel, recent,
                Ui.Advanced("LandPaintAdvanced", new StackPanel
                {
                    Spacing = 6, Children =
                    {
                        AppearanceSlider("LandPaintSpacingSlider", "Шаг отпечатков", 5, 100, "%", () => _editor.BrushSettings.Spacing * 100,
                            (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Spacing = v / 100 }, 5),
                        AppearanceSlider("LandPaintScatterSlider", "Разброс", 0, 60, "%", () => _editor.BrushSettings.Scatter * 100,
                            (v, _) => _editor.BrushSettings = _editor.BrushSettings with { Scatter = v / 100 }, 5),
                        AppearanceToggle("LandPaintPressureSize", "Нажим меняет размер",
                            () => _editor.BrushSettings.PressureAffectsSize,
                            v => _editor.BrushSettings = _editor.BrushSettings with { PressureAffectsSize = v }),
                        AppearanceToggle("LandPaintPressureOpacity", "Нажим меняет прозрачность",
                            () => _editor.BrushSettings.PressureAffectsOpacity,
                            v => _editor.BrushSettings = _editor.BrushSettings with { PressureAffectsOpacity = v }),
                        Ui.TextButton("LandClearPaintButton", "Убрать раскраску слоя", () => _editor.ClearLandPaint(), "compact"),
                        Ui.Hint("Основной цвет, материал всей земли и берег — в разделе «Оформление»."),
                    },
                }),
            },
        });
    }

    /// <summary>
    /// What a material is mixed with where it is about to be used: the land's fill colour and
    /// the strength of the shared material, the water colour of a river, the surface colour of a
    /// road - or nothing at all for the material brush, which lays the raw tile down as paint.
    /// The swatch is drawn by the very same call the renderer uses, so it predicts the map.
    /// </summary>
    internal readonly record struct MaterialPreviewContext(string? BaseColor, double Opacity, double Scale, bool Raw)
    {
        public static MaterialPreviewContext RawTexture(double scale) => new(null, 1, scale, true);

        public static MaterialPreviewContext Over(string? baseColor, double? opacity, double? scale) =>
            new(AppearanceColor.Normalize(baseColor) ?? baseColor, opacity ?? 1, scale ?? MapFillTextures.DefaultGroundScale, false);

        public string Signature => Raw ? $"raw|{Scale:0}" : $"{BaseColor}|{Opacity:0.00}|{Scale:0}";
    }

    private const int MaterialPreviewWidth = 256;
    private const int MaterialPreviewHeight = 160;

    /// <summary>
    /// A material as it will look in its context: the base colour first, then the material by
    /// <see cref="MapTextureLibrary.DrawFill"/> at the context's strength and world scale, at a
    /// zoom of 100 %. One bitmap per material and context, bounded.
    /// </summary>
    private IBrush? MaterialPreviewBrush(string? id, MaterialPreviewContext context)
    {
        if (id is null || MapTextureLibrary.Image(id) is null) return null;
        var key = id + "|" + context.Signature;
        if (!_materialPreviewImages.TryGetValue(key, out var bitmap))
        {
            if (_materialPreviewImages.Count >= 96)
            {
                foreach (var stale in _materialPreviewImages.Values) stale.Dispose();
                _materialPreviewImages.Clear();
            }
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(
                MaterialPreviewWidth, MaterialPreviewHeight, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            var area = new SkiaSharp.SKRect(0, 0, MaterialPreviewWidth, MaterialPreviewHeight);
            if (!context.Raw && context.BaseColor is { } baseColor)
            {
                using var fill = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColor.TryParse(baseColor, out var parsed) ? parsed : SkiaSharp.SKColors.Transparent };
                canvas.DrawRect(area, fill);
            }
            MapTextureLibrary.DrawFill(canvas, id, context.Scale, context.Raw ? 1 : context.Opacity, area);
            // The pixels go to Avalonia as they are: a PNG round trip per swatch was more than
            // half of the swatch cost, paid for every material picker while the editor opens.
            using var pixels = surface.PeekPixels();
            bitmap = new Bitmap(Avalonia.Platform.PixelFormats.Rgba8888, Avalonia.Platform.AlphaFormat.Premul,
                pixels.GetPixels(), new PixelSize(MaterialPreviewWidth, MaterialPreviewHeight), new Vector(96, 96), pixels.RowBytes);
            _materialPreviewImages.Add(key, bitmap);
        }
        return new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
    }

    private async Task OpenMaterialCatalog(MapFillTextureKind kind, string? current, Action<string?> choose,
        Func<MaterialPreviewContext> context)
    {
        if (_materialCatalogWindow is not null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var all = MapFillTextures.Of(kind).Where(t => MapTextureLibrary.Image(t.Id) is not null).ToArray();
        var search = new TextBox { Name = "MaterialCatalogSearch", PlaceholderText = "Найти материал…" };
        var groups = new ListBox
        {
            Name = "MaterialCatalogGroups", ItemsSource = new[] { "Все материалы" }.Concat(all.Select(t => t.Group).Distinct()).ToArray(),
            SelectedIndex = 0,
        }.Classed("material-groups");
        var tiles = new WrapPanel { Name = "MaterialCatalogTiles", Orientation = Orientation.Horizontal };
        var empty = new TextBlock { Name = "MaterialCatalogEmpty", Text = all.Length == 0
            ? "Материалы пока не добавлены. Можно использовать обычную цветную заливку."
            : "Ничего не найдено. Измените название или выберите другую группу.", TextWrapping = TextWrapping.Wrap };
        var count = new TextBlock().Classed("faint", "role-caption");
        var window = new Window
        {
            Name = "MaterialCatalogWindow", Title = kind == MapFillTextureKind.Ground ? "Материалы Земли" : "Материалы воды",
            Width = 900, Height = 650, MinWidth = 680, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowActivated = owner.ShowActivated, ShowInTaskbar = false,
        };
        _materialCatalogWindow = window;
        void Populate()
        {
            tiles.Children.Clear();
            var query = (search.Text ?? "").Trim();
            var group = groups.SelectedItem as string;
            var found = all.Where(t => (group is null or "Все материалы" || t.Group == group)
                && (t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Group.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
            var preview = context();
            foreach (var material in found)
            {
                var image = TextureSwatch(material.Id, material.Title, preview); image.Width = 140; image.Height = 96;
                var label = new TextBlock { Text = material.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 140 };
                var card = new Button
                {
                    Name = "MaterialCatalog_" + material.Id, Width = 156, Height = 150,
                    Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6),
                    Content = new StackPanel { Spacing = 5, Children = { image, label } },
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                }.Classed("swatch");
                AutomationProperties.SetName(card, material.Title);
                Ui.SetClass(card, "active", material.Id == current);
                card.Click += (_, _) => { choose(material.Id); SyncAppearanceSections(); Canvas.InvalidateVisual(); window.Close(); };
                tiles.Children.Add(card);
            }
            empty.IsVisible = found.Length == 0;
            count.Text = all.Length == 0 ? "Материалов: 0" : $"Материалов: {found.Length} · Авторские покрытия Map · " + (preview.Raw
                ? "образцы показаны как кисть кладёт материал"
                : kind == MapFillTextureKind.Ground ? "образцы с действующим цветом и силой материала" : "образцы с действующим цветом воды");
        }
        search.TextChanged += (_, _) => Populate();
        groups.SelectionChanged += (_, _) => Populate();
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), RowDefinitions = new RowDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 12 };
        var heading = new TextBlock { Text = window.Title, FontSize = 22 };
        Grid.SetColumnSpan(heading, 2); content.Children.Add(heading);
        Grid.SetRow(groups, 1); content.Children.Add(groups);
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12 };
        main.Children.Add(search);
        var scroll = new ScrollViewer { Content = new StackPanel { Children = { empty, tiles } }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); main.Children.Add(scroll);
        Grid.SetColumn(main, 1); Grid.SetRow(main, 1); content.Children.Add(main);
        Grid.SetRow(count, 2); Grid.SetColumn(count, 1); content.Children.Add(count);
        var close = Ui.TextButton("CloseMaterialCatalogButton", "Закрыть", () => window.Close(), "compact");
        Grid.SetRow(close, 2); content.Children.Add(close);
        window.Content = new Border { Padding = new Thickness(20), Child = content };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        window.Closed += (_, _) => _materialCatalogWindow = null;
        Populate();
        await window.ShowDialog(owner);
    }
}
