# Live FPS runs of Astra's branch on the PC (RTX 3070, 144 Hz laptop display), windows only on the non-primary display.
param([string[]]$Cases = @('standard:100:standard-cold', 'standard:100:standard-warm', 'medium:100000:medium-cold', 'medium:100000:medium-warm'))
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$S = Join-Path $env:TEMP 'map-fps\pclive'
New-Item -ItemType Directory -Force "$S\profiles" | Out-Null
$log = "$S\live.log"
Set-Location C:\Projects\Map
# Wait until the library pour (same working folder, main-branch build) has finished: its last log line is DONE and no pour process runs.
while ((Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'pour_all.ps1' }) -or -not ((Get-Content 'H:\MapLibrary\pour.log' -Tail 1 -EA 0) -match '^DONE')) { Start-Sleep 20 }
"start $(Get-Date -f HH:mm:ss)" | Out-File $log -Encoding utf8
git switch -q review/astra-fps-1m 2>&1 | Out-File $log -Append -Encoding utf8
"commit $(git rev-parse --short HEAD)" | Out-File $log -Append -Encoding utf8
$b = dotnet build tools\Map.Performance\Map.Performance.csproj -c Release --nologo -v q 2>&1
($b | Select-String 'Build succeeded| error ' | Select-Object -Last 3) | Out-File $log -Append -Encoding utf8
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class W { public delegate bool P(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 public struct R { public int L, T, Rt, B; } }
'@
foreach ($c in $Cases) {
  $case, $count, $label = $c -split ':'
  if (-not $label) { $label = $case }
  $run = "$S\run-$label"; New-Item -ItemType Directory -Force $run | Out-Null
  $env:MAP_DRIVE_LIBRARY_ROOTS = 'H:\MapLibrary'; $env:MAP_PERF_PROFILE_ROOT = "$S\profiles"; $env:MAP_PERF_SHOTS = "$run\shots"
  $env:MAP_MILLION_CASE = $case; $env:MAP_MEASURE_GPU_FLUSH = '1'; $env:MAP_FRAME_TRACE = "$run\frames.tsv"
  $env:MAP_PAINT_W = '2048'; $env:MAP_PAINT_H = '1095'
  # Test mode puts every Map window on the non-primary (laptop) display, full screen (TestWindows.cs, owner 2026-10-28).
  $env:MAP_UI_TEST_MODE = '1'
  # Derived assets ON (as in the product): one shared root for all runs, so the first run of a case is cold, the next warm.
  $env:MAP_PERF_DERIVED_ROOT = "$S\derived"
  $t = Get-Date
  $proc = Start-Process .\tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe -ArgumentList '--profile', 'native-million-assets', '--object-count', $count, '--output', "$run\report.json" -RedirectStandardOutput "$run\console.log" -RedirectStandardError "$run\console.err" -WindowStyle Hidden -PassThru
  # Window watcher in this process: every visible top window of the run, once per second (x < 0 = laptop display).
  while (-not $proc.HasExited) {
    $script:seen = @()
    [W]::EnumWindows({ param($h, $l) $p = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$p)
      if ($p -eq $proc.Id -and [W]::IsWindowVisible($h)) { $r = New-Object W+R; [void][W]::GetWindowRect($h, [ref]$r)
        "$(Get-Date -f HH:mm:ss) $case rect $($r.L),$($r.T),$($r.Rt),$($r.B)" | Out-File "$S\window-rects.log" -Append -Encoding utf8
        if ($r.L -ge 0) { "MAIN-DISPLAY $(Get-Date -f HH:mm:ss) $case $($r.L),$($r.T)" | Out-File $log -Append -Encoding utf8 } }; $true }, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1000
  }
  "$case $count exit $($proc.ExitCode) $([int]((Get-Date)-$t).TotalSeconds) s" | Out-File $log -Append -Encoding utf8
  Get-Content "$run\console.log" -Tail 6 | Out-File $log -Append -Encoding utf8
}
$rects = @(Get-Content "$S\window-rects.log" -EA 0)
"windows seen: $(@($rects).Count), on main display (x>=0): $(@($rects | ? { ($_ -split 'rect ')[1] -match '^(\d+),' }).Count)" | Out-File $log -Append -Encoding utf8
Get-ChildItem "$S\profiles" -Directory -EA 0 | Remove-Item -Recurse -Force
git switch -q codex/stage-18-editable-areas 2>&1 | Out-File $log -Append -Encoding utf8
"DONE $(Get-Date -f HH:mm:ss) branch $(git branch --show-current)" | Out-File $log -Append -Encoding utf8
