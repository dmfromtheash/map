using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

// R25: --map <native-atlas workspace/parent.mapproj> --atlas true --out artifacts/r25/live.
// Только копии фикстур в собственном профиле, DISPLAY1, PrintWindow (Shot из Program).
internal static class AtlasCheck
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public static int Run(Window main, Func<Window[]> windows, string output, Action<string> log, Action<AutomationElement, string> shot)
    {
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks[name] = value; log((value ? "Готово: " : "Ошибка: ") + name); }
        var bounds = main.BoundingRectangle;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2)));
        var dpi = GetDpiForWindow(main.Properties.NativeWindowHandle.ValueOrDefault);
        Check("DISPLAY1 1920×1080 125 %", screen.DeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase) && screen.Bounds.Width == 1920 && screen.Bounds.Height == 1080 && dpi == 120);
        AutomationElement? Find(string id) => Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        void Press(AutomationElement? element)
        {
            if (element is null || !element.IsEnabled) throw new InvalidOperationException("Элемент атласа недоступен.");
            element.AsButton().Invoke(); Thread.Sleep(600);
        }
        string Id(AutomationElement element) => element.Properties.AutomationId.ValueOrDefault ?? "";
        string[] Crumbs() => main.FindAllDescendants().Where(c => Id(c).StartsWith("AtlasCrumb_", StringComparison.Ordinal)).Select(Id).ToArray();
        try
        {
            var child = Retry.WhileNull(() => main.FindAllDescendants().FirstOrDefault(c => Id(c).StartsWith("AtlasChild_", StringComparison.Ordinal)), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250)).Result;
            Check("Миниатюра подкарты на родителе", child is { IsOffscreen: false });
            var parentCrumb = Crumbs().Single();
            shot(main, Path.Combine(output, "r25-parent-preview.png"));
            Press(child);
            var up = Retry.WhileNull(() => Find("AtlasUpButton") is { IsEnabled: true } button ? button : null, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(300)).Result;
            Check("Переход в подкарту и две крошки", up is not null && Crumbs().Length == 2);
            shot(main, Path.Combine(output, "r25-child-breadcrumbs.png"));
            Press(up);
            Retry.WhileFalse(() => Crumbs().Length == 1, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
            Check("Наверх вернул родителя", Crumbs().SequenceEqual(new[] { parentCrumb }));
            child = main.FindAllDescendants().FirstOrDefault(c => Id(c).StartsWith("AtlasChild_", StringComparison.Ordinal));
            Press(child);
            Retry.WhileFalse(() => Crumbs().Length == 2, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
            Press(Find(parentCrumb));
            Retry.WhileFalse(() => Crumbs().Length == 1, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
            Check("Крошка открыла родителя", Crumbs().SequenceEqual(new[] { parentCrumb }));
            shot(main, Path.Combine(output, "r25-parent-return.png"));
            // Invoke can still target a peer retained while the parent view is being replaced.
            // Reacquire the visible button and use a real click in our own test window.
            main.Focus(); main.SetForeground(); Thread.Sleep(300);
            Find("AtlasCheckButton")!.Click(); Thread.Sleep(500);
            // Avalonia can expose a nonmodal owned window below its owner in the UIA tree.
            // It is visible in PrintWindow but absent from both top-level and ModalWindows.
            var dialog = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("AtlasCheckWindow"))?.AsWindow()
                ?? windows().Concat(main.ModalWindows).FirstOrDefault(w => Id(w) == "AtlasCheckWindow" || w.Title == "Проверка атласа"),
                TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
            Check("Окно проверки атласа открылось", dialog is not null);
            if (dialog is null)
            {
                log("Окна своего процесса: " + string.Join("; ", windows().Select(w => Id(w) + " · " + w.Title)));
                shot(main, Path.Combine(output, "r25-check-not-opened.png"));
            }
            if (dialog is not null)
            {
                var summary = Retry.WhileNull(() => dialog.FindFirstDescendant(cf => cf.ByAutomationId("AtlasCheckSummary")) is { } s && !s.Name.Contains("Проверка карт", StringComparison.Ordinal) ? s : null,
                    TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250)).Result;
                Check("Проверка атласа не находит потерянных файлов", summary?.Name.Contains("Проблем не найдено", StringComparison.Ordinal) == true);
                shot(dialog, Path.Combine(output, "r25-integrity.png"));
                dialog.FindFirstDescendant(cf => cf.ByAutomationId("AtlasCheckCloseButton"))!.AsButton().Invoke();
            }
        }
        catch (Exception error) { Check("Проход завершился без ошибок", false); log(error.ToString()); }
        var failures = checks.Values.Count(v => !v);
        File.WriteAllText(Path.Combine(output, "atlas-live.json"), JsonSerializer.Serialize(new { checks, failures, gatePassed = failures == 0,
            display = screen.DeviceName, width = screen.Bounds.Width, height = screen.Bounds.Height, dpi }, new JsonSerializerOptions { WriteIndented = true }));
        log("R25 живой проход: " + (failures == 0 ? "PASS" : "FAIL")); return failures;
    }
}
