using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;

// --map <map> --object-list-drag true; add --object-list-centre true for a target at map centre.
// Measures real recycled rows before/after two mouse drags, scrolls the long Assets list,
// verifies two separate Undo entries and writes object-list-live.json. Only own test window.
internal static class ObjectListDragCheck
{
    public static int Run(Window main, string output, Action<string> log, Action<Window, string> shot, bool centred = false)
    {
        var checks = new Dictionary<string, bool>();
        var states = new List<ListState>();
        void Check(string name, bool value) { checks[name] = value; log((value ? "Готово: " : "Ошибка: ") + name); }
        AutomationElement? Find(string id) => main.FindFirstDescendant(cf => cf.ByAutomationId(id));
        void Press(string id)
        {
            var element = Retry.WhileNull(() => Find(id), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200)).Result
                ?? throw new InvalidOperationException("Не найден элемент: " + id);
            if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
            else if (element.Patterns.Toggle.IsSupported) element.Patterns.Toggle.Pattern.Toggle();
            else element.Click();
        }
        try
        {
            main.Focus(); main.SetForeground();
            var bounds = main.BoundingRectangle;
            var screen = System.Windows.Forms.Screen.FromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            Check("DISPLAY1", screen.DeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase));
            var canvas = Retry.WhileNull(() => Find("MapCanvas"), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
            if (canvas is null) throw new InvalidOperationException("Не найден холст.");
            if (centred)
            {
                Press("PanelSectionAssets"); Thread.Sleep(500);
                string FirstRow(string prefix) => main.FindAllDescendants()
                    .Select(c => c.Properties.AutomationId.ValueOrDefault ?? "").First(id => id.StartsWith(prefix, StringComparison.Ordinal));
                Press(FirstRow("AssetKindRow_")); Thread.Sleep(500);
                // The fixture's topmost/first row is its centre target. Select before fitting so
                // the quick property bar and both panels already have their final heights.
                Press(FirstRow("ObjectRow_")); Thread.Sleep(600);
                Press("FitButton"); Thread.Sleep(800);
            }
            var box = canvas.BoundingRectangle;
            var panelRight = Find("ContextPanel")?.BoundingRectangle.Right
                ?? (Find("CollapseContextPanelButton")?.BoundingRectangle.Right + 20) ?? box.Left;
            var inset = centred ? Math.Max(0, panelRight - box.Left) : 0;
            var grab = centred ? new Point(box.Left + (box.Width + inset) / 2, box.Top + box.Height / 2)
                : new Point(box.Left + box.Width / 2, box.Top + (int)(box.Height * .16));
            log($"Холст: {box}; отступ: {inset}; точка: {grab}; окно: {bounds}");
            Mouse.MoveTo(grab);
            if (!centred) Mouse.Click(grab);
            Thread.Sleep(900);
            var baseline = Report("после выбора");
            string Size() => (Find("WidthBox")?.AsTextBox().Text ?? "") + "/" + (Find("HeightBox")?.AsTextBox().Text ?? "");
            var originalSize = Size();
            Check("Список содержит несколько видимых строк", baseline.Height > 80 && baseline.VisibleRows > 1);
            Check("Исходная карта без изменений", Find("UndoButton") is { IsEnabled: false });
            shot(main, Path.Combine(output, "list-1-selected.png"));

            void Drag(Point from, int dx, int dy)
            {
                Mouse.MoveTo(from); Mouse.Down(MouseButton.Left);
                for (var i = 1; i <= 30; i++) { Mouse.MoveTo(new Point(from.X + dx * i / 30, from.Y + dy * i / 30)); Thread.Sleep(16); }
                Mouse.Up(MouseButton.Left); Thread.Sleep(1000);
            }
            void CheckList(string label)
            {
                var state = Report(label);
                Check(label, state.Height >= baseline.Height * .9 && state.VisibleRows > 1);
            }
            Drag(grab, 120, 90);
            CheckList("Список после первого перемещения");
            if (centred) Check("Первый drag перемещает объект без изменения размера", Size() == originalSize && originalSize != "/");
            Check("Первое перемещение записано в Undo", Find("UndoButton") is { IsEnabled: true });
            shot(main, Path.Combine(output, "list-2-dragged.png"));
            var list = Find("ObjectList");
            if (centred && list?.Patterns.Scroll.IsSupported == true)
            {
                list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 0); Thread.Sleep(500);
                CheckList("Список после прокрутки к началу");
                list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100); Thread.Sleep(500);
                CheckList("Список после прокрутки к концу");
            }
            Drag(new Point(grab.X + 120, grab.Y + 90), -100, -80);
            CheckList("Список после второго перемещения");
            if (centred) Check("Второй drag перемещает объект без изменения размера", Size() == originalSize && originalSize != "/");
            shot(main, Path.Combine(output, "list-3-dragged-again.png"));
            Press("UndoButton"); Thread.Sleep(500);
            var another = Find("UndoButton") is { IsEnabled: true };
            if (another) { Press("UndoButton"); Thread.Sleep(500); }
            Check("Два перемещения отменяются отдельно", another && Find("UndoButton") is { IsEnabled: false });
        }
        catch (Exception error) { Check("Проход без исключения", false); log(error.ToString()); }
        var failures = checks.Values.Count(value => !value);
        File.WriteAllText(Path.Combine(output, "object-list-live.json"), JsonSerializer.Serialize(new { checks, states, centred, failures, gatePassed = failures == 0 }, new JsonSerializerOptions { WriteIndented = true }));
        return failures;

        ListState Report(string phase)
        {
            var list = Find("ObjectList");
            if (list is null) throw new InvalidOperationException("Список объектов не найден: " + phase);
            var rows = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            var visible = rows.Count(r => !r.IsOffscreen && r.BoundingRectangle.Height > 1 && r.BoundingRectangle.IntersectsWith(list.BoundingRectangle));
            var state = new ListState(phase, list.BoundingRectangle.Width, list.BoundingRectangle.Height, rows.Length, visible);
            states.Add(state); log($"{phase}: список {state.Width}×{state.Height}, строк {state.RealizedRows}, видно {state.VisibleRows}");
            return state;
        }
    }
    private sealed record ListState(string Phase, int Width, int Height, int RealizedRows, int VisibleRows);
}
