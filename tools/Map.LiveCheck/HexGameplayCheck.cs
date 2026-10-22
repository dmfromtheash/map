using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

// R17: --map artifacts/r17/smoke-workspace/r17-live.mapproj --hex-gameplay true --out artifacts/r17/live.
// Program задаёт три изолированных пути, второй дисплей и снимки PrintWindow. При открытом Map владельца не запускать.
internal static class HexGameplayCheck
{
    public static int Run(Window main, Func<Window?> playerWindow, Action<Window> move,
        string output, Action<string> log, Action<Window, string> shot)
    {
        var missing = 0;
        void Press(string id)
        {
            var c = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200)).Result;
            if (c is null) { missing++; log("Не найден элемент: " + id); return; }
            if (c.Patterns.Invoke.IsSupported) c.Patterns.Invoke.Pattern.Invoke();
            else if (c.Patterns.Toggle.IsSupported) c.Patterns.Toggle.Pattern.Toggle();
            Thread.Sleep(500);
        }
        Press("HexTabMarkup"); Press("HexModeParty");
        var radius = main.FindFirstDescendant(cf => cf.ByAutomationId("HexPartyRadiusBox"));
        if (radius is null) { missing++; log("Нет поля обзора партии."); }
        shot(main, Path.Combine(output, "r17-party.png"));
        Press("HexModeKey"); Press("HexKeyRow_1309");
        var condition = main.FindFirstDescendant(cf => cf.ByAutomationId("HexRevealConditionBox"));
        if (condition is null) { missing++; log("Нет условия раскрытия."); }
        shot(main, Path.Combine(output, "r17-gm-key.png"));
        Press("HexModeParty"); Press("HexPartyPlayerButton");
        var player = Retry.WhileNull(playerWindow, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250)).Result;
        if (player is null) { missing++; log("Окно игрока не открылось."); }
        else
        {
            move(player); Thread.Sleep(1500);
            var names = player.FindAllDescendants().Select(c => c.Name).ToArray();
            var safe = names.Any(n => n.Contains("Старая обитель", StringComparison.Ordinal))
                && names.Any(n => n.Contains("Говорят", StringComparison.Ordinal))
                && !names.Any(n => n.Contains("Истинное имя R17", StringComparison.Ordinal) || n.Contains("Секрет ведущего R17", StringComparison.Ordinal));
            log(safe ? "Окно игрока: слух и примерная подпись, секретов в элементах нет." : "Не подтверждено содержимое карты знаний.");
            if (!safe) missing++;
            shot(player, Path.Combine(output, "r17-player.png")); player.Close();
        }
        var views = main.FindFirstDescendant(cf => cf.ByAutomationId("HexPresentationChoice"))?.AsComboBox();
        if (views is null) { missing++; log("Нет представлений гекса."); }
        else
        {
            views.Select(3); views.Collapse(); Thread.Sleep(800); shot(main, Path.Combine(output, "r17-knowledge.png"));
            views.Select(0); views.Collapse();
        }
        log("R17: ошибок живого прохода — " + missing);
        return missing;
    }
}
