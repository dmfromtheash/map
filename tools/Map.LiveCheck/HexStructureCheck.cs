using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

// R16 real-window pass: --map artifacts/r16/smoke-workspace/r16-live.mapproj --hex-structure true
// Program isolates the profile, maximizes on the secondary monitor and supplies PrintWindow shots.
internal static class HexStructureCheck
{
    public static int Run(Window main, string output, Action<string> log, Action<Window, string> shot)
    {
        var missing = 0;
        void Press(string id)
        {
            var button = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
            if (button is null) { missing++; log("Не найден элемент: " + id); return; }
            if (button.Patterns.Invoke.IsSupported) button.Patterns.Invoke.Pattern.Invoke();
            else if (button.Patterns.Toggle.IsSupported) button.Patterns.Toggle.Pattern.Toggle();
            Thread.Sleep(500);
        }
        Press("HexTabMarkup"); Press("HexModeRegion");
        var parent = main.FindFirstDescendant(cf => cf.ByAutomationId("HexRegionParent_1"));
        if (parent is null) { missing++; log("Не найден выбор родителя региона."); }
        else log("Выбор родителя региона доступен: " + parent.Name);
        shot(main, Path.Combine(output, "r16-regions.png"));
        var choice = main.FindFirstDescendant(cf => cf.ByAutomationId("HexPresentationChoice"));
        if (choice is null) { log("Не найден переключатель представлений."); return missing + 1; }
        var combo = choice.AsComboBox();
        var labels = new[] { "Рельеф", "Политика", "Путешествие", "Знания игроков" };
        for (var i = 0; i < 4; i++)
        {
            combo.Select(i); combo.Collapse(); Thread.Sleep(1100);
            log("Представление: " + labels[i]);
            shot(main, Path.Combine(output, "r16-view-" + i + ".png"));
        }
        combo.Select(0); combo.Collapse(); Thread.Sleep(500);
        Press("HexModeKey");
        // Open the saved key through its table: this uses the existing navigation rather than desktop coordinates.
        Press("HexKeyRow_1309");
        var add = main.FindFirstDescendant(cf => cf.ByAutomationId("HexPoiAddButton"));
        if (add is null) { missing++; log("Не найден список точек интереса."); }
        else log("Список точек интереса открыт.");
        shot(main, Path.Combine(output, "r16-poi.png"));
        return missing;
    }
}
