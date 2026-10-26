using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Map.Core.Documents;

namespace Map.App.Views;

public sealed partial class EditorView
{
    /// <summary>
    /// The cliffs of a raised piece of land. While the land has no tier the section is one line
    /// saying how to get one and a button that raises the land by a level; once the land stands
    /// above the ground or sea beneath it, the ready looks (after the owner's tier sheets) and every
    /// setting of the wall appear: its shape, its faces, colour and wash, hatching and dots, the ink
    /// and the foot, and the shadow below it. The everyday settings are open; the rest sit in
    /// collapsed groups.
    /// </summary>
    private Control BuildLandCliffSection(Func<LandEdgeStyle?> style, Action<Func<LandEdgeStyle, LandEdgeStyle>> set)
    {
        var defaults = new LandCliffStyle();
        LandCliffStyle? Cliff() => style()?.Cliff;
        void SetCliff(Func<LandCliffStyle, LandCliffStyle> change) => set(current => current with { Cliff = change(current.Cliff) });

        Control Slider(string name, string label, double minimum, double maximum, string unit, double step,
            Func<LandCliffStyle, double> read, Func<LandCliffStyle, double, LandCliffStyle> write, double scale, string description) =>
            AppearanceSlider(name, label, minimum, maximum, unit,
                () => Cliff() is { } cliff ? read(cliff) * scale : null,
                (v, _) => SetCliff(cliff => write(cliff, v / scale)), step,
                defaultValue: read(defaults) * scale, description: description);

        Control Percent(string name, string label, Func<LandCliffStyle, double> read, Func<LandCliffStyle, double, LandCliffStyle> write,
            string description, double minimum = 0) =>
            Slider(name, label, minimum, 100, "%", 5, read, write, 100, description);

        Control Colour(string name, string label, string description, Func<LandCliffStyle, string> read, Func<LandCliffStyle, string, LandCliffStyle> write)
        {
            var field = AppearanceColorBox(name, label, () => Cliff() is { } cliff ? read(cliff) : null, value =>
            {
                if (AppearanceColor.Normalize(value) is { } color) SetCliff(cliff => write(cliff, color));
            });
            ToolTip.SetTip(field, description);
            return field;
        }

        // Ready looks: the whole cliff, and the rim line the look is drawn with.
        var looks = new UniformGrid { Columns = 2 }.Named("LandCliffLooks");
        foreach (var look in LandCliffStyle.Looks)
        {
            var button = new Button
            {
                Name = "LandCliffLook_" + look.Id, Content = look.Title,
                HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 4),
            };
            ToolTip.SetTip(button, look.Description);
            AutomationProperties.SetName(button, look.Title);
            var chosen = look;
            button.Click += (_, _) =>
            {
                set(current => current with
                {
                    Cliff = chosen.Style,
                    OutlineColor = chosen.Rim ?? current.OutlineColor,
                    OutlineWidth = chosen.RimWidth ?? current.OutlineWidth,
                });
                SyncAppearanceSections(); Canvas.InvalidateVisual();
            };
            looks.Children.Add(button);
        }

        var (footRow, footButtons) = Ui.Segmented("LandCliffFoot_",
            [(nameof(CliffFoot.Ink), "Линия", null), (nameof(CliffFoot.Fade), "Растворение", null), (nameof(CliffFoot.Plain), "Без линии", null)],
            key => { SetCliff(cliff => cliff with { Foot = Enum.Parse<CliffFoot>(key) }); SyncAppearanceSections(); });
        _appearanceSyncs.Add(() =>
        {
            var foot = Cliff()?.Foot;
            foreach (var button in footButtons.OfType<ToggleButton>())
                button.IsChecked = foot is { } current && button.Name == "LandCliffFoot_" + current;
        });

        Control Group(string name, string title, bool open, params Control[] children)
        {
            var body = new StackPanel { Spacing = 4 };
            foreach (var child in children) body.Children.Add(child);
            if (open) return Ui.Subgroup(title, body);
            var expander = new Expander
            {
                Header = new TextBlock { Text = title }.Classed("role-panel"),
                Content = body, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            }.Named(name);
            AutomationProperties.SetName(expander, title);
            return expander;
        }

