using System.Diagnostics;
using System.Globalization;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

// Live pass over the real Map.App.exe through UI Automation. Usage:
//   dotnet run --project tools/Map.LiveCheck -c Release -- [--exe <Map.App.exe>] [--out <folder>] [--map <file.mapproj>]
// With --map the file is copied into the isolated profile and opened from the command line; the
// pass then opens the export window, dumps and screenshots it and checks the three output modes.
// With --hex-paths true and --map, selects the first border and flips all three blocking switches using UIA.
// With --player-package true and --map, selects the player recipient and verifies the visible package preview.
// With --obsidian-export true and --map, exports both Obsidian recipients through the native destination dialog.
// With --poster-export true and --map, writes a poster PDF and a layer PNG folder through system dialogs.
// Exit code 0: every step found its element; 2: a step could not find its element (screenshots
// and the UIA tree are still written); 1: the program did not start or crashed.
// Per-monitor DPI awareness: without it Windows scales this process's view of a 125 % window,
// and PrintWindow pictures came out cropped (the "black fields" of the export window were this).
SetProcessDpiAwarenessContext(new IntPtr(-4));
var root = FindRepoRoot();
var exe = Arg("--exe") ?? Path.Combine(root, "src", "Map.App", "bin", "Release", "net10.0", "win-x64", "Map.App.exe");
var outDir = Arg("--out") ?? Path.Combine(root, "artifacts", "live-check-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture));
outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);
var report = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

if (!File.Exists(exe))
{
    Log($"FAIL: no program at {exe}");
    return 1;
}

var profile = Path.Combine(outDir, "profile");
Directory.CreateDirectory(Path.Combine(profile, "Maps"));
Directory.CreateDirectory(Path.Combine(profile, "Recovery"));
var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
var mapArgument = Arg("--map");
var libraryArgument = Arg("--library-wins");
if (libraryArgument is not null)
    foreach (var file in Directory.GetFiles(Path.GetFullPath(libraryArgument), "*.mapproj"))
        File.Copy(file, Path.Combine(profile, "Maps", Path.GetFileName(file)), overwrite: true);
if (mapArgument is not null)
{
    if (!File.Exists(mapArgument))
    {
        Log($"FAIL: no map at {mapArgument}");
        return 1;
    }

    var copy = Path.Combine(profile, "Maps", Path.GetFileName(mapArgument));
    File.Copy(mapArgument, copy, overwrite: true);
    if (Arg("--atlas") is not null)
        foreach (var pattern in new[] { "*.mapproj", "*.mapatlas" })
            foreach (var source in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(mapArgument))!, pattern))
                File.Copy(source, Path.Combine(profile, "Maps", Path.GetFileName(source)), overwrite: true);
    start.ArgumentList.Add(copy);
}
start.Environment["MAP_UI_TEST_MODE"] = "1";
start.Environment["MAP_UI_TEST_PROFILE_ROOT"] = profile;
start.Environment["MAP_UI_TEST_RECOVERY_ROOT"] = Path.Combine(profile, "Recovery");
start.Environment["MAP_UI_TEST_SHORTCUT_SETTINGS_PATH"] = Path.Combine(profile, "shortcuts.json");

