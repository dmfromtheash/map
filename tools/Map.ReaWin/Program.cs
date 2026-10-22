using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

// REA native UI capture for Windows (Map project). Commands mirror REA's CLI:
//   observe-native-ui <exe> --pid <n> [--window-id <hwnd>] [--max-nodes 3000] [--out <dir>] [--name <base>]
//   capture-native-ui-scenario <exe> --pid <n> [--window-id <hwnd>] --steps <json|@file> [--out <dir>]
//   launch <exe> [--map <file.mapproj>] [--profile <dir>]   (Map in an isolated test profile; prints pid/hwnd)
//   audit <capture.json>                                     (layout audit of a saved capture)
// Result: {"ok":true,"result":{window,nodes,truncated,screenshot,gaps,audit}} as REA's ReaNativeUI helper,
// with the screenshot written as a PNG file next to the JSON instead of inline base64.
// Steps: {"kind":"click","automation_id":"X"} | {"kind":"click","name":"Сохранить"} | {"kind":"key","text":"Escape"}
//        {"kind":"key-entry","automation_id":"Box","text":"…"} | {"kind":"scroll","automation_id":"X","direction":"increment"}
//        {"kind":"wait","ms":500} | {"kind":"window","title":"…"} (switch to another window of the process; "" = main)
//        {"kind":"capture","name":"label"}  — every step except wait/window is followed by a capture.

// Per-monitor DPI awareness: without it Windows scales this process's view of a 125 % window,
// and PrintWindow pictures came out cropped (the "black fields" of the export window were this).
Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
if (args.Length < 2) return Usage();
var command = args[0];
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

