using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

// R18: --map <r18-live.mapproj> --hex-travel true --out artifacts/r18/live.
// Program изолирует профиль, разворачивает на втором дисплее и снимает только HWND через PrintWindow.
internal static class HexTravelCheck
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    public static int Run(Window main, string output, Action<string> log, Action<Window, string> shot)
    {
        var errors = 0;
        var bounds = main.BoundingRectangle;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2)));
        log($"Дисплей: {screen.DeviceName}; экран {screen.Bounds}; окно {bounds}; основной: {screen.Primary}.");
        var dpi = GetDpiForWindow(main.Properties.NativeWindowHandle.ValueOrDefault);
        log($"Масштаб окна: {dpi} DPI ({dpi * 100 / 96} %). Цель владельца — DISPLAY1 1920×1080.");
        if (!screen.DeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase) || screen.Bounds.Width != 1920 || screen.Bounds.Height != 1080)
        { errors++; log("Ошибка: окно оказалось за пределами заданного DISPLAY1 1920×1080."); }
        if (screen.Primary) log("ASSUMPTION: в этой сессии DISPLAY1 помечен основным; целевой дисплей выбран по имени владельца.");
        AutomationElement? Find(string id) => Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200)).Result;
        void Press(string id)
        {
            var c = Find(id); if (c is null) { errors++; log("Не найден элемент: " + id); return; }
            if (c.Patterns.ScrollItem.IsSupported) c.Patterns.ScrollItem.Pattern.ScrollIntoView();
            if (c.Patterns.Invoke.IsSupported) c.Patterns.Invoke.Pattern.Invoke();
            else if (c.Patterns.Toggle.IsSupported) c.Patterns.Toggle.Pattern.Toggle();
            else if (c.Patterns.ExpandCollapse.IsSupported) c.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(700);
        }
        Press("HexTabMarkup"); Press("HexModeMeasure");
        var views = Find("HexPresentationChoice")?.AsComboBox();
        if (views is not null) { views.Select(0); views.Collapse(); }
        // Убираем кольца, чтобы первый снимок показывал чистые маршруты.
        Press("HexJourneyReachBox"); Press("HexJourneyRoute_0"); Thread.Sleep(800);
        shot(main, Path.Combine(output, "r18-route.png"));
        Press("HexJourneyRoute_1"); shot(main, Path.Combine(output, "r18-alternative.png"));
        Press("HexJourneyRoute_0"); Press("HexJourneyDay_1");
        var day = Find("HexJourneyDay_1");
        var text = day?.Properties.Name.ValueOrDefault ?? "";
        log("Навигатор: " + text);
        if (day is null) errors++;
        var navigationText = string.Join(" ", main.FindAllDescendants().Select(c => c.Properties.Name.ValueOrDefault ?? ""));
        if (!navigationText.Contains("День 1:", StringComparison.Ordinal) || !navigationText.Contains("Мельница", StringComparison.Ordinal))
        { errors++; log("Не подтверждён текст первой ночёвки у Мельницы."); }
        shot(main, Path.Combine(output, "r18-navigator.png"));
        Press("HexJourneyReachBox");
        // Общий вид: тот же доступный пользователю «Вписать карту».
        Press("FitButton"); Thread.Sleep(800);
        shot(main, Path.Combine(output, "r18-reach.png"));
        Press("HexJourneyCalendarExpander");
        var box = Find("HexJourneyCampaignDayBox");
        if (box?.Patterns.Value.IsSupported == true) box.Patterns.Value.Pattern.SetValue("270");
        else
        {
            var input = box?.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
            if (input?.Patterns.Value.IsSupported == true) input.Patterns.Value.Pattern.SetValue("270");
            else { errors++; log("Не удалось задать день зимы."); }
        }
        if (views is not null) { views.Select(2); views.Collapse(); } Thread.Sleep(800);
        var calendarText = string.Join(" ", main.FindAllDescendants().Select(c => c.Properties.Name.ValueOrDefault ?? ""));
        if (!calendarText.Contains("зима", StringComparison.Ordinal)) { errors++; log("Зимняя дата не подтверждена в интерфейсе."); }
        shot(main, Path.Combine(output, "r18-winter.png"));
        log("R18: ошибок живого прохода — " + errors); return errors;
    }
}