var clock = Stopwatch.StartNew();
var missing = 0;
FlaUI.Core.Application? app = null;
try
{
    app = FlaUI.Core.Application.Launch(start);
    using var automation = new UIA3Automation();
    // A hung window of another program slows every UIA call on the desktop; the 2 s default
    // made the first call fail on a busy machine.
    automation.ConnectionTimeout = TimeSpan.FromSeconds(30);
    automation.TransactionTimeout = TimeSpan.FromSeconds(30);
    Log($"started pid {app.ProcessId}: {exe}");

    var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(60));
    // The owner works on the primary display: test windows go to another display when there is one.
    MoveToSecondaryDisplay(main.Properties.NativeWindowHandle.ValueOrDefault, Log);
    Log($"main window '{main.Title}' after {clock.ElapsedMilliseconds} ms");

    if (mapArgument is not null)
    {
        // The map given on the command line opens straight into the workshop.
        var export = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")),
            TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(250)).Result;
        Log(export is null ? "MISSING: ExportButton (workshop did not open the map)" : $"workshop with the map after {clock.ElapsedMilliseconds} ms");
        if (export is null) missing++;
        Thread.Sleep(1500);

        if (Arg("--atlas") is not null)
        {
            MoveToSecondaryDisplay(main.Properties.NativeWindowHandle.ValueOrDefault, Log, "DISPLAY1");
            missing += AtlasCheck.Run(main, () => app.GetAllTopLevelWindows(automation), outDir, Log, Shot);
            // The shared finally ends this isolated process. Closing through UIA immediately
            // after the owned check window races its asynchronous close animation.
            return missing == 0 ? 0 : 2;
        }

        if (Arg("--hex-sets") is { } tileSetSource)
        {
            MoveToSecondaryDisplay(main.Properties.NativeWindowHandle.ValueOrDefault, Log, "DISPLAY1");
            missing += HexTileSetCheck.Run(main, tileSetSource,
                () => app.GetAllTopLevelWindows(automation), outDir, Log, Shot);
            main.Close();
            return missing == 0 ? 0 : 2;
        }

        // --hover: the real mouse glides across the canvas (the hex hover outline), with shots on the way.
        if (Arg("--hover") is not null && main.FindFirstDescendant(cf => cf.ByAutomationId("MapCanvas")) is { } canvas)
        {
            main.Focus();
            main.SetForeground();
            var box = canvas.BoundingRectangle;
            var y = box.Top + box.Height / 2;
            for (var x = box.Left + box.Width / 3; x < box.Left + box.Width * 2 / 3; x += 3)
            {
                Mouse.MoveTo(x, y);
                Thread.Sleep(16);
                if ((x - box.Left) % 120 < 3) Shot(main, Path.Combine(outDir, $"hover-{x - box.Left:D4}.png"));
            }

            Thread.Sleep(600);
            Shot(main, Path.Combine(outDir, "hover-rest.png"));
            Log($"hover: glided across the canvas {box}");
        }
        if (Arg("--object-list-drag") is not null)
        {
            missing += ObjectListDragCheck.Run(main, outDir, Log, Shot, Arg("--object-list-centre") is not null);
            main.Close();
            return missing == 0 ? 0 : 2;
        }
        if (Arg("--hex-travel") is not null)
        {
            MoveToSecondaryDisplay(main.Properties.NativeWindowHandle.ValueOrDefault, Log, "DISPLAY1");
            missing += HexTravelCheck.Run(main, outDir, Log, Shot);
            main.Close();
            return missing == 0 ? 0 : 2;
        }
        if (Arg("--hex-gameplay") is not null)
        {
            missing += HexGameplayCheck.Run(main,
                () => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title.EndsWith("окно игрока", StringComparison.Ordinal)),
                w => MoveToSecondaryDisplay(w.Properties.NativeWindowHandle.ValueOrDefault, Log), outDir, Log, Shot);
            main.Close();
            return missing == 0 ? 0 : 2;
        }
        if (Arg("--hex-structure") is not null)
        {
            missing += HexStructureCheck.Run(main, outDir, Log, Shot);
            main.Close();
            return missing == 0 ? 0 : 2;
        }
        if (Arg("--hex-paths") is not null)
        {
            foreach (var id in new[] { "HexTabMarkup", "HexModePath", "HexPathSelect_1_Border" })
            {
                var button = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                    TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
                if (button is null) { missing++; Log("MISSING: " + id); continue; }
                if (button.Patterns.Invoke.IsSupported) button.Patterns.Invoke.Pattern.Invoke();
                else if (button.Patterns.Toggle.IsSupported) button.Patterns.Toggle.Pattern.Toggle();
                else { missing++; Log("MISSING activation pattern: " + id); }
                Thread.Sleep(350);
            }
            Shot(main, Path.Combine(outDir, "r01-path-before.png"));
            foreach (var id in new[] { "HexPathBlocksSightCheckBox", "HexPathBlocksMoveCheckBox", "HexPathBlocksLightCheckBox" })
            {
                var part = main.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (part is null) { missing++; Log("MISSING: " + id); continue; }
                var check = part.AsCheckBox();
                var before = check.IsChecked;
                check.Toggle();
                Thread.Sleep(350);
                var after = main.FindFirstDescendant(cf => cf.ByAutomationId(id))!.AsCheckBox().IsChecked;
                if (after == before) missing++;
                Log($"R01 {id}: {before} -> {after}; enabled={check.IsEnabled}");
                // Restore the fixture flags with a second real UIA toggle; only the isolated copy is edited.
                check.Toggle();
                Thread.Sleep(250);
            }
            Shot(main, Path.Combine(outDir, "r01-path-after.png"));
        }
        Dump(main, Path.Combine(outDir, "ui-tree-workshop.txt"));
        Shot(main, Path.Combine(outDir, "10-workshop.png"));
        if (export is not null)
        {
            export.AsButton().Invoke();
            var pid2 = app.ProcessId;
            var desktop2 = automation.GetDesktop();
            var exportWindow = Retry.WhileNull(() => desktop2
                    .FindAllChildren(cf => cf.ByProcessId(pid2).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))
                    .Select(e => e.AsWindow())
                    .FirstOrDefault(w => w.AutomationId == "ExportWorkflowWindow" || w.FindFirstDescendant(cf => cf.ByAutomationId("StartExportButton")) is not null),
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)).Result;
            if (exportWindow is null)
            {
                missing++;
                Log("MISSING: export window (StartExportButton)");
            }
            else
            {
                Thread.Sleep(1000);
                Log($"export window '{exportWindow.Title}' after {clock.ElapsedMilliseconds} ms");
                Dump(exportWindow, Path.Combine(outDir, "ui-tree-export.txt"));
                Shot(exportWindow, Path.Combine(outDir, "11-export.png"));
                foreach (var id in new[] { "ExportSingleButton", "ExportTilesButton", "ExportMbTilesButton", "StartExportButton", "CloseExportButton" })
                {
                    var part = exportWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
                    Log(part is null ? $"MISSING: {id}" : $"found {id}: '{part.Name}'");
                    if (part is null) missing++;
                }

                if (Arg("--hex-paths") is not null)
                {
                    var level = exportWindow.FindFirstDescendant(cf => cf.ByAutomationId("ExportHexLevelComboBox"));
                    if (level is null) { missing++; Log("MISSING: ExportHexLevelComboBox"); }
                    else
                    {
                        var combo = level.AsComboBox();
                        combo.Select(1);
                        Thread.Sleep(800);
                        var selected = combo.SelectedItem;
                        if (selected is null) Log("R01 export level index 1 selected; UIA does not report SelectedItem. Verify the visible label in r01-export-level.png.");
                        else Log("R01 export level: " + selected.Name);
                        combo.Collapse();
                        Thread.Sleep(300);
                        Shot(exportWindow, Path.Combine(outDir, "r01-export-level.png"));
                    }
                }
                if (Arg("--export-wins") is not null)
                    missing += ExportWinsCheck.Export(exportWindow, outDir, Log, Shot, Dump);
                if (Arg("--player-package") is not null)
                    missing += PlayerPackageCheck.Run(exportWindow, outDir, Log, Shot, Dump);
                if (Arg("--obsidian-export") is not null)
                    missing += ObsidianExportCheck.Run(main, exportWindow, outDir, Log, Shot);
                if (Arg("--poster-export") is not null)
                    missing += PosterExportCheck.Run(main, exportWindow, outDir, Log, Shot);
                var mbtiles = exportWindow.FindFirstDescendant(cf => cf.ByAutomationId("ExportMbTilesButton"));
                if (mbtiles is not null)
                {
                    mbtiles.AsButton().Invoke();
                    Thread.Sleep(800);
                    Shot(exportWindow, Path.Combine(outDir, "12-export-mbtiles.png"));
                }

                exportWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseExportButton"))?.AsButton().Invoke();
                Thread.Sleep(500);
            }
        }

        try { main.Close(); } catch (FlaUI.Core.Exceptions.ElementNotEnabledException) { }
        for (var i = 0; i < 20 && !app.HasExited; i++) Thread.Sleep(500);
        Log(app.HasExited ? $"closed after {clock.ElapsedMilliseconds} ms" : "program still running after close; killing it");
        return missing switch { 0 => 0, < 0 => 1, _ => 2 };
    }

    // The library page is ready when its create button is in the tree.
    var create = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("RailCreateButton")),
        TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250)).Result;
    Log(create is null ? "MISSING: RailCreateButton (library page)" : $"library page ready after {clock.ElapsedMilliseconds} ms");
    if (create is null) missing++;
    Thread.Sleep(700);
    Dump(main, Path.Combine(outDir, "ui-tree-library.txt"));
    Shot(main, Path.Combine(outDir, "01-library.png"));

    if (Arg("--ui-tour") is not null)
    {
        missing += UiTourCheck.Run(app, automation, main, outDir, Log, Shot, Dump);
        try { main.Close(); } catch (FlaUI.Core.Exceptions.ElementNotEnabledException) { }
        return missing == 0 ? 0 : 2;
    }

    if (libraryArgument is not null)
    {
        missing += ExportWinsCheck.Library(main, outDir, Arg("--library-folder"), Log, Shot, Dump);
        main.Close();
        return missing == 0 ? 0 : 2;
    }

    if (create is not null)
    {
        create.AsButton().Invoke();
        // The wizard is a modal dialog of the main window: look at the main window's modal
        // children first, then at every window of the process on the desktop except the main one.
        var pid = app.ProcessId;
        var desktop = automation.GetDesktop();
        Window? FindWizard()
        {
            var modal = main.ModalWindows;
            if (modal.Length > 0) return modal[0];
            return desktop.FindAllChildren(cf => cf.ByProcessId(pid).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))
                .Select(e => e.AsWindow())
                .FirstOrDefault(w => !w.Equals(main) && w.Properties.ProcessId.ValueOrDefault == pid
                    && w.FindFirstDescendant(cf => cf.ByAutomationId("WizardStepHost")) is not null);
        }

        var wizard = Retry.WhileNull(FindWizard, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)).Result;
        if (wizard is null)
        {
            missing++;
            Log("MISSING: New Map wizard window (WizardStepHost)");
            Shot(main, Path.Combine(outDir, "02-after-create.png"));
        }
        else
        {
            Thread.Sleep(700);
            Log($"wizard '{wizard.Title}' after {clock.ElapsedMilliseconds} ms");
            Dump(wizard, Path.Combine(outDir, "ui-tree-wizard.txt"));
            Shot(wizard, Path.Combine(outDir, "02-wizard.png"));
            // Every named wizard part the smokes rely on must be reachable from outside the process.
            foreach (var id in new[] { "WizardStepTitle", "WizardCategoryTitle", "ChooseCategoryButton", "CancelWizardButton" })
            {
                var part = wizard.FindFirstDescendant(cf => cf.ByAutomationId(id));
                Log(part is null ? $"MISSING: {id}" : $"found {id}: '{part.Name}'");
                if (part is null) missing++;
            }

            wizard.Focus();
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Retry.WhileTrue(() => app.GetAllTopLevelWindows(automation).Length > 1, TimeSpan.FromSeconds(10));
            Shot(main, Path.Combine(outDir, "03-library-again.png"));
        }
    }

    try
    {
        main.Close();
    }
    catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
    {
        Log("main window still disabled by a dialog; closing the program by process");
    }

    for (var i = 0; i < 20 && !app.HasExited; i++) Thread.Sleep(500);

    Log(app.HasExited ? $"closed after {clock.ElapsedMilliseconds} ms" : "program still running after close; killing it");
}
catch (Exception error)
{
    Log("FAIL: " + error);
    missing = -1;
}
finally
{
    try { if (app is { HasExited: false }) app.Kill(); } catch (InvalidOperationException) { }
    File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
    Console.WriteLine($"report: {outDir}");
}

