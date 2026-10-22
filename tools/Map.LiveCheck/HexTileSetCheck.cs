using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

// Живой R15: --map <r15-live.mapproj> --hex-sets <папка с set.json> --out artifacts/r15/live.
// Импорт через настоящий системный выбор каталога, снимки только PrintWindow, DISPLAY1.
internal static class HexTileSetCheck
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public static int Run(Window main, string source, Func<Window[]> windows, string output, Action<string> log, Action<Window, string> shot)
    {
        var errors = 0; var bounds = main.BoundingRectangle;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2)));
        var dpi = GetDpiForWindow(main.Properties.NativeWindowHandle.ValueOrDefault);
        log($"R15: дисплей {screen.DeviceName}, {screen.Bounds}, DPI {dpi}; окно {bounds}.");
        if (!screen.DeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase) || screen.Bounds.Width != 1920 || screen.Bounds.Height != 1080 || dpi != 120)
        { errors++; log("Ошибка: целевой DISPLAY1 1920×1080 125 % не подтверждён."); }
        AutomationElement? Find(Window window, string id) => Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250)).Result;
        void Press(Window window, string id)
        {
            var c = Find(window, id); if (c is null) { errors++; log("Не найден элемент: " + id); return; }
            if (c.Patterns.ScrollItem.IsSupported) c.Patterns.ScrollItem.Pattern.ScrollIntoView();
            if (c.Patterns.Invoke.IsSupported) c.Patterns.Invoke.Pattern.Invoke();
            else if (c.Patterns.Toggle.IsSupported) c.Patterns.Toggle.Pattern.Toggle();
            Thread.Sleep(500);
        }
        Press(main, "HexTabMySets");
        var missing = Find(main, "HexMissingSetsText");
        if (missing?.Name.Contains("Не найден комплект", StringComparison.Ordinal) != true)
        { errors++; log("Ошибка: понятное сообщение об отсутствующем комплекте не найдено."); }
        shot(main, Path.Combine(output, "r15-missing-set.png"));
        Press(main, "HexImportSetButton");
        var picker = Retry.WhileNull(() => windows().Concat(main.ModalWindows).FirstOrDefault(w => w.Name == "Папка комплекта с set.json"), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        if (picker is null) { log("Не открылся системный выбор папки."); return errors + 1; }
        var input = picker.FindFirstDescendant(cf => cf.ByAutomationId("1152").And(cf.ByControlType(ControlType.Edit)))
            ?? picker.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByControlType(ControlType.Edit)))
            ?? picker.FindFirstDescendant(cf => cf.ByAutomationId("FileNameControlHost"))?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (input is not null) input.AsTextBox().Text = Path.GetFullPath(source);
        else
        {
            // A folder picker has no FileNameControlHost on this Windows build. Its Explorer
            // address bar is the native, keyboard-accessible way to navigate to the directory.
            picker.Focus(); picker.SetForeground();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
            Keyboard.Type(Path.GetFullPath(source)); Keyboard.Type(VirtualKeyShort.RETURN);
        }
        Thread.Sleep(700);
        var apply = picker.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)));
        if (apply is null) { picker.Close(); return errors + 1; }
        apply.AsButton().Invoke(); Thread.Sleep(1200);
        var imported = Find(main, "HexCheckSet_procedural");
        if (imported is null) { log("Комплект не появился после импорта."); return errors + 1; }
        log("Импорт: " + Find(main, "HexUserSetsMessage")?.Name);
        shot(main, Path.Combine(output, "r15-my-sets.png"));
        int MissingPixels(string file)
        {
            using var bitmap = new System.Drawing.Bitmap(file); var count = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    if (p.R > 150 && p.B > 110 && p.G < p.R * .85 && p.B > p.G * 1.1) count++;
                }
            return count;
        }
        var beforePixels = MissingPixels(Path.Combine(output, "r15-missing-set.png"));
        var afterPixels = MissingPixels(Path.Combine(output, "r15-my-sets.png"));
        log($"Розовая заглушка: {beforePixels} → {afterPixels} пикселей после импорта, без правки карты.");
        if (beforePixels < 300 || afterPixels > beforePixels / 2) { errors++; log("Ошибка: заглушка не исчезла с холста после импорта."); }
        Press(main, "HexCheckSet_procedural");
        var dialog = Retry.WhileNull(() => windows().Concat(main.ModalWindows).FirstOrDefault(w => w.AutomationId == "HexTileSetCheckWindow" || w.Title == "Проверка комплекта"), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250)).Result;
        if (dialog is null) return errors + 1;
        Press(dialog, "HexSetCheckNextProblem");
        var problem = Find(dialog, "HexSetCheckProblems")?.Name ?? "";
        log("Проверка соседства: " + problem);
        if (!problem.Contains("Край не совпадает", StringComparison.Ordinal)) errors++;
        shot(dialog, Path.Combine(output, "r15-neighbors.png"));
        var kind = Find(dialog, "HexSetCheckKind")!.AsComboBox();
        kind.Select(1); kind.Collapse();
        var number = Find(dialog, "HexSetCheckNumber")!;
        var edit = number.ControlType == ControlType.Edit ? number : number.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (edit?.Patterns.Value.IsSupported == true) edit.Patterns.Value.Pattern.SetValue("14");
        else errors++;
        Press(dialog, "HexSetCheckNextProblem");
        var turn = Find(dialog, "HexSetCheckProblems")?.Name ?? ""; log("Повороты: " + turn);
        if (!turn.Contains("Поворот запрещён", StringComparison.Ordinal)) errors++;
        shot(dialog, Path.Combine(output, "r15-rotations.png"));
        kind.Select(2); kind.Collapse();
        // Pair grass/peaks, upper-row overlap, zero rotation.
        if (edit?.Patterns.Value.IsSupported == true) edit.Patterns.Value.Pattern.SetValue("17");
        Press(dialog, "HexSetCheckNext");
        shot(dialog, Path.Combine(output, "r15-overlaps.png"));
        var summary = Find(dialog, "HexSetCheckSummary")?.Name ?? ""; log("Перекрытия: " + summary);
        if (!summary.Contains("54", StringComparison.Ordinal)) errors++;
        Press(dialog, "HexSetCheckCloseButton");
        log("R15 живой проход: " + (errors == 0 ? "PASS" : "FAIL") + ", ошибок: " + errors);
        return errors;
    }
}