        var settings = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.Subgroup("Готовый вид", looks),
                Group("LandCliffShape", "Форма", true,
                    Slider("LandCliffHeightSlider", "Высота яруса", 0, LandCliffStyle.MaximumHeight, "ед.", 1,
                        c => c.Height, (c, v) => c with { Height = v }, 1,
                        "Высота стены на один уровень разницы. Земля на два уровня выше моря получает стену из двух уступов. 0 выключает обрывы."),
                    Percent("LandCliffVariationSlider", "Перепад высоты", c => c.HeightVariation, (c, v) => c with { HeightVariation = v },
                        "Насколько стена то выше, то ниже вдоль кромки: 0 — ровная, 100 — высокие выступы и провалы."),
                    Slider("LandCliffVariationLengthSlider", "Длина волны", 8, 512, "ед.", 2,
                        c => c.VariationLength, (c, v) => c with { VariationLength = v }, 1,
                        "Расстояние между выступами стены. Меньше — чаще, больше — длинные пологие волны."),
                    Percent("LandCliffGapsSlider", "Разрывы", c => c.Gaps, (c, v) => c with { Gaps = v },
                        "Доля кромки без стены, только линия берега: как обрывки уступов на эскизах."),
                    Slider("LandCliffGapLengthSlider", "Длина участков", 8, 1024, "ед.", 4,
                        c => c.GapLength, (c, v) => c with { GapLength = v }, 1,
                        "Средняя длина отдельных кусков стены и промежутков между ними."),
                    Slider("LandCliffLeanSlider", "Наклон взгляда", -100, 100, "%", 5,
                        c => c.Lean, (c, v) => c with { Lean = v }, 100,
                        "Стена уходит не прямо вниз, а вниз-вправо (+) или вниз-влево (−): так видны и боковые стены.")),
                Group("LandCliffFaces", "Грани", true,
                    Percent("LandCliffFacetsSlider", "Плоские грани", c => c.Facets, (c, v) => c with { Facets = v },
                        "0 — свет плавно переходит по стене, 100 — стена из плоских граней, у каждой свой тон."),
                    Slider("LandCliffFacetWidthSlider", "Ширина грани", 3, 256, "ед.", 1,
                        c => c.FacetWidth, (c, v) => c with { FacetWidth = v }, 1,
                        "Ширина одной грани вдоль прямой кромки."),
                    Percent("LandCliffFacetLinesSlider", "Линии на углах", c => c.FacetLines, (c, v) => c with { FacetLines = v },
                        "Сила вертикальных линий туши между гранями и по краям стены."),
                    Percent("LandCliffContrastSlider", "Контраст света", c => c.Contrast, (c, v) => c with { Contrast = v },
                        "Насколько грань, повёрнутая от света, темнее грани, повёрнутой к свету."),
                    Slider("LandCliffLightAngleSlider", "Свет с", 0, 359, "°", 5,
                        c => c.LightAngle, (c, v) => c with { LightAngle = v }, 1,
                        "Откуда падает свет, по компасу: 0 — север, 90 — восток, 180 — юг, 270 — запад. 315 — северо-запад, как на всех картах до сих пор.")),
                Group("LandCliffColours", "Цвет и заливка", false,
                    Colour("LandCliffColorBox", "Цвет скалы", "Освещённая грань скалы.", c => c.Color, (c, v) => c with { Color = v }),
                    Colour("LandCliffShadeColorBox", "Цвет тени", "Грань в тени и самая густая заливка.", c => c.ShadeColor, (c, v) => c with { ShadeColor = v }),
                    Slider("LandCliffWashSlider", "Заливка", -100, 100, "%", 5,
                        c => c.Wash, (c, v) => c with { Wash = v }, 100,
                        "Тон по высоте стены: меньше нуля — темнее у кромки и светлеет книзу (тушь с размывкой), больше нуля — темнеет к подножию."),
                    Percent("LandCliffRimShadowSlider", "Тень под кромкой", c => c.RimShadow, (c, v) => c with { RimShadow = v },
                        "Тёмная полоса сразу под краем земли, как тень от нависающей кромки.")),
                Group("LandCliffTexture", "Штриховка и точки", false,
                    Percent("LandCliffHatchingSlider", "Штриховка", c => c.Hatching, (c, v) => c with { Hatching = v },
                        "Сила штрихов пером, свисающих от кромки."),
                    Slider("LandCliffHatchSpacingSlider", "Шаг штрихов", 0.8, 64, "ед.", 0.2,
                        c => c.HatchSpacing, (c, v) => c with { HatchSpacing = v }, 1,
                        "Расстояние между штрихами. Меньше — гуще."),
                    Percent("LandCliffHatchLengthSlider", "Длина штрихов", c => c.HatchLength, (c, v) => c with { HatchLength = v },
                        "Какую часть стены проходят штрихи от кромки вниз."),
                    Slider("LandCliffHatchSlantSlider", "Наклон штрихов", -100, 100, "%", 5,
                        c => c.HatchSlant, (c, v) => c with { HatchSlant = v }, 100,
                        "0 — вертикальные штрихи, + — наклон вправо, − — влево."),
                    Percent("LandCliffStippleSlider", "Точки", c => c.Stipple, (c, v) => c with { Stipple = v },
                        "Густота точек: больше всего под кромкой, книзу реже."),
                    Slider("LandCliffStippleSizeSlider", "Размер точек", 0.3, 8, "ед.", 0.1,
                        c => c.StippleSize, (c, v) => c with { StippleSize = v }, 1,
                        "Размер одной точки."),
                    Slider("LandCliffStrataSlider", "Пласты", 0, LandCliffStyle.MaximumStrata, "", 1,
                        c => c.Strata, (c, v) => c with { Strata = (int)Math.Round(v) }, 1,
                        "Сколько слоёв породы видно поперёк одного уступа. 0 — без пластов.")),
                Group("LandCliffInk", "Тушь и низ", false,
                    Colour("LandCliffInkColorBox", "Цвет туши", "Цвет всех линий стены: углы, низ, края, штрихи и точки.", c => c.InkColor, (c, v) => c with { InkColor = v }),
                    Slider("LandCliffInkWidthSlider", "Толщина линий", 0, 8, "ед.", 0.1,
                        c => c.InkWidth, (c, v) => c with { InkWidth = v }, 1,
                        "Толщина линий туши на стене."),
                    Ui.Subgroup("Низ стены", footRow),
                    Percent("LandCliffFootRoughnessSlider", "Рваный низ", c => c.FootRoughness, (c, v) => c with { FootRoughness = v },
                        "0 — ровный низ, 100 — волны и свисающие зубцы."),
                    Percent("LandCliffFootTicksSlider", "Волоски и корни", c => c.FootTicks, (c, v) => c with { FootTicks = v },
                        "Короткие штрихи, свисающие под стеной, как на карандашных эскизах.")),
                Group("LandCliffShadow", "Тень у подножия", false,
                    Percent("LandCliffBaseShadowSlider", "Сила тени", c => c.BaseShadow, (c, v) => c with { BaseShadow = v },
                        "Мягкая тень, которую стена бросает на землю или воду под собой."),
                    Slider("LandCliffBaseShadowLengthSlider", "Длина тени", 10, 200, "%", 5,
                        c => c.BaseShadowLength, (c, v) => c with { BaseShadowLength = v }, 100,
                        "Длина тени в долях высоты яруса."),
                    Colour("LandCliffShadowColorBox", "Цвет тени", "Цвет тени у подножия.", c => c.BaseShadowColor, (c, v) => c with { BaseShadowColor = v })),
            },
        };

        var hint = Ui.Hint("Обрыв появится, когда эта земля будет выше земли или моря под ней: поднимите её уровень.");
        var raise = new Button { Name = "LandCliffRaiseButton", Content = "Поднять на ярус", HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(raise, "Поднять выбранную землю на один уровень: над морем и землёй ниже появится обрыв.");
        raise.Click += (_, _) => { _editor.ShiftLandLevel(1); SyncSidePanels(); };
        var noTier = new StackPanel { Spacing = 4, Children = { hint, raise } };

        _appearanceSyncs.Add(() =>
        {
            var tier = ActiveLandHasTier();
            noTier.IsVisible = !tier;
            settings.IsVisible = tier;
        });

        return EffectSection("LandCliffs", "Обрывы ярусов",
            "Стена скалы под южной кромкой земли, которая выше соседней: ярус над ярусом, как на объёмной карте.",
            () => Cliff()?.Height, defaults.Height, v => SetCliff(cliff => cliff with { Height = v }),
            noTier, settings);
    }

    /// <summary>True when the active land stands above some land drawn before it, or above the sea.</summary>
    private bool ActiveLandHasTier()
    {
        if (_editor.ActiveLayer is not LandLayer land) return false;
        var floor = 0;
        foreach (var other in TerrainOrder.Lands(_editor.Session.Document))
        {
            if (ReferenceEquals(other, land)) break;
            if (other.Visible && other.GeometryTiles.TileCount > 0) floor = Math.Min(floor, other.Level);
        }
        return land.Level > floor;
    }
}