return missing switch { 0 => 0, < 0 => 1, _ => 2 };

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

// Every visible window of Map draws itself into the picture (PrintWindow, as tools/ai/live/mapwin.ps1
// does), bottom to top, so dialogs and popups over the element's window are included. A screen copy
// of the rectangle (Capture.Element) would save whatever other program covers Map.
void Shot(AutomationElement element, string path)
{
    try
    {
        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            throw new InvalidOperationException("the element has no window of its own");
        GetWindowThreadProcessId(handle, out var pid);
        var windows = new List<IntPtr>(); // top of the z-order first
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid)
                windows.Add(window);
            return true;
        }, IntPtr.Zero);
        windows.Reverse();
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            foreach (var window in windows)
            {
                if (!GetWindowRect(window, out var part) || part.Right <= part.Left || part.Bottom <= part.Top) continue;
                using var layer = new System.Drawing.Bitmap(part.Right - part.Left, part.Bottom - part.Top);
                using (var layerGraphics = System.Drawing.Graphics.FromImage(layer))
                {
                    var dc = layerGraphics.GetHdc();
                    try
                    {
                        if (!PrintWindow(window, dc, 2)) continue;
                    }
                    finally { layerGraphics.ReleaseHdc(dc); }
                }
                graphics.DrawImageUnscaled(layer, part.Left - rect.Left, part.Top - rect.Top);
            }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Log($"shot {Path.GetFileName(path)}");
    }
    catch (Exception error)
    {
        Log($"shot {Path.GetFileName(path)} failed: {error.Message}");
    }
}

