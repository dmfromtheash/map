param([string]$Action = "rect", [int]$X = 100, [int]$Y = 80, [int]$W = 1600, [int]$H = 1000, [string]$Path = "", [string]$Title = "Map")
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class MapWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  public static string Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
    using (var bmp = new Bitmap(w, hh, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      bool ok = PrintWindow(h, dc, 2);
      g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
      return ok + " " + w + "x" + hh;
    }
  }
}
"@ -ReferencedAssemblies System.Drawing
if (-not ([System.Management.Automation.PSTypeName]"MapFindTitled").Type) { Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class MapFindTitled {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static IntPtr Find(uint pid, string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString().StartsWith(prefix)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
}
$proc = Get-Process Map.App -ErrorAction Stop | Select-Object -First 1
$h = [MapFindTitled]::Find([uint32]$proc.Id, $Title)
if ($h -eq [IntPtr]::Zero) { throw "Map window not found" }
switch ($Action) {
  "rect" { $r = New-Object MapWin+RECT; [MapWin]::GetWindowRect($h, [ref]$r) | Out-Null; $c = New-Object MapWin+RECT; [MapWin]::GetClientRect($h, [ref]$c) | Out-Null; $p = New-Object MapWin+POINT; [MapWin]::ClientToScreen($h, [ref]$p) | Out-Null; "pid=$($proc.Id) window=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom) client=$($p.X),$($p.Y) size=$($c.Right)x$($c.Bottom)" }
  "move" { [MapWin]::ShowWindow($h, 9) | Out-Null; [MapWin]::SetWindowPos($h, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0040) | Out-Null; [MapWin]::SetForegroundWindow($h) | Out-Null; "moved" }
  "front" { [MapWin]::SetForegroundWindow($h) | Out-Null; "front" }
  "topmost" { [MapWin]::ShowWindow($h, 9) | Out-Null; [MapWin]::SetWindowPos($h, [IntPtr](-1), $X, $Y, $W, $H, 0x0040) | Out-Null; "topmost" }
  "normal" { [MapWin]::SetWindowPos($h, [IntPtr](-2), $X, $Y, $W, $H, 0x0040) | Out-Null; "normal" }
  "shot" { [MapWin]::Shot($h, $Path) }
}
