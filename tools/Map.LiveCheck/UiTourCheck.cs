using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

/// <summary>
/// `--ui-tour true` (no --map): a picture of every moment of the program for a visual review of
/// overlapping icons, cut and overflowing text. Home and each rail page; the New Map wizard for
/// both categories; the standard workshop with each rail tool, each section of the map panel, the
/// file menu, the export window and the unsaved-changes dialog on the way back; then the hex
/// workshop with each tab. Shots are PrintWindow pictures of Map's own windows only, numbered in
/// the order taken, into --out. Uses UIA by AutomationId; a missing id is logged, not fatal.
/// </summary>
internal static class UiTourCheck
{
    public static int Run(FlaUI.Core.Application app, FlaUI.UIA3.UIA3Automation automation, Window main, string output,
        Action<string> log, Action<AutomationElement, string> shot, Action<AutomationElement, string> dump)
    {
        var step = 0;
        var missing = 0;
        void Shot(string name, AutomationElement? element = null, bool dropdowns = true)
        {
            Thread.Sleep(450);
            shot(element ?? main, Path.Combine(output, $"{++step:D2}-{name}.png"));
            if (!dropdowns) return;
            // Every drop-down of this view, opened: the list is part of what the user sees.
            var root = element ?? main;
            var combos = root.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                .Where(c => !c.Properties.IsOffscreen.ValueOrDefault && c.Properties.IsEnabled.ValueOrDefault).Take(6).ToArray();
            var index = 0;
            foreach (var combo in combos)
            {
                try
                {
                    if (!combo.Patterns.ExpandCollapse.IsSupported) continue;
                    combo.Patterns.ExpandCollapse.Pattern.Expand();
                    Thread.Sleep(450);
                    shot(root, Path.Combine(output, $"{step:D2}-{name}-dropdown-{++index}-{Safe(combo.Properties.AutomationId.ValueOrDefault ?? combo.Name)}.png"));
                    combo.Patterns.ExpandCollapse.Pattern.Collapse();
                    Thread.Sleep(250);
                }
                catch (Exception error) { log("dropdown " + combo.Properties.AutomationId.ValueOrDefault + ": " + error.Message); }
            }
        }

        var pid = (uint)app.ProcessId;
        Window[] Windows()
        {
            var handles = new List<IntPtr>();
            EnumWindows((w, _) =>
            {
                if (IsWindowVisible(w) && GetWindowThreadProcessId(w, out var owner) != 0 && owner == pid) handles.Add(w);
                return true;
            }, IntPtr.Zero);
            var result = new List<Window>();
            foreach (var h in handles)
            {
                try { result.Add(automation.FromHandle(h).AsWindow()); } catch (Exception) { }
            }
            return [.. result];
        }
        Window? Other(Func<Window, bool>? also = null) =>
            Retry.WhileNull(() => main.ModalWindows.Concat(Windows()).FirstOrDefault(w => !w.Equals(main) && (also?.Invoke(w) ?? true)),
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
        AutomationElement? Find(AutomationElement root, string id) =>
            Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(200)).Result;
        bool Press(AutomationElement root, string id)
        {
            var element = Find(root, id);
            if (element is null) { log("MISSING: " + id); missing++; return false; }
            if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
            else if (element.Patterns.Toggle.IsSupported) element.Patterns.Toggle.Pattern.Toggle();
            else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
            else element.Click();
            Thread.Sleep(350);
            return true;
        }
        void CloseOthers()
        {
            var mainHandle = main.Properties.NativeWindowHandle.ValueOrDefault;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var others = new List<IntPtr>();
                EnumWindows((w, _) =>
                {
                    if (w != mainHandle && IsWindowVisible(w) && GetWindowThreadProcessId(w, out var owner) != 0 && owner == pid) others.Add(w);
                    return true;
                }, IntPtr.Zero);
                if (others.Count == 0) break;
                foreach (var w in others) PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                Thread.Sleep(700);
            }
        }

        // 1. Home and the rail pages.
        Shot("home");
        foreach (var id in new[] { "RailMapsButton", "RailTemplateButton", "RailPngButton", "LocalHelpButton", "AppSettingsButton" })
        {
            if (!Press(main, id)) continue;
            Thread.Sleep(900);
            Shot("page-" + id.Replace("Button", "", StringComparison.Ordinal), dropdowns: false);
            // A page that opens its own window (settings, help, the PNG viewer): that window too.
            var mainHandle = main.Properties.NativeWindowHandle.ValueOrDefault;
            foreach (var window in Windows().Where(w => w.Properties.NativeWindowHandle.ValueOrDefault != mainHandle))
                Shot("window-" + Safe(window.Title), window);
            CloseOthers();
            Press(main, "RailHomeButton");
        }