void Dump(AutomationElement rootElement, string path)
{
    var lines = new StringBuilder();
    var count = 0;
    void Walk(AutomationElement element, int depth)
    {
        if (depth > 14 || count > 4000) return;
        count++;
        string type, id, name, rect;
        try
        {
            type = element.ControlType.ToString();
            id = element.AutomationId;
            name = element.Name;
            var r = element.BoundingRectangle;
            rect = $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}";
        }
        catch (Exception error)
        {
            lines.Append(' ', depth * 2).AppendLine("<unreadable: " + error.GetType().Name + ">");
            return;
        }

        lines.Append(' ', depth * 2).AppendLine($"{type} id='{id}' name='{name}' {rect}");
        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }
        foreach (var child in children) Walk(child, depth + 1);
    }

    Walk(rootElement, 0);
    File.WriteAllText(path, lines.ToString());
    Log($"ui tree {Path.GetFileName(path)}: {count} elements");
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr window);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

static void MoveToSecondaryDisplay(IntPtr window, Action<string> log, string? preferredDisplay = null)
{
    if (Environment.GetEnvironmentVariable("MAP_LIVE_PRIMARY") == "1") return;
    var screen = preferredDisplay is null
        ? System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary)
        : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.EndsWith(preferredDisplay, StringComparison.OrdinalIgnoreCase));
    if (screen is null || window == IntPtr.Zero) return;
    ShowWindow(window, 9); // restore, so the move is not undone by a maximised state
    var area = screen.WorkingArea;
    SetWindowPos(window, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0040);
    ShowWindow(window, 3); // maximise on that display
    Thread.Sleep(800);
    log($"window moved to {screen.DeviceName} {area}");
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr window, int command);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(IntPtr value);

delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct WindowRect
{
    public int Left, Top, Right, Bottom;
}
