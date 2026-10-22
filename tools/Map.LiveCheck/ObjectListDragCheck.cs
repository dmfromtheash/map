using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;

// Tail of session 3: «the object list on the right collapses to one row after dragging».
// --map <map with objects> --object-list-drag true: selects an object on the canvas, drags it with
// the real mouse and records the list's height and visible rows before and after, with shots.
internal static class ObjectListDragCheck
{
    public static int Run(Window main, string output, Action<string> log, Action<Window, string> shot)
    {
        main.Focus();
        main.SetForeground();
        var canvas = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("MapCanvas")),
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
        if (canvas is null) { log("Не найден холст."); return 1; }
        var box = canvas.BoundingRectangle;
        // The showcase map is fitted into the canvas: its cartouche sits in the top middle.
        var grab = new Point(box.Left + box.Width / 2, box.Top + (int)(box.Height * 0.16));
        Mouse.MoveTo(grab);
        Thread.Sleep(200);
        Mouse.Click(grab);
        Thread.Sleep(900);
        Report("после выбора");
        shot(main, Path.Combine(output, "list-1-selected.png"));

        Mouse.MoveTo(grab);
        Mouse.Down(MouseButton.Left);
        for (var i = 1; i <= 30; i++)
        {
            Mouse.MoveTo(new Point(grab.X + i * 4, grab.Y + i * 3));
            Thread.Sleep(16);
        }
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(1200);
        Report("после перетаскивания");
        shot(main, Path.Combine(output, "list-2-dragged.png"));

        // A second drag on the canvas, as a user keeps arranging.
        var second = new Point(grab.X + 120, grab.Y + 90);
        Mouse.MoveTo(second);
        Mouse.Down(MouseButton.Left);
        for (var i = 1; i <= 20; i++) { Mouse.MoveTo(new Point(second.X - i * 5, second.Y - i * 4)); Thread.Sleep(16); }
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(1200);
        Report("после второго перетаскивания");
        shot(main, Path.Combine(output, "list-3-dragged-again.png"));
        // Undo both moves, so the window closes without the unsaved-changes question.
        for (var i = 0; i < 2; i++)
        {
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_Z);
            Thread.Sleep(400);
        }
        return 0;

        void Report(string when)
        {
            var list = main.FindFirstDescendant(cf => cf.ByAutomationId("ObjectList"));
            if (list is null) { log($"{when}: список объектов не найден"); return; }
            var rows = list.FindAllChildren();
            var visible = rows.Count(r => !r.IsOffscreen);
            log($"{when}: список {list.BoundingRectangle.Width}×{list.BoundingRectangle.Height}, строк {rows.Length}, видно {visible}, offscreen={list.IsOffscreen}");
        }
    }
}
