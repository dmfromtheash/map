# Functions for live checks on USER, loaded by agent.ps1 in user1's desktop session.
# Real mouse/keyboard (the owner is not at USER), UI Automation by control Name (= AutomationId), screen shots.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
[void][W]::SetProcessDpiAwarenessContext([IntPtr](-4))
$global:LiveRoot = 'C:\MapAudit\live'
$global:Ui = [System.Windows.Automation.AutomationElement]

# Map built from the clone, isolated profile, a copy of the owner's library (C:\MapAudit\owner-library).
function Start-Map([string]$exe = 'C:\Projects\Map\src\Map.App\bin\Release\net10.0\win-x64\Map.App.exe') {
  $p = "$LiveRoot\profile"
  New-Item -ItemType Directory -Force "$p\Recovery" | Out-Null
  if (-not (Test-Path "$p\Assets")) { Copy-Item C:\MapAudit\owner-library "$p\Assets" -Recurse }
  $env:MAP_UI_TEST_MODE = '1'; $env:MAP_UI_TEST_PROFILE_ROOT = $p; $env:MAP_UI_TEST_RECOVERY_ROOT = "$p\Recovery"
  $env:MAP_UI_TEST_SHORTCUT_SETTINGS_PATH = "$p\shortcuts.json"
  $proc = Start-Process $exe -PassThru -WorkingDirectory (Split-Path $exe)
  for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { break } }
  "pid $($proc.Id)"
}
function MapProc { Get-Process Map.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
function MapWin { $m = MapProc; if ($m) { $Ui::FromHandle($m.MainWindowHandle) } }
function Front { $m = MapProc; [W]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [W]::keybd_event(0x12, 0, 2, [IntPtr]::Zero); [void][W]::ShowWindow($m.MainWindowHandle, 3); [void][W]::SetForegroundWindow($m.MainWindowHandle) }
# The whole screen: dialogs, the catalog and the export window are separate windows.
function ScreenShot([string]$name) {
  $b = New-Object System.Drawing.Bitmap 1920, 1080; $g = [System.Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen(0, 0, 0, 0, $b.Size); $g.Dispose()
  $b.Save("$LiveRoot\shots\$name.png", [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); "screen $name"
}
function Find([string]$id, $root) {
  if (-not $root) { $root = MapWin }
  $c = New-Object System.Windows.Automation.OrCondition (
    (New-Object System.Windows.Automation.PropertyCondition ($Ui::AutomationIdProperty, $id)),
    (New-Object System.Windows.Automation.PropertyCondition ($Ui::NameProperty, $id)))
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Press([string]$id, $root) {
  $e = Find $id $root; if (-not $e) { return "NOT FOUND $id" }
  $p = $null
  if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return "invoked $id" }
  Click-El $e; "clicked $id"
}
function Click-At([int]$x, [int]$y) { [void][W]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 60; [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 40; [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero) }
function Click-El($e) { if (-not $e) { throw "element not found" }; $r = $e.Current.BoundingRectangle; Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) }
function Move-To([int]$x, [int]$y) { [void][W]::SetCursorPos($x, $y) }
function Wheel([int]$x, [int]$y, [int]$notches) { Move-To $x $y; for ($i = 0; $i -lt [math]::Abs($notches); $i++) { [W]::mouse_event(0x0800, 0, 0, 120 * [math]::Sign($notches), [IntPtr]::Zero); Start-Sleep -Milliseconds 80 } }
function Drag([int[]]$pts, [int]$stepMs = 15) {
  [void][W]::SetCursorPos($pts[0], $pts[1]); Start-Sleep -Milliseconds 50; [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
  for ($i = 2; $i -lt $pts.Length; $i += 2) {
    $x0 = $pts[$i - 2]; $y0 = $pts[$i - 1]; $x1 = $pts[$i]; $y1 = $pts[$i + 1]
    for ($t = 1; $t -le 8; $t++) { [void][W]::SetCursorPos([int]($x0 + ($x1 - $x0) * $t / 8), [int]($y0 + ($y1 - $y0) * $t / 8)); Start-Sleep -Milliseconds $stepMs }
  }
  [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}
function Key([byte]$vk, [switch]$Ctrl, [switch]$Shift) {
  if ($Ctrl) { [W]::keybd_event(0x11, 0, 0, [IntPtr]::Zero) }; if ($Shift) { [W]::keybd_event(0x10, 0, 0, [IntPtr]::Zero) }
  [W]::keybd_event($vk, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30; [W]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
  if ($Shift) { [W]::keybd_event(0x10, 0, 2, [IntPtr]::Zero) }; if ($Ctrl) { [W]::keybd_event(0x11, 0, 2, [IntPtr]::Zero) }
}
function TypeText([string]$s) { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait($s) }
# Visible controls: type | Name | text | x,y wxh (physical pixels).
function Dump([string]$filter = '', $root, [int]$max = 400) {
  if (-not $root) { $root = MapWin }
  $n = 0
  foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current; if ($c.IsOffscreen) { continue }
    $line = "{0} | {1} | {2} | {3:N0},{4:N0} {5:N0}x{6:N0}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.AutomationId, $c.Name, $c.BoundingRectangle.X, $c.BoundingRectangle.Y, $c.BoundingRectangle.Width, $c.BoundingRectangle.Height
    if ($filter -and $line -notmatch $filter) { continue }
    $line; $n++; if ($n -ge $max) { break }
  }
}