try
{
    switch (command)
    {
        case "launch": return Launch(args[1]);
        case "audit":
        {
            var capture = JsonNode.Parse(File.ReadAllText(args[1]))!;
            var audit = Audit.Run(capture["result"]!);
            Console.WriteLine(audit.ToJsonString(json));
            return 0;
        }
        case "observe-native-ui":
        case "capture-native-ui-scenario":
            break;
        default: return Usage();
    }

    var executable = Path.GetFullPath(args[1]);
    var pid = int.Parse(Opt("--pid") ?? throw new ArgumentException("--pid is required"), System.Globalization.CultureInfo.InvariantCulture);
    var output = Path.GetFullPath(Opt("--out") ?? Path.Combine(Environment.CurrentDirectory, "rea-win"));
    Directory.CreateDirectory(output);
    var maxNodes = int.TryParse(Opt("--max-nodes"), out var m) ? m : 3000;
    using var process = Process.GetProcessById(pid);
    if (!string.Equals(Path.GetFullPath(process.MainModule!.FileName), executable, StringComparison.OrdinalIgnoreCase))
        return Fail("target-mismatch", $"Process {pid} runs {process.MainModule.FileName}, not {executable}.");

    using var automation = new UIA3Automation { ConnectionTimeout = TimeSpan.FromSeconds(30), TransactionTimeout = TimeSpan.FromSeconds(30) };
    var app = FlaUI.Core.Application.Attach(process);
    var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
    var window = main;
    if (Opt("--window-id") is { } hwndText)
    {
        var hwnd = new IntPtr(long.Parse(hwndText, System.Globalization.CultureInfo.InvariantCulture));
        window = app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Properties.NativeWindowHandle.ValueOrDefault == hwnd)
            ?? throw new ArgumentException($"Window {hwndText} is not a top-level window of process {pid}.");
    }

    var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
    var results = new JsonArray();
    var counter = 0;
    JsonObject Capture(string name)
    {
        var baseName = $"{++counter:D2}-{Safe(name)}";
        var result = Observe(window, pid, executable, sha, maxNodes, Path.Combine(output, baseName + ".png"));
        result["audit"] = Audit.Run(result);
        var envelope = new JsonObject { ["ok"] = true, ["result"] = result };
        File.WriteAllText(Path.Combine(output, baseName + ".json"), envelope.ToJsonString(json));
        var audit = result["audit"]!;
        Console.Error.WriteLine($"{baseName}: {result["nodes"]!.AsArray().Count} nodes, {audit["findings"]!.AsArray().Count} findings");
        return new JsonObject { ["name"] = baseName, ["json"] = baseName + ".json", ["png"] = baseName + ".png", ["findings"] = audit["findings"]!.AsArray().Count };
    }

    if (command == "observe-native-ui")
    {
        results.Add(Capture(Opt("--name") ?? "observe"));
    }
    else
    {
        var stepsText = Opt("--steps") ?? throw new ArgumentException("--steps is required");
        if (stepsText.StartsWith('@')) stepsText = File.ReadAllText(stepsText[1..]);
        var steps = JsonNode.Parse(stepsText)!.AsArray();
        results.Add(Capture("start"));
        foreach (var step in steps)
        {
            var kind = (string?)step!["kind"] ?? "";
            var target = Describe(step);
            switch (kind)
            {
                case "wait": Thread.Sleep((int?)step["ms"] ?? 500); continue;
                case "window":
                {
                    var title = (string?)step["title"] ?? "";
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    Window? found = null;
                    while (found is null && DateTime.UtcNow < deadline)
                    {
                        found = title.Length == 0 ? main
                            : app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                                || main.ModalWindows.Contains(w));
                        if (found is null) Thread.Sleep(250);
                    }
                    window = found ?? throw new InvalidOperationException("No window titled " + title);
                    continue;
                }
                case "capture": results.Add(Capture((string?)step["name"] ?? "capture")); continue;
                case "click":
                {
                    var element = FindTarget(window, step) ?? throw new InvalidOperationException("Not found: " + target);
                    if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
                    else if (element.Patterns.Toggle.IsSupported) element.Patterns.Toggle.Pattern.Toggle();
                    else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
                    else element.Click();
                    break;
                }
                case "key-entry":
                {
                    var element = FindTarget(window, step) ?? throw new InvalidOperationException("Not found: " + target);
                    element.AsTextBox().Text = (string?)step["text"] ?? "";
                    break;
                }
                case "scroll":
                {
                    var element = FindTarget(window, step) ?? throw new InvalidOperationException("Not found: " + target);
                    var up = (string?)step["direction"] == "decrement";
                    if (element.Patterns.Scroll.IsSupported) element.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, up ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement);
                    break;
                }
                case "key":
                {
                    window.Focus();
                    var keys = ((string?)step["text"] ?? "").Split('+').Select(k => Enum.Parse<VirtualKeyShort>(k.ToUpperInvariant() switch
                    {
                        "CONTROL" or "CTRL" => "CONTROL", "SHIFT" => "SHIFT", "ALT" => "ALT", "ESCAPE" or "ESC" => "ESCAPE",
                        "ENTER" => "ENTER", "DELETE" => "DELETE", "TAB" => "TAB", var k1 when k1.Length == 1 => "KEY_" + k1, var k2 => k2,
                    })).ToArray();
                    Keyboard.TypeSimultaneously(keys);
                    break;
                }
                default: throw new ArgumentException("Unknown step kind: " + kind);
            }

            Thread.Sleep((int?)step["settle_ms"] ?? 500);
            results.Add(Capture((string?)step["name"] ?? kind + "-" + target));
        }
    }

    var summary = new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["output"] = output, ["captures"] = results } };
    File.WriteAllText(Path.Combine(output, "scenario.json"), summary.ToJsonString(json));
    Console.WriteLine(summary.ToJsonString(json));
    return 0;
}
catch (Exception error)
{
    return Fail("capture-failed", error.Message);
}

int Fail(string code, string message)
{
    Console.WriteLine(new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message }.ToJsonString(json));
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: Map.ReaWin observe-native-ui|capture-native-ui-scenario <exe> --pid <n> [--window-id <hwnd>] [--steps <json|@file>] [--out <dir>]\n       Map.ReaWin launch <exe> [--map <file>] [--profile <dir>]\n       Map.ReaWin audit <capture.json>");
    return 2;
}

