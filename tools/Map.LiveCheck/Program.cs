using System.Diagnostics;
using System.Globalization;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

// Live pass over the real Map.App.exe through UI Automation. Usage:
//   dotnet run --project tools/Map.LiveCheck -c Release -- [--exe <Map.App.exe>] [--out <folder>] [--map <file.mapproj>]
// With --map the file is copied into the isolated profile and opened from the command line; the
// pass then opens the export window, dumps and screenshots it and checks the three output modes.
// Exit code 0: every step found its element; 2: a step could not find its element (screenshots
// and the UIA tree are still written); 1: the program did not start or crashed.
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
if (mapArgument is not null)
{
    if (!File.Exists(mapArgument))
    {
        Log($"FAIL: no map at {mapArgument}");
        return 1;
    }

    var copy = Path.Combine(profile, "Maps", Path.GetFileName(mapArgument));
    File.Copy(mapArgument, copy, overwrite: true);
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
    Log($"started pid {app.ProcessId}: {exe}");

    var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(60));
    Log($"main window '{main.Title}' after {clock.ElapsedMilliseconds} ms");

    if (mapArgument is not null)
    {
        // The map given on the command line opens straight into the workshop.
        var export = Retry.WhileNull(() => main.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")),
            TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(250)).Result;
        Log(export is null ? "MISSING: ExportButton (workshop did not open the map)" : $"workshop with the map after {clock.ElapsedMilliseconds} ms");
        if (export is null) missing++;
        Thread.Sleep(1500);
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

void Shot(AutomationElement element, string path)
{
    try
    {
        using var image = Capture.Element(element);
        image.ToFile(path);
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
