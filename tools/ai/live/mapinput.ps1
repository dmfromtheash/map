# Drives the Map window through Win32 input messages posted to its own message loop (the same
# WM_MOUSE*/WM_KEY* path a real mouse and keyboard take), without moving the owner's cursor or
# needing the window in the foreground. Coordinates are client pixels of the Map window.
param(
  [string]$Action = "click",   # click | down | up | move | drag | wheel | key | text
  [int]$X = 0, [int]$Y = 0,
  [string]$Points = "",        # drag: "x1,y1;x2,y2;..." visited with -StepMs between points
  [int]$StepMs = 16,
  [int]$Delta = 120,           # wheel: positive = zoom in (away from user)
  [int]$Count = 1,
  [string]$Key = "",           # key: virtual key name e.g. "Z", "Escape", "Control+Z"
  [string]$Button = "left"     # drag: left | middle
)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MapInput {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
    WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
  public static IntPtr LParam(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
  public static IntPtr ScreenLParam(IntPtr h, int x, int y) { var p = new POINT { X = x, Y = y }; ClientToScreen(h, ref p); return LParam(p.X, p.Y); }
}
"@
if (-not ([System.Management.Automation.PSTypeName]"MapFind").Type) { Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class MapFind {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString().StartsWith("Map")) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
}
$proc = Get-Process Map.App -ErrorAction Stop | Select-Object -First 1
$h = [MapFind]::Find([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { throw "Map window not found" }
$MK_LBUTTON = [IntPtr]1
function MMove([int]$x, [int]$y, [IntPtr]$w) { [MapInput]::SendMessage($h, [MapInput]::WM_MOUSEMOVE, $w, [MapInput]::LParam($x, $y)) | Out-Null }
$MK_MBUTTON = [IntPtr]0x10
function MDown([int]$x, [int]$y) { if ($Button -eq "middle") { [MapInput]::SendMessage($h, [MapInput]::WM_MBUTTONDOWN, $MK_MBUTTON, [MapInput]::LParam($x, $y)) | Out-Null } else { [MapInput]::SendMessage($h, [MapInput]::WM_LBUTTONDOWN, $MK_LBUTTON, [MapInput]::LParam($x, $y)) | Out-Null } }
function MUp([int]$x, [int]$y) { if ($Button -eq "middle") { [MapInput]::SendMessage($h, [MapInput]::WM_MBUTTONUP, [IntPtr]0, [MapInput]::LParam($x, $y)) | Out-Null } else { [MapInput]::SendMessage($h, [MapInput]::WM_LBUTTONUP, [IntPtr]0, [MapInput]::LParam($x, $y)) | Out-Null } }
function VK([string]$name) {
  switch ($name.ToLowerInvariant()) {
    "escape" { return 0x1B } "enter" { return 0x0D } "control" { return 0x11 } "shift" { return 0x10 } "alt" { return 0x12 }
    "space" { return 0x20 } "delete" { return 0x2E } "tab" { return 0x09 }
    default { return [int][char]$name.ToUpperInvariant()[0] }
  }
}
switch ($Action) {
  "move" { MMove $X $Y ([IntPtr]0); "moved $X,$Y" }
  "down" { MMove $X $Y ([IntPtr]0); MDown $X $Y; "down $X,$Y" }
  "up" { MUp $X $Y; "up $X,$Y" }
  "click" {
    for ($i = 0; $i -lt $Count; $i++) { MMove $X $Y ([IntPtr]0); Start-Sleep -Milliseconds 30; MDown $X $Y; Start-Sleep -Milliseconds 40; MUp $X $Y; Start-Sleep -Milliseconds 60 }
    "clicked $X,$Y x$Count"
  }
  "drag" {
    $pts = @($Points.Split(';') | ForEach-Object { $a = $_.Split(','); ,@([int]$a[0], [int]$a[1]) })
    $first = $pts[0]; MMove $first[0] $first[1] ([IntPtr]0); Start-Sleep -Milliseconds 30; MDown $first[0] $first[1]
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $n = 0
    foreach ($p in $pts[1..($pts.Count - 1)]) {
      $n++; $due = $n * $StepMs; while ($sw.ElapsedMilliseconds -lt $due) { [System.Threading.Thread]::SpinWait(2000) }
      MMove $p[0] $p[1] $MK_LBUTTON
    }
    $last = $pts[-1]; Start-Sleep -Milliseconds 30; MUp $last[0] $last[1]
    "dragged $($pts.Count) points in $($sw.ElapsedMilliseconds) ms"
  }
  "wheel" {
    MMove $X $Y ([IntPtr]0)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $Count; $i++) {
      [MapInput]::SendMessage($h, [MapInput]::WM_MOUSEWHEEL, [IntPtr](($Delta -band 0xFFFF) -shl 16), [MapInput]::ScreenLParam($h, $X, $Y)) | Out-Null
      $due = ($i + 1) * $StepMs; while ($sw.ElapsedMilliseconds -lt $due) { [System.Threading.Thread]::SpinWait(2000) }
    }
    "wheel x$Count delta=$Delta at $X,$Y in $($sw.ElapsedMilliseconds) ms"
  }
  "key" {
    $parts = $Key.Split('+'); $mods = @(); $main = $parts[-1]
    if ($parts.Count -gt 1) { $mods = $parts[0..($parts.Count - 2)] }
    foreach ($m in $mods) { [MapInput]::SendMessage($h, [MapInput]::WM_KEYDOWN, [IntPtr](VK $m), [IntPtr]0) | Out-Null }
    [MapInput]::SendMessage($h, [MapInput]::WM_KEYDOWN, [IntPtr](VK $main), [IntPtr]0) | Out-Null
    [MapInput]::SendMessage($h, [MapInput]::WM_KEYUP, [IntPtr](VK $main), [IntPtr]0) | Out-Null
    foreach ($m in $mods) { [MapInput]::SendMessage($h, [MapInput]::WM_KEYUP, [IntPtr](VK $m), [IntPtr]0) | Out-Null }
    "key $Key"
  }
}