int Launch(string exe)
{
    var profile = Path.GetFullPath(Opt("--profile") ?? Path.Combine(Environment.CurrentDirectory, "rea-win-profile"));
    Directory.CreateDirectory(Path.Combine(profile, "Maps"));
    Directory.CreateDirectory(Path.Combine(profile, "Recovery"));
    // Map must not hold this tool's stdout/stderr: the caller (a shell capturing the JSON line)
    // waits for EOF, and Windows hands every inheritable handle to the child, not only the ones
    // it redirects - so «launch hangs» until Map closes. Our std handles stop being inheritable
    // and Map gets pipes of its own.
    foreach (var std in new[] { -10, -11, -12 })
        Native.SetHandleInformation(Native.GetStdHandle(std), 1, 0);
    var start = new ProcessStartInfo(Path.GetFullPath(exe))
    {
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
    };
    if (Opt("--map") is { } map)
    {
        var copy = Path.Combine(profile, "Maps", Path.GetFileName(map));
        File.Copy(map, copy, overwrite: true);
        start.ArgumentList.Add(copy);
    }
    start.Environment["MAP_UI_TEST_MODE"] = "1";
    start.Environment["MAP_UI_TEST_PROFILE_ROOT"] = profile;
    start.Environment["MAP_UI_TEST_RECOVERY_ROOT"] = Path.Combine(profile, "Recovery");
    start.Environment["MAP_UI_TEST_SHORTCUT_SETTINGS_PATH"] = Path.Combine(profile, "shortcuts.json");
    using var automation = new UIA3Automation { ConnectionTimeout = TimeSpan.FromSeconds(30), TransactionTimeout = TimeSpan.FromSeconds(30) };
    var app = FlaUI.Core.Application.Launch(start);
    var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(60));
    // The owner works on the primary display: the test window goes to another display when there is one.
    if (Environment.GetEnvironmentVariable("MAP_LIVE_PRIMARY") != "1"
        && System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary) is { } screen)
    {
        var handle = main.Properties.NativeWindowHandle.ValueOrDefault;
        Native.ShowWindow(handle, 9);
        Native.SetWindowPos(handle, IntPtr.Zero, screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height, 0x0040);
        Native.ShowWindow(handle, 3);
        Thread.Sleep(800);
    }
    Console.WriteLine(new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["pid"] = (int)WindowProcess(main.Properties.NativeWindowHandle.ValueOrDefault), ["window_id"] = main.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(), ["title"] = main.Title, ["profile"] = profile } }.ToJsonString(json));
    return 0;
}

static uint WindowProcess(IntPtr window) { Native.GetWindowThreadProcessId(window, out var pid); return pid; }

static AutomationElement? FindTarget(Window window, JsonNode step)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline)
    {
        AutomationElement? found = null;
        if ((string?)step["automation_id"] is { } id) found = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
        else if ((string?)step["name"] is { } name) found = window.FindFirstDescendant(cf => cf.ByName(name));
        if (found is not null) return found;
        Thread.Sleep(200);
    }

    return null;
}

static string Describe(JsonNode step) => (string?)step["automation_id"] ?? (string?)step["name"] ?? (string?)step["text"] ?? "";

