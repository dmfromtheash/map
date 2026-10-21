using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

/// <summary>R19: --map fixture.mapproj --obsidian-export true --out artifacts/r19/live.
/// Uses UIA and the real save-name dialog. Only Map's export window is captured via PrintWindow.
/// Destination folders and the application profile are confined to the supplied output folder.</summary>
internal static class ObsidianExportCheck
{
    public static int Run(Window main, Window window, string output, Action<string> log, Action<Window, string> shot)
    {
        var missing = 0;
        var recipient = window.FindFirstDescendant(cf => cf.ByAutomationId("ExportRecipientComboBox"))?.AsComboBox();
        if (recipient is null) { log("MISSING: ExportRecipientComboBox"); return 1; }
        foreach (var players in new[] { false, true })
        {
            var label = players ? "player" : "gm";
            recipient.Select(players ? 1 : 0); recipient.Collapse(); Thread.Sleep(900);
            var button = window.FindFirstDescendant(cf => cf.ByAutomationId("ExportObsidianButton"));
            if (button is null || !button.IsEnabled || button.IsOffscreen)
            { missing++; log("MISSING, disabled or offscreen: ExportObsidianButton for " + label); continue; }
            log($"R19 {label}: {button.Name}; bounds={button.BoundingRectangle}");
            shot(window, Path.Combine(output, "r19-obsidian-" + label + ".png"));
            button.AsButton().Invoke();
            var picker = Retry.WhileNull(() => main.ModalWindows.Concat(window.ModalWindows)
                .FirstOrDefault(w => w.Name == "Новая папка Obsidian"), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
            if (picker is null) { log("MISSING: Obsidian destination dialog"); return missing + 1; }
            var input = picker.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByControlType(ControlType.Edit)))
                ?? picker.FindFirstDescendant(cf => cf.ByAutomationId("FileNameControlHost"))?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            if (input is null) { log("MISSING: file name field in Obsidian dialog"); picker.Close(); return missing + 1; }
            var folder = Path.Combine(output, label + "-obsidian");
            // The reused Windows dialog may still be applying its suggested name when its
            // UIA peer first appears. Wait for it to settle, then verify the actual edit value.
            Thread.Sleep(600);
            input.Focus();
            input.AsTextBox().Text = folder;
            Thread.Sleep(300);
            log($"R19 {label} destination field: {input.AsTextBox().Text}");
            if (!string.Equals(input.AsTextBox().Text, folder, StringComparison.Ordinal))
            { log("MISMATCH: destination field"); picker.Close(); return missing + 1; }
            Keyboard.Type(VirtualKeyShort.TAB);
            // File-list entries can also have id "1". Require the save button itself.
            var save = picker.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)))?.AsButton();
            if (save is null) { log("MISSING: save button in Obsidian dialog"); picker.Close(); return missing + 1; }
            log($"R19 {label} destination action: {save.Name}");
            save.Invoke();
            Thread.Sleep(600);
            log($"R19 {label} status: {main.FindFirstDescendant(cf => cf.ByAutomationId("StatusDetailText"))?.Name}");
            shot(window, Path.Combine(output, "r19-obsidian-after-" + label + ".png"));
            var written = Retry.WhileFalse(() => File.Exists(Path.Combine(folder, "index.md")),
                TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
            if (!written.Result) { log("MISSING: " + folder + "/index.md"); missing++; continue; }
            var files = Directory.GetFiles(folder, "*.md").ToDictionary(p => Path.GetFileNameWithoutExtension(p), File.ReadAllText);
            var unresolved = files.Values.SelectMany(t => Regex.Matches(t, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]"))
                .Count(m => !files.ContainsKey(m.Groups[1].Value));
            var text = string.Join('\n', files.Values);
            var privateText = new[] { "GM_SECRET", "GM_TITLE", "HIDDEN_TITLE", "HIDDEN_TEXT", "HIDDEN_REGION", "childMap", "secret.mapproj" };
            var valid = unresolved == 0 && files.Count == (players ? 22 : 83)
                && (players ? privateText.All(s => !text.Contains(s, StringComparison.Ordinal)) : privateText.All(s => text.Contains(s, StringComparison.Ordinal)));
            if (!valid) missing++;
            log($"R19 {label}: {files.Count} Markdown files; unresolved links={unresolved}; content/privacy={(valid ? "PASS" : "FAIL")}; folder={folder}");
        }
        log($"R19 live Obsidian: {(missing == 0 ? "PASS" : "FAIL")}");
        return missing;
    }
}
