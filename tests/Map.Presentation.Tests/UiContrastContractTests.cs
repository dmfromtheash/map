using System.Globalization;
using System.Text.RegularExpressions;

namespace Map.Presentation.Tests;

/// <summary>
/// The colour half of the same follow-up. The owner photographed two defects: file-menu artwork
/// that all but vanished, and object rows drawing #10264A on a #2B2B2B ground - about 1.03:1.
/// <para>
/// Neither colour was a mistake on its own. #10264A is Map's text on Map's paper; #2B2B2B is a
/// container colour Fluent's dark variant supplied underneath it. So these tests check the two
/// things that actually went wrong: the palette's own pairs have to clear WCAG, and no Map style
/// may put the light-surface text token on the dark shell.
/// </para>
/// The palette itself is read from App.axaml, so the test and the shipping colours can never be
/// two different sets of numbers.
/// </summary>
public sealed class UiContrastContractTests
{
    private const double BodyText = 4.5;
    private const double LargeOrGraphic = 3.0;

    private static readonly Lazy<string> Markup = new(() => File.ReadAllText(AppMarkupPath()));

    /// <summary>
    /// Every brush key with all the colours it can show: one for a solid brush, each stop for a
    /// gradient. Most surfaces are gradients, so reading only solid brushes left them unchecked.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Palette = new(() =>
    {
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            Markup.Value,
            "<SolidColorBrush\\s+x:Key=\"(?<key>[^\"]+)\"\\s+Color=\"(?<color>#[0-9A-Fa-f]{6,8})\""))
        {
            found[match.Groups["key"].Value] = [match.Groups["color"].Value];
        }

        foreach (Match match in Regex.Matches(
            Markup.Value,
            "<(?<kind>Linear|Radial)GradientBrush\\s+x:Key=\"(?<key>[^\"]+)\"(?<body>.*?)</\\k<kind>GradientBrush>",
            RegexOptions.Singleline))
        {
            found[match.Groups["key"].Value] = Regex.Matches(
                    match.Groups["body"].Value, "Color=\"(?<color>#[0-9A-Fa-f]{6,8})\"")
                .Select(stop => stop.Groups["color"].Value)
                .ToArray();
        }

        return found;
    });

    private static string AppMarkupPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Map.App", "App.axaml");
        Assert.True(File.Exists(path), "App.axaml not found at " + path);
        return path;
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..];
        }

        double Channel(int offset)
        {
            var raw = int.Parse(value.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static double Contrast(string foregroundKey, string backgroundKey)
    {
        Assert.True(Palette.Value.ContainsKey(foregroundKey), "No such brush: " + foregroundKey);
        Assert.True(Palette.Value.ContainsKey(backgroundKey), "No such brush: " + backgroundKey);
        // A gradient is only as readable as its worst stop.
        return Palette.Value[foregroundKey]
            .SelectMany(_ => Palette.Value[backgroundKey], (foreground, background) =>
            {
                var first = Luminance(foreground);
                var second = Luminance(background);
                var (lighter, darker) = first >= second ? (first, second) : (second, first);
                return (lighter + 0.05) / (darker + 0.05);
            })
            .Min();
    }

    private static void Require(string foreground, string background, double minimum)
    {
        var ratio = Contrast(foreground, background);
        Assert.True(
            ratio >= minimum,
            $"{foreground} on {background} is {ratio:0.00}:1, below the required {minimum:0.0}:1.");
    }

    /// <summary>Surfaces drawn on Map's own paper.</summary>
    private static readonly string[] LightSurfaces =
    [
        "MapSurfaceBrush", "MapPanelBrush", "MapElevatedBrush", "MapControlBrush",
        "MapControlHoverBrush", "MapControlPressedBrush", "MapFieldBrush", "MapSelectionBrush",
        "MapRailTileBrush", "MapRailTileHoverBrush", "MapRailTileSelectedBrush",
    ];

    /// <summary>
    /// The light surfaces a caption is actually drawn on. The rail tiles are not among them: they
    /// carry a glyph and nothing else, and the two transient pointer fills are checked separately.
    /// </summary>
    private static readonly string[] ReadingSurfaces =
    [
        "MapSurfaceBrush", "MapPanelBrush", "MapElevatedBrush", "MapControlBrush",
        "MapFieldBrush", "MapSelectionBrush",
    ];

    /// <summary>Surfaces of the dark shell: the rail, the app bar, the status bar, a tooltip.</summary>
    private static readonly string[] ChromeSurfaces =
    [
        "MapChromeBrush", "MapChromeElevatedBrush", "MapChromeHoverBrush", "MapChromePressedBrush",
    ];

    [Fact]
    public void Ordinary_text_is_readable_on_every_light_surface_including_hover_and_pressed()
    {
        foreach (var surface in LightSurfaces)
        {
            Require("MapTextBrush", surface, BodyText);
            Require("MapInputTextBrush", surface, BodyText);
            Require("MapMutedBrush", surface, BodyText);
        }
    }

    [Fact]
    public void A_faint_or_disabled_caption_stays_visible_rather_than_fading_out()
    {
        // A hint and a switched-off caption are ordinary small reading text: 4.5:1, the same as
        // any other. The 3:1 allowance belongs to large text and to graphics, not to a class name.
        foreach (var surface in ReadingSurfaces)
        {
            Require("MapFaintBrush", surface, BodyText);
            Require("MapDisabledTextBrush", surface, BodyText);
        }

        // On the two transient fills a control paints under the pointer, 3:1 is the floor; no
        // caption is drawn on them, and the live probe in the native smoke is what proves it.
        // A switched-off control is checked on every light surface without exception, transient
        // pointer fills and rail tiles included: "off" must never become "gone".
        foreach (var surface in LightSurfaces)
        {
            Require("MapDisabledTextBrush", surface, BodyText);
        }

        // And a disabled caption is still clearly weaker than a live one, so the state reads.
        Assert.True(
            Contrast("MapTextBrush", "MapPanelBrush") > Contrast("MapDisabledTextBrush", "MapPanelBrush"),
            "Disabled text must be visibly weaker than ordinary text.");
    }

    [Fact]
    public void The_dark_shell_has_its_own_text_and_never_borrows_the_light_surface_token()
    {
        foreach (var surface in ChromeSurfaces)
        {
            Require("MapChromeTextBrush", surface, BodyText);
            Require("MapChromeMutedBrush", surface, LargeOrGraphic);
        }

        // In the dark Tavern palette (2026-10-17) the panels and the shell are both wood, so the
        // work-area text must now read on the shell as well: a style that borrows it there is
        // still legible instead of vanishing.
        Require("MapTextBrush", "MapChromeBrush", BodyText);
    }

    [Fact]
    public void No_style_puts_the_light_surface_text_on_the_dark_shell()
    {
        // The rule the screenshots caught. A style hosted by the rail, the app bar or the status
        // bar may use MapTextBrush only when it also paints a light tile underneath it.
        string[] darkHosts = ["Border.rail ", "Border.app-bar ", "Border.status-bar "];
        var lightBackgrounds = LightSurfaces.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (Match style in Regex.Matches(
            Markup.Value,
            "<Style Selector=\"(?<selector>[^\"]+)\">(?<body>.*?)</Style>",
            RegexOptions.Singleline))
        {
            var selector = style.Groups["selector"].Value;
            if (!darkHosts.Any(host => selector.StartsWith(host, StringComparison.Ordinal)))
            {
                continue;
            }

            var body = style.Groups["body"].Value;
            if (!body.Contains("Property=\"Foreground\" Value=\"{DynamicResource MapTextBrush}\"", StringComparison.Ordinal))
            {
                continue;
            }

            var background = Regex.Match(
                body, "Property=\"Background\" Value=\"\\{DynamicResource (?<key>[A-Za-z]+)\\}\"");
            if (!background.Success || !lightBackgrounds.Contains(background.Groups["key"].Value))
            {
                offenders.Add(selector);
            }
        }

        Assert.True(offenders.Count == 0,
            "These shell styles use the light-surface text without a light tile: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void The_containers_fluent_would_colour_itself_are_pinned_to_maps_palette()
    {
        // A ListBox row, a menu presenter, a dropdown row and a tooltip take their colours from
        // the active Fluent variant unless a style says otherwise. Left alone they are exactly how
        // #2B2B2B got underneath #10264A.
        foreach (var selector in new[]
        {
            "<Style Selector=\"ListBox\">",
            "<Style Selector=\"ListBoxItem\">",
            "<Style Selector=\"ListBoxItem /template/ ContentPresenter#PART_ContentPresenter\">",
            "<Style Selector=\"MenuFlyoutPresenter\">",
            "<Style Selector=\"MenuItem\">",
            "<Style Selector=\"MenuItem TextBlock\">",
            "<Style Selector=\"ComboBoxItem\">",
            "<Style Selector=\"FlyoutPresenter\">",
            "<Style Selector=\"ToolTip\">",
        })
        {
            Assert.Contains(selector, Markup.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_in_the_palette_is_a_neutral_dark_container_colour()
    {
        // #2B2B2B belongs to no Map surface. If a brush ever lands in that neutral near-black
        // band, a variant's colour has been copied into the palette instead of fixed.
        foreach (var (key, hex) in Palette.Value.SelectMany(entry => entry.Value, (entry, hex) => (entry.Key, hex)))
        {
            var value = hex.TrimStart('#');
            if (value.Length == 8)
            {
                value = value[2..];
            }

            var r = int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = int.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = int.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var neutral = Math.Abs(r - g) <= 6 && Math.Abs(g - b) <= 6 && Math.Abs(r - b) <= 6;
            Assert.False(
                neutral && r is >= 25 and <= 70,
                $"{key} = {hex} is a neutral dark container colour, not a Map surface.");
        }
    }

    [Fact]
    public void A_field_and_a_focus_ring_have_a_boundary_the_eye_can_find()
    {
        // MapFieldBrush and MapPanelBrush are a shade apart, so the border is the only thing that
        // says where the input starts. WCAG asks 3:1 of exactly that boundary.
        Require("MapFieldBorderBrush", "MapPanelBrush", LargeOrGraphic);
        Require("MapFieldBorderBrush", "MapFieldBrush", LargeOrGraphic);
        Require("MapFocusBrush", "MapPanelBrush", LargeOrGraphic);
        Require("MapFocusBrush", "MapFieldBrush", LargeOrGraphic);
        Require("MapSelectionBorderBrush", "MapPanelBrush", LargeOrGraphic);
    }

    [Fact]
    public void State_colours_carry_their_own_readable_text()
    {
        // Since the Candle & Ornament style (2026-10-17) the main command is a dark, candle-lit
        // ground with a brass caption rather than a brass fill: the caption is checked on it.
        Require("MapOnAccentBrush", "MapPrimaryFillBrush", BodyText);
        Require("MapOnAccentBrush", "MapPrimaryHoverFillBrush", BodyText);
        Require("MapOnAccentBrush", "MapPrimaryPressedFillBrush", BodyText);
        Require("MapDangerBrush", "MapPanelBrush", BodyText);
        Require("MapDangerBrush", "MapDangerDimBrush", BodyText);
        Require("MapWarnBrush", "MapPanelBrush", BodyText);
        Require("MapWarnBrush", "MapWarnDimBrush", BodyText);
        Require("MapSuccessBrush", "MapPanelBrush", BodyText);
    }

    [Fact]
    public void A_plain_string_caption_is_coloured_too()
    {
        // Avalonia wraps a Button whose Content is a string in an AccessText, and a "TextBlock"
        // type selector does not match it. Every colour rule written only for TextBlock therefore
        // had a hole, and one caption came out white on a light control at 1.17:1.
        Assert.Contains("<Style Selector=\":is(Button) AccessText\">", Markup.Value, StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\":is(Button):disabled AccessText\">", Markup.Value, StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\"Border.rail :is(Button) AccessText\">", Markup.Value, StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\"Border.app-bar :is(Button) AccessText\">", Markup.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filled_button_keeps_its_own_caption_colour_wherever_it_is_hosted()
    {
        // Two matching styles are decided by declaration order, so the shell's "every caption in
        // the app bar is light" rule has to be followed by the button's own colour - otherwise a
        // filled Save button draws light text on its cyan fill, 1.81:1.
        var shellRule = Markup.Value.IndexOf(
            "<Style Selector=\"Border.app-bar TextBlock\">", StringComparison.Ordinal);
        var filledRule = Markup.Value.LastIndexOf(
            "<Style Selector=\":is(Button).primary TextBlock, :is(Button).primary AccessText\">",
            StringComparison.Ordinal);

        Assert.True(shellRule >= 0, "The shell text rule is gone; this guard no longer applies.");
        Assert.True(filledRule > shellRule,
            "A filled button's caption colour must be declared after the shell's, or the shell wins.");
    }

    [Fact]
    public void A_switched_off_control_is_dimmed_by_colour_and_not_dissolved_by_opacity()
    {
        // The old rule faded every disabled button to 45 % of itself, which is how a caption stops
        // being readable rather than merely quiet.
        var flattened = Regex.Replace(Markup.Value, @"\s+", " ");
        Assert.DoesNotContain(
            "<Style Selector=\"Button:disabled\"> <Setter Property=\"Opacity\" Value=\"0.45\" />",
            flattened,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\":is(Button):disabled\">", Markup.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_pins_one_approved_variant_instead_of_following_the_system()
    {
        var main = Path.Combine(
            Path.GetDirectoryName(AppMarkupPath())!, "MainWindow.cs");
        var source = File.ReadAllText(main);

        // The Tavern palette (2026-10-17) is a dark one, so the pinned variant is Dark - and the
        // markup must name the same variant, never follow the system.
        Assert.Contains("RequestedThemeVariant = ThemeVariant.Dark", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeVariant.Light", source, StringComparison.Ordinal);
        Assert.Contains("RequestedThemeVariant=\"Dark\"", Markup.Value, StringComparison.Ordinal);
    }
}
