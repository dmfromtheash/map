<#
.SYNOPSIS
Drives the spike window with real Win32 input and captures what it actually shows.

.DESCRIPTION
This is the manual-smoke harness. It launches the built spike executable, sends real keyboard and
mouse input to the real window through user32, and saves PrintWindow captures. It exists so a
"manual UI check" produces artefacts a reviewer can look at, not a claim.

It does NOT assert anything. Read the captured PNGs and the printed process facts.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\spike\tools\drive-window.ps1 -OutDir .\artifacts\smoke
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than in the param defaults: under Windows PowerShell 5.1 launched with
# -File, $PSScriptRoot is not yet populated when parameter defaults are evaluated.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) {
    $Exe = Join-Path $scriptRoot '..\src\Map.Spike.App\bin\Release\net10.0\Map.Spike.App.exe'
}
if (-not $OutDir) {
    $OutDir = Join-Path $scriptRoot '..\..\artifacts\spike-smoke'
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class Win32Input
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public const uint MOUSEEVENTF_WHEEL    = 0x0800;
    public const uint KEYEVENTF_KEYUP      = 0x0002;
    public const byte VK_CONTROL           = 0x11;
}
"@

# Windows refuses SetForegroundWindow from a process that is not already in the foreground.
# Attaching our input queue to the current foreground thread lifts that restriction, which is
# what makes SendKeys actually land on the spike window instead of the console.
function Set-ActiveWindow([IntPtr]$hwnd) {
    $foreground = [Win32Input]::GetForegroundWindow()
    $foregroundThread = [Win32Input]::GetWindowThreadProcessId($foreground, [ref]([uint32]0))
    $ourThread = [Win32Input]::GetCurrentThreadId()

    [void][Win32Input]::AttachThreadInput($ourThread, $foregroundThread, $true)
    [void][Win32Input]::BringWindowToTop($hwnd)
    [void][Win32Input]::SetForegroundWindow($hwnd)
    [void][Win32Input]::AttachThreadInput($ourThread, $foregroundThread, $false)
    Start-Sleep -Milliseconds 250
}

function Assert-Active([System.Diagnostics.Process]$proc) {
    Set-ActiveWindow $proc.MainWindowHandle
    $fg = [Win32Input]::GetForegroundWindow()
    $fgPid = [uint32]0
    [void][Win32Input]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    if ($fgPid -ne $proc.Id) {
        throw "The spike window is not in the foreground (foreground pid $fgPid, expected $($proc.Id)). Input would go elsewhere."
    }
}

function Get-ClientOrigin([IntPtr]$hwnd) {
    $p = New-Object Win32Input+POINT
    $p.X = 0; $p.Y = 0
    [void][Win32Input]::ClientToScreen($hwnd, [ref]$p)
    return $p
}

function Save-WindowCapture([System.Diagnostics.Process]$proc, [string]$path) {
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
    $rect = New-Object Win32Input+RECT
    [void][Win32Input]::GetWindowRect($hwnd, [ref]$rect)

    $width  = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    # PrintWindow with PW_RENDERFULLCONTENT (2) captures GPU-composited client areas that a plain
    # BitBlt of the desktop would miss.
    $hdc = $graphics.GetHdc()
    $ok = [Win32Input]::PrintWindow($hwnd, $hdc, 2)
    $graphics.ReleaseHdc($hdc)

    if (-not $ok) {
        # Fall back to grabbing the screen region the window occupies.
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    }

    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "captured -> $path"
}

$script:DrivenProcess = $null

# Virtual-key codes are layout independent. SendKeys is not: it resolves a character through
# VkKeyScan for the ACTIVE keyboard layout, so on a non-Latin layout (this machine runs a Russian
# layout) every latin letter is silently dropped while digits still work. Driving raw VKs is the
# only way this harness produces the same input regardless of what layout the user has selected.
$script:VirtualKeys = @{
    'd1' = 0x31; 'd2' = 0x32; 'd3' = 0x33
    'e'  = 0x45; 'f' = 0x46; 'm' = 0x4D; 'o' = 0x4F
    'r'  = 0x52; 's' = 0x53; 'y' = 0x59; 'z' = 0x5A
    'f5' = 0x74
}

function Send-Key([string]$name, [switch]$Control, [int]$settleMs = 500) {
    Assert-Active $script:DrivenProcess

    if (-not $script:VirtualKeys.ContainsKey($name)) {
        throw "Unknown key '$name'."
    }

    $vk = [byte]$script:VirtualKeys[$name]

    if ($Control) { [Win32Input]::keybd_event([Win32Input]::VK_CONTROL, 0, 0, [IntPtr]::Zero) }
    [Win32Input]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [Win32Input]::keybd_event($vk, 0, [Win32Input]::KEYEVENTF_KEYUP, [IntPtr]::Zero)
    if ($Control) { [Win32Input]::keybd_event([Win32Input]::VK_CONTROL, 0, [Win32Input]::KEYEVENTF_KEYUP, [IntPtr]::Zero) }

    Start-Sleep -Milliseconds $settleMs
}

