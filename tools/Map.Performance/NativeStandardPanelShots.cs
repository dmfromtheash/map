using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    /// <summary>
    /// `native-standard-panel-shots`: the tool panel of the standard constructor for every rail tool
    /// (land, land paint, river and road with a drawn one selected, line, shape, text, notes, brush,
    /// mask, select with the river picked), as it opens and with every category unfolded, plus the
    /// layout audit of the unfolded panel. Writes `&lt;output&gt;.std-panel-&lt;tool&gt;.png`,
    /// `…-open.png` (unfolded), `…-view.png` (what the window shows) and `std-panel-audit.md/.json`.
    /// </summary>
    private async Task StandardPanelShotsOnly()
    {
        var audit = new List<object>();
        var markdown = new StringBuilder("# Аудит раскладки панелей стандартного конструктора\n\n");
        var noTip = new SortedSet<string>(StringComparer.Ordinal);

        async Task Shoot(string tool)
        {
            await Settle(); await Settle();
            var plate = Find<Border>("ContextPanel");
            var host = plate.GetVisualDescendants().OfType<ScrollViewer>().First();
            host.Offset = default; await Settle();
            var panel = (Control)host.Content!;
            panel.UpdateLayout();
            var opened = panel.Bounds.Height;
            PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}.png"));
            PanelAudit.Save(plate, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-view.png"));
            var folded = panel.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToList();
            foreach (var expander in folded) expander.IsExpanded = true;
            await Settle(); panel.UpdateLayout();
            PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-open.png"));
            var issues = PanelAudit.Audit(panel);
            foreach (var tip in PanelAudit.MissingTips(_view)) noTip.Add(tip);
            audit.Add(new { tool, height = Math.Round(opened), unfolded = Math.Round(panel.Bounds.Height), viewport = Math.Round(host.Viewport.Height), folded = folded.Count, issues });
            markdown.AppendLine($"## {tool}: {opened:0} px как открывается, {panel.Bounds.Height:0} px раскрыто ({folded.Count} свёрнутых), видно {host.Viewport.Height:0} px\n");
            foreach (var issue in issues) markdown.AppendLine("- " + issue);
            if (issues.Count == 0) markdown.AppendLine("- нарушений раскладки не найдено");
            markdown.AppendLine();
            foreach (var expander in folded) expander.IsExpanded = false;
            await Settle();

            // Every other tab of the panel, unfolded, with its own audit.
            var strip = panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>().FirstOrDefault(g => g.Name?.EndsWith("TabsStrip", StringComparison.Ordinal) == true && g.IsEffectivelyVisible);
            if (strip is null) return;
            var tabs = strip.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>().ToList();
            foreach (var tab in tabs.Skip(1))
            {
                tab.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
                var inner = panel.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded && e.IsEffectivelyVisible).ToList();
                foreach (var expander in inner) expander.IsExpanded = true;
                await Settle(); panel.UpdateLayout();
                var key = tab.Name![(strip.Name!.Length - "Strip".Length)..];
                PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-tab-{key}.png"));
                var tabIssues = PanelAudit.Audit(panel);
                foreach (var tip in PanelAudit.MissingTips(_view)) noTip.Add(tip);
                audit.Add(new { tool = tool + "/" + key, height = Math.Round(panel.Bounds.Height), unfolded = Math.Round(panel.Bounds.Height), viewport = Math.Round(host.Viewport.Height), folded = inner.Count, issues = tabIssues });
                markdown.AppendLine($"### {tool} · вкладка {key}: {panel.Bounds.Height:0} px раскрыто\n");
                foreach (var issue in tabIssues) markdown.AppendLine("- " + issue);
                if (tabIssues.Count == 0) markdown.AppendLine("- нарушений раскладки не найдено");
                markdown.AppendLine();
                foreach (var expander in inner) expander.IsExpanded = false;
            }

            tabs[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        }

        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id);
        // The create buttons' pictures are frames of the real map drawn in the background (owner, 2026-10-31: «1 в 1
        // всё как в настоящем использовании»): wait for them, and check each shows a drawn map, not an empty strip.
        var drawn = new List<string>();
        async Task Pictures(string hero)
        {
            for (var i = 0; i < 60; i++)
            {
                var images = _view.GetVisualDescendants().OfType<Control>().Where(c => c.Name == hero)
                    .SelectMany(h => h.GetVisualDescendants().OfType<Image>()).ToList();
                if (images.Count > 0 && images.All(img => img.Source is not null)) { drawn.Add(hero); return; }
                await Task.Delay(100); await Settle();
            }
        }
        await Click("LandToolButton"); await Pictures("NewLandButtonHero"); await Shoot("land");
        _editor.SetLandPaintMode(true); await Shoot("land-paint");
        _editor.SetLandPaintMode(false); await Settle();

        await Click("RiverToolButton"); await Pictures("StartRiverButtonHero"); await Pictures("StartLakeButtonHero"); await Shoot("river-new");
        await Click("StartRiverButton");
        Point(120, 220); Point(380, 320); Point(640, 280); Point(900, 600);
        await Click("FinishRiverButton");
        Point(380, 320); await Settle();
        await Shoot("river");

        await Click("RoadToolButton"); await Pictures("StartRoadButtonHero"); await Shoot("road-new");
        Console.WriteLine("create pictures drawn: " + string.Join(", ", drawn));
        _report.Checks["create-pictures-drawn"] = drawn.Count == 4;
        await Click("StartRoadButton");
        Point(60, 430); Point(300, 380); Point(520, 470); Point(960, 380);
        await Click("FinishRoadButton");
        await Shoot("road");

        await Click("SelectToolButton"); Point(380, 320); await Shoot("select-river");
        _editor.DeselectObject(); await Shoot("select-empty");
        foreach (var (button, tool) in new[] { ("LineToolButton", "line"), ("ShapeToolButton", "shape"), ("TextToolButton", "text"), ("NotesToolButton", "notes"), ("BrushToolButton", "brush") })
        {
            await Click(button); await Shoot(tool);
        }

        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        _editor.SelectLayer(objects.Id); await Settle();
        await Click("MaskToolButton");
        if (_editor.ActiveLayer?.Mask is null) { _editor.ToggleActiveMask(); await Settle(); }
        await Shoot("mask");

        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "std-panel-audit.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "std-panel-audit.md"), markdown.ToString());
        // DEC-070: every visible control of the whole window (bars, rail, panels) in all the states above has a tooltip.
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "std-panel-no-tooltip.txt"), noTip);
        _report.Checks["every-visible-control-has-tooltip"] = noTip.Count == 0;
        Console.WriteLine($"controls without tooltip: {noTip.Count}");
        _report.Checks["standard-panel-shots-saved"] = audit.Count >= 14;

        // DEC-080: the «Внутри» card opens on every section tab, the column tabs and the main rail tools, and is drawn.
        var cards = 0; var badCards = new List<string>();
        foreach (var name in new[] { "RightTabProperties", "RightTabContent", "RightTabLayers",
                     "SelectToolButton", "LandToolButton", "RiverToolButton", "RoadToolButton", "AssetsLibraryButton" })
        {
            var target = Find<Control>(name);
            if (ToolTip.GetTip(target) is not ToolTip tip || !tip.Classes.Contains("inside-card")) { badCards.Add(name + ": нет карточки"); continue; }
            ToolTip.SetIsOpen(target, true);
            for (var i = 0; i < 20 && tip.Content is not Control { Bounds.Width: > 0 }; i++) await Settle();
            // Drawn, and (owner, 2026-10-31: «выходит за окна») shown no larger than the room between the control and the
            // window's edge: the fit box around the card is what the user sees.
            if (tip.Content is Viewbox { Name: "InsideCardFit", Child: Border { Name: "InsideCard" } card } fit
                && card.Bounds is { Width: > 300, Height: > 120 })
            {
                var top = TopLevel.GetTopLevel(target)!;
                var origin = target.TranslatePoint(new Point(0, 0), top) ?? new Point(0, 0);
                var room = ToolTip.GetPlacement(target) == PlacementMode.Bottom
                    ? top.ClientSize.Height - (origin.Y + target.Bounds.Height)
                    : top.ClientSize.Height;
                Console.WriteLine($"inside card {name}: card {card.Bounds.Width:0}x{card.Bounds.Height:0}, shown {fit.Bounds.Width:0}x{fit.Bounds.Height:0}, room {room:0}");
                if (fit.Bounds.Height > room || fit.Bounds.Width > top.ClientSize.Width) badCards.Add(name + ": больше окна");
                PanelAudit.Save(fit, Path.ChangeExtension(Options.OutputPath, $"inside-card-{name}.png"));
                cards++;
            }
            else badCards.Add(name + ": карточка не построена");
            ToolTip.SetIsOpen(target, false); await Settle();
        }
        foreach (var bad in badCards) Console.WriteLine("inside card: " + bad);
        Console.WriteLine($"inside cards drawn: {cards}/8");
        _report.Checks["inside-card-on-column-tabs-and-tools"] = badCards.Count == 0;

        // DEC-093: a section tab opens its navigation - the list, the show drawn by the renderer, the functions. The
        // editor gets a catalog of the owner's mountains (DEC-076), which the «Ассеты» show places one by one.
        var navCatalog = new Map.Assets.AssetCatalog(Path.Combine(_workspace, "nav-catalog"));
        navCatalog.ImportSet([.. Enumerable.Range(1, 4).Select(i => OwnerMountainFixtures.Path(i))], OwnerMountainFixtures.Names[0],
            new Map.Assets.AssetImportOptions(), DateTimeOffset.UnixEpoch);
        OpenEditor(_editor.Session.Document, null, navCatalog); await Settle();
        var navBad = new List<string>();
        foreach (var workspace in new[] { EditorWorkspace.Draw, EditorWorkspace.Objects, EditorWorkspace.World, EditorWorkspace.Appearance })
        {
            var nav = _view.OpenSectionNavForSmoke(workspace);
            for (var i = 0; i < 20 && nav is not Control { Bounds.Width: > 0 }; i++) await Settle();
            if (nav is not Border { Name: "SectionNav" } panel) { navBad.Add(workspace + ": нет панели"); continue; }
            var rows = panel.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("SectionNavItem_", StringComparison.Ordinal) == true);
            var chips = panel.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("SectionNavFunction_", StringComparison.Ordinal) == true);
            // The show is drawn in the background: wait for its first frame where the entry has one.
            var show = panel.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "SectionNavShow");
            for (var i = 0; i < 80 && show?.Child is Image { Source: null }; i++) await Task.Delay(100);
            var showDrawn = show is null || show.Child is Image { Source: not null };
            var top = TopLevel.GetTopLevel(panel)!;
            var inside = panel.TranslatePoint(new Point(0, 0), top) is { } at && at.Y + panel.Bounds.Height <= top.ClientSize.Height + 1;
            Console.WriteLine($"section nav {workspace}: {rows} entries, {chips} functions, show {(show is null ? "none" : showDrawn ? "drawn" : "missing")}, {panel.Bounds.Width:0}x{panel.Bounds.Height:0}");
            if (rows == 0 || chips == 0 && workspace != EditorWorkspace.World || !showDrawn || !inside) navBad.Add($"{workspace}: {rows}/{chips}/{showDrawn}/{inside}");
            PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"section-nav-{workspace}.png"));
            _view.CloseSectionNavForSmoke(); await Settle();
        }
        // A function chip does what it says: «Земля → Круг» takes the land tool with the ellipse brush.
        if (_view.OpenSectionNavForSmoke(EditorWorkspace.Draw) is Control drawNav)
        {
            await Settle();
            var circle = drawNav.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SectionNavFunction_Круг");
            circle?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
            if (circle is null || _editor.ActiveTool != EditorTool.Land || _editor.ActiveBrushPreset != BrushPresetKind.Ellipse) navBad.Add("Земля → Круг не сработал");
        }
        foreach (var bad in navBad) Console.WriteLine("section nav: " + bad);
        _report.Checks["section-nav-on-tabs"] = navBad.Count == 0;

        // DEC-094: «Пакет стиля» on «Вид» - four tiles 2×2, a click puts the map in the pack in one undo step, the
        // pictures are drawn, and «Сохранить свой» adds «Мой пакет» (kept in this run's workspace).
        Map.Presentation.ViewModels.EditorViewModel.OwnStylePackPath = Path.Combine(_workspace, "StylePacks", "own.mapproj");
        _view.ShowWorkspace(EditorWorkspace.Appearance, openWorldList: true); await Settle();
        var packBad = new List<string>();
        Button[] PackTiles() => [.. _view.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("StylePack_", StringComparison.Ordinal) == true)];
        if (PackTiles().Length != 4) packBad.Add($"плиток {PackTiles().Length}");
        var styleBefore = _editor.Session.Document.Style; var depthBefore = _editor.Session.UndoDepth;
        PackTiles().FirstOrDefault(b => b.Name == "StylePack_ink")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (_editor.ActiveStylePackId != "ink" || _editor.Session.Document.Style?.Preset != MapStylePreset.InkWetBrush || _editor.Session.UndoDepth != depthBefore + 1)
            packBad.Add("щелчок по «Тушь» не применил пакет одним шагом");
        // The pictures of the packs are drawn by the program once the map is finished.
        for (var i = 0; i < 300 && PackTiles().Any(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not Avalonia.Media.ImageBrush); i++) await Task.Delay(200);
        if (PackTiles().Any(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not Avalonia.Media.ImageBrush)) packBad.Add("картинки пакетов не нарисованы");
        var packPart = _view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "LookPart_pack");
        if (packPart is not null) PanelAudit.Save(packPart, Path.ChangeExtension(Options.OutputPath, "style-packs.png"));
        _editor.Undo(); await Settle();
        if (_editor.Session.Document.Style != styleBefore) packBad.Add("Ctrl+Z не вернул прежний стиль");
        Find<Button>("StylePackSaveOwn").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (PackTiles().Length != 5 || !File.Exists(Map.Presentation.ViewModels.EditorViewModel.OwnStylePackPath)) packBad.Add("«Мой пакет» не сохранён");
        foreach (var bad in packBad) Console.WriteLine("style packs: " + bad);
        _report.Checks["style-packs-on-look"] = packBad.Count == 0;
        Map.Presentation.ViewModels.EditorViewModel.OwnStylePackPath = null;

        // R09e: «Варианты карты» on «Вид» - the starting four as tiles (with «Зима», DEC-097), a click shows the map in «Ночь» in one undo
        // step, the pictures are drawn, Ctrl+Z goes back to «День», «+ Запомнить» adds a fifth.
        var variantBad = new List<string>();
        Button[] VariantTiles() => [.. _view.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("MapVariant_", StringComparison.Ordinal) == true)];
        if (VariantTiles().Length != 0) variantBad.Add($"плиток до создания {VariantTiles().Length}");
        Find<Button>("MapVariantStarters").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        var variants = _editor.Session.Document.Variants;
        if (VariantTiles().Length != 4 || variants.Items.Length != 4) variantBad.Add($"после «Создать» плиток {VariantTiles().Length}, вариантов {variants.Items.Length}");
        var dayId = variants.Items.FirstOrDefault(v => v.Name == "День")?.Id;
        var nightId = variants.Items.FirstOrDefault(v => v.Name == "Ночь")?.Id;
        var variantDepth = _editor.Session.UndoDepth;
        VariantTiles().FirstOrDefault(b => b.Name == "MapVariant_" + nightId?.ToString("N"))?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (_editor.Session.Document.Variants.Active != nightId || _editor.Session.Document.Lighting?.TimeOfDay != 1.0 || _editor.Session.UndoDepth != variantDepth + 1)
            variantBad.Add("щелчок по «Ночь» не показал вариант одним шагом");
        for (var i = 0; i < 300 && VariantTiles().Any(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not Avalonia.Media.ImageBrush); i++) await Task.Delay(200);
        if (VariantTiles().Any(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not Avalonia.Media.ImageBrush)) variantBad.Add("картинки вариантов не нарисованы");
        var variantPart = _view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "LookPart_variant");
        if (variantPart is not null) PanelAudit.Save(variantPart, Path.ChangeExtension(Options.OutputPath, "map-variants.png"));
        _editor.Undo(); await Settle();
        if (_editor.Session.Document.Variants.Active != dayId) variantBad.Add("Ctrl+Z не вернул «День»");
        Find<Button>("MapVariantAdd").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (VariantTiles().Length != 5) variantBad.Add($"после «Запомнить» плиток {VariantTiles().Length}");
        foreach (var bad in variantBad) Console.WriteLine("map variants: " + bad);
        _report.Checks["map-variants-on-look"] = variantBad.Count == 0;
    }
}