        // 2. The wizard, both categories, then a standard map.
        Window? Wizard(string category)
        {
            if (!Press(main, "RailCreateButton")) return null;
            var wizard = Other(w => w.AutomationId == "WizardWindow");
            if (wizard is null)
            {
                log("MISSING: wizard; windows: " + string.Join(" | ", Windows().Select(w => w.Title + " #" + w.AutomationId)) + "; main enabled=" + main.IsEnabled);
                Shot("no-wizard");
                missing++; return null;
            }
            Thread.Sleep(600);
            // The category cards are toggle buttons named after the category id (WizardCategoryHex…);
            // a toggle reacts to its click, so the real mouse picks it.
            var cardId = category == "Гекс" ? "WizardCategoryHex" : "WizardCategoryStandard";
            var card = Find(wizard, cardId);
            log(card is null ? $"MISSING: {cardId}" : $"wizard category: {card.Name}");
            if (card is not null)
            {
                card.Click();
                Thread.Sleep(500);
            }
            Shot("wizard-1-" + (category == "Гекс" ? "hex" : "standard"), wizard);
            Press(wizard, "ChooseCategoryButton");
            Thread.Sleep(600);
            Shot("wizard-2-" + (category == "Гекс" ? "hex" : "standard"), wizard);
            return wizard;
        }

        var standard = Wizard("Стандарт");
        if (standard is not null)
        {
            dump(standard, Path.Combine(output, "ui-tree-wizard-2.txt"));
            Press(standard, "CreateMapButton");
            var ready = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250)).Result;
            if (ready is null) { log("MISSING: workshop after CreateMapButton"); missing++; }
            else Workshop("std");
        }

        var hex = Wizard("Гекс");
        if (hex is not null)
        {
            Press(hex, "CreateMapButton");
            var ready = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250)).Result;
            if (ready is null) { log("MISSING: hex workshop"); missing++; }
            else Workshop("hex");
        }

        log($"UI tour: {step} shots, {missing} missing ids");
        return missing;

        void Workshop(string kind)
        {
            Thread.Sleep(1500);
            dump(main, Path.Combine(output, $"ui-tree-{kind}-workshop.txt"));
            Shot(kind + "-workshop");
            // Every tool of the rail: buttons that live in the left rail and end with ToolButton.
            var tools = main.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "")
                .Where(id => id.EndsWith("ToolButton", StringComparison.Ordinal) || id.StartsWith("HexTab", StringComparison.Ordinal))
                .Distinct().ToList();
            log($"{kind} tools: {string.Join(", ", tools)}");
            foreach (var id in tools)
            {
                if (!Press(main, id)) continue;
                Shot($"{kind}-tool-{id}");
                CloseOthers();
            }

            // The sections of the map panel on the right.
            if (main.FindFirstDescendant(cf => cf.ByAutomationId("PanelSectionSwitcher")) is { } switcher)
            {
                var sections = switcher.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).Or(cf.ByControlType(ControlType.RadioButton)));
                for (var i = 0; i < sections.Length; i++)
                {
                    var current = main.FindFirstDescendant(cf => cf.ByAutomationId("PanelSectionSwitcher"))?
                        .FindAllDescendants(cf => cf.ByControlType(ControlType.Button).Or(cf.ByControlType(ControlType.RadioButton)));
                    if (current is null || i >= current.Length) break;
                    var section = current[i];
                    if (section.Patterns.Invoke.IsSupported) section.Patterns.Invoke.Pattern.Invoke();
                    else section.Click();
                    Shot($"{kind}-section-{i}-{Safe(section.Name)}");
                }
            }

            if (Press(main, "FileMenuButton")) { Shot($"{kind}-file-menu"); Keyboard.Type(VirtualKeyShort.ESCAPE); Thread.Sleep(300); }
            if (Press(main, "ExportButton"))
            {
                var export = Other(w => w.AutomationId == "ExportWorkflowWindow" || w.FindFirstDescendant(cf => cf.ByAutomationId("StartExportButton")) is not null);
                if (export is not null) { Thread.Sleep(900); Shot($"{kind}-export", export); }
                CloseOthers();
            }

            // Back to the library: an unsaved map asks first.
            if (Press(main, "BackButton"))
            {
                var dialog = Other();
                if (dialog is not null)
                {
                    Shot($"{kind}-leave-dialog", dialog);
                    dump(dialog, Path.Combine(output, $"ui-tree-{kind}-leave-dialog.txt"));
                    var discard = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(b => b.Name.Contains("Не сохран", StringComparison.OrdinalIgnoreCase) || b.Name.Contains("Без сохран", StringComparison.OrdinalIgnoreCase));
                    if (discard is not null) discard.Patterns.Invoke.Pattern.Invoke(); else CloseOthers();
                }
                Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("RailCreateButton")), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
                Thread.Sleep(800);
                Shot($"{kind}-back-home");
            }
        }

        static string Safe(string name) => new string(name.Where(char.IsLetterOrDigit).Take(16).ToArray());
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