function Invoke-Drag([System.Diagnostics.Process]$proc, [int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 24) {
    Assert-Active $proc
    $origin = Get-ClientOrigin $proc.MainWindowHandle
    [void][Win32Input]::SetCursorPos(($origin.X + $x1), ($origin.Y + $y1))
    Start-Sleep -Milliseconds 120
    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)

    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps
        $x = [int]($x1 + (($x2 - $x1) * $t))
        $y = [int]($y1 + (($y2 - $y1) * $t))
        [void][Win32Input]::SetCursorPos(($origin.X + $x), ($origin.Y + $y))
        Start-Sleep -Milliseconds 16
    }

    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Invoke-Click([System.Diagnostics.Process]$proc, [int]$x, [int]$y) {
    Assert-Active $proc
    $origin = Get-ClientOrigin $proc.MainWindowHandle
    [void][Win32Input]::SetCursorPos(($origin.X + $x), ($origin.Y + $y))
    Start-Sleep -Milliseconds 120
    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Invoke-Wheel([System.Diagnostics.Process]$proc, [int]$x, [int]$y, [int]$notches) {
    Assert-Active $proc
    $origin = Get-ClientOrigin $proc.MainWindowHandle
    [void][Win32Input]::SetCursorPos(($origin.X + $x), ($origin.Y + $y))
    Start-Sleep -Milliseconds 120
    $step = if ($notches -ge 0) { 120 } else { -120 }
    for ($i = 0; $i -lt [math]::Abs($notches); $i++) {
        [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_WHEEL, 0, 0, [uint32]$step, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 80
    }
    Start-Sleep -Milliseconds 300
}

# ---------------------------------------------------------------------------- run

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$proc = Start-Process -FilePath $Exe -PassThru
Write-Host "started pid $($proc.Id)"

for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}

if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "The window never appeared." }

$script:DrivenProcess = $proc

[void][Win32Input]::ShowWindow($proc.MainWindowHandle, 3)   # maximize
Start-Sleep -Seconds 2
Assert-Active $proc
Start-Sleep -Seconds 1

$client = New-Object Win32Input+RECT
[void][Win32Input]::GetClientRect($proc.MainWindowHandle, [ref]$client)
$cw = $client.Right; $ch = $client.Bottom
Write-Host "client area: ${cw}x${ch}"

# Refit after maximising: the window was 1400x900 when it first opened.
Send-Key "z" -settleMs 700
Save-WindowCapture $proc (Join-Path $OutDir "01-startup-10k-objects.png")

Write-Host "== zoom at cursor =="
Invoke-Wheel $proc ([int]($cw * 0.35)) ([int]($ch * 0.4)) 6
Save-WindowCapture $proc (Join-Path $OutDir "02-zoomed-in.png")

Write-Host "== select an object =="
Send-Key "d1" -settleMs 400
Invoke-Click $proc ([int]($cw * 0.5)) ([int]($ch * 0.45))
Save-WindowCapture $proc (Join-Path $OutDir "03-selection.png")

# The brush and the mask deliberately follow the SAME screen path. If they did not overlap, a
# capture could not show whether the mask actually removes brush pixels.
$strokeX1 = [int]($cw * 0.20); $strokeY1 = [int]($ch * 0.30)
$strokeX2 = [int]($cw * 0.62); $strokeY2 = [int]($ch * 0.62)

Write-Host "== brush stroke =="
Send-Key "d2" -settleMs 400
Invoke-Drag $proc $strokeX1 $strokeY1 $strokeX2 $strokeY2
Save-WindowCapture $proc (Join-Path $OutDir "04-brush-stroke.png")

Write-Host "== undo, then redo =="
Send-Key "z" -Control -settleMs 700
Save-WindowCapture $proc (Join-Path $OutDir "05-after-undo.png")
Send-Key "y" -Control -settleMs 700
Save-WindowCapture $proc (Join-Path $OutDir "06-after-redo.png")

Write-Host "== mask erase over the middle of the same stroke, red overlay on =="
Send-Key "d3" -settleMs 400
Invoke-Drag $proc ([int](($strokeX1 + $strokeX2) / 2 - $cw * 0.10)) ([int](($strokeY1 + $strokeY2) / 2 - $ch * 0.076)) ([int](($strokeX1 + $strokeX2) / 2 + $cw * 0.10)) ([int](($strokeY1 + $strokeY2) / 2 + $ch * 0.076))
Save-WindowCapture $proc (Join-Path $OutDir "07-mask-overlay-on.png")

Write-Host "== overlay off, mask still applied =="
Send-Key "o" -settleMs 600
Save-WindowCapture $proc (Join-Path $OutDir "08-mask-overlay-off.png")

Write-Host "== mask disabled: the erased brush pixels must come back =="
Send-Key "m" -settleMs 600
Save-WindowCapture $proc (Join-Path $OutDir "09-mask-disabled.png")
Send-Key "m" -settleMs 600

Write-Host "== save =="
Send-Key "s" -Control -settleMs 3000
Save-WindowCapture $proc (Join-Path $OutDir "10-after-save.png")

Write-Host "== export 4K png =="
Send-Key "e" -settleMs 8000
Save-WindowCapture $proc (Join-Path $OutDir "11-after-export.png")

Write-Host "== reopen from disk =="
Send-Key "r" -Control -settleMs 4000
Send-Key "z" -settleMs 900
Save-WindowCapture $proc (Join-Path $OutDir "12-after-reopen.png")

Write-Host "== frame benchmark =="
Send-Key "f" -settleMs 1000
Start-Sleep -Seconds 25
Save-WindowCapture $proc (Join-Path $OutDir "13-frame-benchmark.png")

$proc.Refresh()
Write-Host ""
Write-Host "responding: $($proc.Responding)"
Write-Host "working set: $([math]::Round($proc.WorkingSet64 / 1MB, 2)) MiB"
Write-Host "private bytes: $([math]::Round($proc.PrivateMemorySize64 / 1MB, 2)) MiB"

$listening = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -eq $proc.Id }
Write-Host "listening TCP sockets: $(if ($listening) { ($listening | Measure-Object).Count } else { 0 })"

$proc.Kill()
Write-Host "done; artefacts in $OutDir"