static string Safe(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').Take(40).ToArray());

static JsonObject Observe(Window window, int pid, string executable, string sha, int maxNodes, string pngPath)
{
    var nodes = new JsonArray();
    var gaps = new JsonArray();
    var truncated = false;
    var windowRect = window.BoundingRectangle;
    void Walk(AutomationElement element, int[] path, int depth)
    {
        if (nodes.Count >= maxNodes) { truncated = true; return; }
        JsonObject node;
        AutomationElement[] children;
        try
        {
            var r = element.BoundingRectangle;
            var actions = new JsonArray();
            if (element.Patterns.Invoke.IsSupported) actions.Add("invoke");
            if (element.Patterns.Toggle.IsSupported) actions.Add("toggle");
            if (element.Patterns.Value.IsSupported) actions.Add("set-value");
            if (element.Patterns.Scroll.IsSupported) actions.Add("scroll");
            if (element.Patterns.SelectionItem.IsSupported) actions.Add("select");
            children = element.FindAllChildren();
            node = new JsonObject
            {
                ["path"] = new JsonArray([.. path.Select(p => (JsonNode)p)]),
                ["role"] = element.ControlType.ToString(),
                ["title"] = element.Properties.Name.ValueOrDefault ?? "",
                ["value"] = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.ValueOrDefault ?? "" : null,
                ["automation_id"] = element.Properties.AutomationId.ValueOrDefault ?? "",
                ["class_name"] = element.Properties.ClassName.ValueOrDefault ?? "",
                ["bounds"] = new JsonObject { ["x"] = (double)(r.X - windowRect.X), ["y"] = (double)(r.Y - windowRect.Y), ["w"] = (double)r.Width, ["h"] = (double)r.Height },
                ["offscreen"] = element.Properties.IsOffscreen.ValueOrDefault,
                ["enabled"] = element.Properties.IsEnabled.ValueOrDefault,
                ["actions"] = actions,
                ["children_count"] = children.Length,
            };
        }
        catch (Exception error)
        {
            gaps.Add($"{string.Join('.', path)}: {error.GetType().Name}");
            return;
        }

        nodes.Add(node);
        if (depth > 40) { gaps.Add($"{string.Join('.', path)}: depth limit"); return; }
        for (var i = 0; i < children.Length; i++) Walk(children[i], [.. path, i], depth + 1);
    }

    Walk(window, [], 0);
    var (width, height) = PrintWindows(window, pid, pngPath);
    var bytes = File.ReadAllBytes(pngPath);
    return new JsonObject
    {
        ["window"] = new JsonObject
        {
            ["pid"] = pid, ["window_id"] = window.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(),
            ["executable"] = executable, ["sha256"] = sha, ["title"] = window.Title,
            ["bounds"] = new JsonObject { ["x"] = (double)windowRect.X, ["y"] = (double)windowRect.Y, ["w"] = (double)windowRect.Width, ["h"] = (double)windowRect.Height },
            ["dpi_scale"] = Native.DpiScale(window.Properties.NativeWindowHandle.ValueOrDefault),
        },
        ["nodes"] = nodes,
        ["truncated"] = truncated,
        ["screenshot"] = new JsonObject
        {
            ["mime_type"] = "image/png", ["path"] = Path.GetFileName(pngPath),
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["width"] = width, ["height"] = height,
        },
        ["gaps"] = gaps,
    };
}

// Every visible window of the process over the target, drawn by itself (PrintWindow), so a dialog
// or popup appears in the picture and another program covering Map never does.
static (int Width, int Height) PrintWindows(Window window, int pid, string path)
{
    var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
    if (!Native.GetWindowRect(handle, out var rect)) throw new InvalidOperationException("The window has no rectangle.");
    var windows = new List<IntPtr>();
    Native.EnumWindows((w, _) =>
    {
        if (Native.IsWindowVisible(w) && Native.GetWindowThreadProcessId(w, out var owner) != 0 && owner == pid) windows.Add(w);
        return true;
    }, IntPtr.Zero);
    windows.Reverse();
    using var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        foreach (var w in windows)
        {
            if (!Native.GetWindowRect(w, out var part) || part.Right <= part.Left || part.Bottom <= part.Top) continue;
            using var layer = new Bitmap(part.Right - part.Left, part.Bottom - part.Top);
            using (var layerGraphics = Graphics.FromImage(layer))
            {
                var dc = layerGraphics.GetHdc();
                try { if (!Native.PrintWindow(w, dc, 2)) continue; }
                finally { layerGraphics.ReleaseHdc(dc); }
            }
            graphics.DrawImageUnscaled(layer, part.Left - rect.Left, part.Top - rect.Top);
        }
    }

    bitmap.Save(path, ImageFormat.Png);
    return (bitmap.Width, bitmap.Height);
}

