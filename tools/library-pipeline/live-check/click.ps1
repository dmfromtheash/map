param([string]$Title = "Map", [int]$X, [int]$Y, [int]$Count = 1, [int]$ProcessId, [string]$Action = "click", [int]$Delta = 120)
# PostMessage input into one window of the isolated Map process, chosen by title prefix; client px (physical).
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class Cw {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public static IntPtr Find(uint pid, string prefix) { IntPtr f = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString().StartsWith(prefix)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
  public static IntPtr L(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
  public static IntPtr S(IntPtr h, int x, int y) { var p = new POINT { X = x, Y = y }; ClientToScreen(h, ref p); return L(p.X, p.Y); }
}
"@
$h = [Cw]::Find([uint32]$ProcessId, $Title)
if ($h -eq [IntPtr]::Zero) { throw "window '$Title' not found" }
if ($Action -eq "wheel") {
  [Cw]::SendMessage($h, 0x0200, [IntPtr]0, [Cw]::L($X, $Y)) | Out-Null
  for ($i = 0; $i -lt $Count; $i++) { [Cw]::SendMessage($h, 0x020A, [IntPtr](($Delta -band 0xFFFF) -shl 16), [Cw]::S($h, $X, $Y)) | Out-Null; Start-Sleep -Milliseconds 40 }
  "wheel x$Count at $X,$Y"; return
}
for ($i = 0; $i -lt $Count; $i++) {
  [Cw]::SendMessage($h, 0x0200, [IntPtr]0, [Cw]::L($X, $Y)) | Out-Null; Start-Sleep -Milliseconds 30
  [Cw]::SendMessage($h, 0x0201, [IntPtr]1, [Cw]::L($X, $Y)) | Out-Null; Start-Sleep -Milliseconds 40
  [Cw]::SendMessage($h, 0x0202, [IntPtr]0, [Cw]::L($X, $Y)) | Out-Null; Start-Sleep -Milliseconds 60
}
"clicked '$Title' $X,$Y x$Count"
