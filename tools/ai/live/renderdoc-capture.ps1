# Captures GPU frames of Map under RenderDoc without touching the keyboard (DEC-050).
# Map starts in an isolated test profile (nothing of the owner's library is read or written),
# opens the given map, and the frame after each listed map frame number is written as .rdc:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/live/renderdoc-capture.ps1 `
#       -Map "C:\path\to\map.mapproj" -Frames "120,400" -Out artifacts\renderdoc-2026-10-19
# Open the .rdc in RenderDoc (qrenderdoc.exe): Event Browser shows every draw call and texture
# upload of the frame, Statistics the totals. Requires RenderDoc (C:\Program Files\RenderDoc).
param(
    [string]$Map = "",
    [string]$Frames = "10",
    [string]$Out = "",
    [string]$Exe = "",
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$renderdoc = 'C:\Program Files\RenderDoc\renderdoccmd.exe'
if (-not (Test-Path $renderdoc)) { throw "RenderDoc is not installed: $renderdoc" }
if ($Exe -eq "") { $Exe = "$root\src\Map.App\bin\Release\net10.0\win-x64\Map.App.exe" }
if (-not (Test-Path $Exe)) { throw "No Map build at $Exe (dotnet build .\Map.slnx -c Release)" }
if ($Out -eq "") { $Out = "$root\artifacts\renderdoc-" + (Get-Date -Format 'yyyy-MM-dd-HHmm') }
$Out = [IO.Path]::GetFullPath($Out)
$profile = "$Out\profile"
New-Item -ItemType Directory -Force "$profile\Maps", "$profile\Recovery" | Out-Null
if ($Map -ne "") {
    $Map = [IO.Path]::GetFullPath($Map)
    if (-not (Test-Path $Map)) { throw "No map at $Map" }
    Copy-Item $Map "$profile\Maps\" -Force
    $Map = Join-Path "$profile\Maps" (Split-Path -Leaf $Map)
}

$env:MAP_UI_TEST_MODE = '1'
$env:MAP_UI_TEST_PROFILE_ROOT = $profile
$env:MAP_UI_TEST_RECOVERY_ROOT = "$profile\Recovery"
$env:MAP_UI_TEST_SHORTCUT_SETTINGS_PATH = "$profile\shortcuts.json"
$env:MAP_RENDERDOC_CAPTURE_FRAMES = $Frames
$env:MAP_RENDERDOC_CAPTURE_PATH = "$Out\map-frame"
$env:MAP_FRAME_TRACE = "$Out\frames.tsv"
# Avalonia draws through ANGLE (OpenGL ES on Direct3D 11): RenderDoc hooks the D3D11 side, not EGL.
$env:RENDERDOC_HOOK_EGL = '0'
# RenderDoc kills the process in Avalonia's default WinUI composition path (Avalonia #15911,
# ICompositorInterop.CreateGraphicsDevice); the DXGI swap chain path captures fine.
$env:MAP_WIN32_COMPOSITION = 'LowLatencyDxgiSwapChain'

$arguments = @('capture', '--wait-for-exit', '--working-dir', (Split-Path -Parent $Exe), '--capture-file', "$Out\map-frame", $Exe)
if ($Map -ne "") { $arguments += $Map }
Write-Host "RenderDoc: $Exe (frames $Frames) -> $Out"
$process = Start-Process -FilePath $renderdoc -ArgumentList $arguments -PassThru -NoNewWindow
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $captures = @(Get-ChildItem "$Out\*.rdc" -ErrorAction SilentlyContinue)
    $wanted = ($Frames -split '[,; ]+' | Where-Object { $_ -ne '' }).Count
    if ($captures.Count -ge $wanted -and $Map -ne "") {
        # Every frame is captured: close Map (it has no unsaved work in the test profile).
        Start-Sleep -Seconds 2
        Get-Process Map.App -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $process.StartTime } | Stop-Process -Force
    }
}
if (-not $process.HasExited) {
    Get-Process Map.App -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $process.StartTime } | Stop-Process -Force
    Start-Sleep -Seconds 1
    if (-not $process.HasExited) { $process.Kill() }
}
$captures = @(Get-ChildItem "$Out\*.rdc" -ErrorAction SilentlyContinue)
foreach ($c in $captures) { Write-Host ("captured {0} ({1:N1} MB)" -f $c.Name, ($c.Length / 1MB)) }
if ($captures.Count -eq 0) { Write-Error "No .rdc written: Map rendered fewer frames than $Frames, or RenderDoc did not hook the process."; exit 2 }
Write-Host "Open with: `"C:\Program Files\RenderDoc\qrenderdoc.exe`" `"$($captures[0].FullName)`""