/// <summary>
/// What a person sees as broken, read from the captured tree: a control past its container's edge,
/// two neighbours on top of each other, something past the window's edge, a text wider than its
/// box. Scroll containers legitimately hold content beyond their viewport and are not judged.
/// </summary>
internal static class Audit
{
    public static JsonObject Run(JsonNode result)
    {
        var nodes = result["nodes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var byPath = nodes.ToDictionary(n => string.Join('.', n["path"]!.AsArray().Select(p => (int)p!)));
        var window = result["window"]!["bounds"]!;
        double W = (double)window["w"]!, H = (double)window["h"]!;
        var findings = new JsonArray();
        static (double X, double Y, double W, double H) B(JsonObject n) =>
            ((double)n["bounds"]!["x"]!, (double)n["bounds"]!["y"]!, (double)n["bounds"]!["w"]!, (double)n["bounds"]!["h"]!);
        static bool Visible(JsonObject n) => !(bool)n["offscreen"]! && B(n).W > 1 && B(n).H > 1;
        static string Label(JsonObject n) => $"{n["role"]} «{n["title"]}»{((string?)n["automation_id"] is { Length: > 0 } id ? " #" + id : "")}";
        bool InScroller(string key)
        {
            while (key.Contains('.'))
            {
                key = key[..key.LastIndexOf('.')];
                if (byPath.TryGetValue(key, out var a) && (a["actions"]!.AsArray().Any(x => (string?)x == "scroll") || (string?)a["role"] == "List")) return true;
            }

            return false;
        }

        foreach (var (key, node) in byPath)
        {
            if (!Visible(node) || key.Length == 0) continue;
            var b = B(node);
            if (b.X < -2 || b.Y < -2 || b.X + b.W > W + 2 || b.Y + b.H > H + 2)
                findings.Add(Finding("за краем окна", node, null, b));
            var parentKey = key.Contains('.') ? key[..key.LastIndexOf('.')] : "";
            if (parentKey.Length > 0 && byPath.TryGetValue(parentKey, out var parent) && Visible(parent) && !InScroller(key)
                && parent["actions"]!.AsArray().All(x => (string?)x != "scroll"))
            {
                var p = B(parent);
                var over = Math.Max(Math.Max(p.X - b.X, b.X + b.W - (p.X + p.W)), Math.Max(p.Y - b.Y, b.Y + b.H - (p.Y + p.H)));
                if (over > 2) findings.Add(Finding((string?)node["role"] == "Text" ? "текст шире своего места" : "выходит за рамку родителя", node, parent, b, over));
            }
        }

        // Neighbours overlapping: siblings that both draw something and share more than a sliver.
        foreach (var group in byPath.Where(p => p.Key.Length > 0).GroupBy(p => p.Key.Contains('.') ? p.Key[..p.Key.LastIndexOf('.')] : ""))
        {
            var items = group.Select(p => p.Value).Where(Visible).ToList();
            for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
            {
                var a = B(items[i]); var c = B(items[j]);
                var w = Math.Min(a.X + a.W, c.X + c.W) - Math.Max(a.X, c.X);
                var h = Math.Min(a.Y + a.H, c.Y + c.H) - Math.Max(a.Y, c.Y);
                if (w <= 1 || h <= 1) continue;
                var smaller = Math.Min(a.W * a.H, c.W * c.H);
                if (w * h < 0.12 * smaller || w * h < 16) continue;
                // One fully inside the other is a container/backdrop pair, not a collision.
                if (w * h >= 0.98 * smaller) continue;
                findings.Add(Finding("налезают друг на друга", items[i], items[j], a, Math.Round(100 * w * h / smaller)));
            }
        }

        return new JsonObject { ["findings"] = findings, ["checked_nodes"] = nodes.Count };

        JsonObject Finding(string kind, JsonObject node, JsonObject? other, (double X, double Y, double W, double H) b, double amount = 0) => new()
        {
            ["kind"] = kind,
            ["element"] = Label(node),
            ["other"] = other is null ? null : Label(other),
            ["bounds"] = $"{b.X:0},{b.Y:0} {b.W:0}×{b.H:0}",
            ["amount"] = amount,
        };
    }
}

internal static class Native
{
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static double DpiScale(IntPtr window) { var dpi = GetDpiForWindow(window); return dpi == 0 ? 1 : dpi / 96.0; }
}
