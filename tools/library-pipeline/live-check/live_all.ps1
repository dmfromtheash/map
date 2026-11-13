# Live pass of every poured hundred (owner 13.11 «уже»): isolated test Map on the laptop display, one catalog per Map
# process, live_batch2.ps1 per batch (UIA + window messages, the cursor is not moved), window rectangle logged every
# second (x >= 0 = primary display = violation). Skips batches that already passed live. Profile deleted at the end.
#   powershell -NoProfile -ExecutionPolicy Bypass -File live_all.ps1 [-Catalogs a,b] [-Place 10]
param([string[]]$Catalogs = @(), [int]$Place = 10)
$ErrorActionPreference = 'Continue'
$Lib = 'H:\MapLibrary'; $Sol7 = "$Lib\sol\sol7"; $Checks = "$Lib\checks"
$Log = "$Checks\live\live_all.log"; $Rects = "$Checks\live\window-rects.log"
New-Item -ItemType Directory -Force "$Checks\live" | Out-Null
function Say($t) { "$(Get-Date -f HH:mm:ss) $t" | Out-File $Log -Append -Encoding utf8 }
$Catalogs = @($Catalogs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Catalogs) { $Catalogs = @(Get-ChildItem $Checks -Directory | Where-Object Name -ne 'live' | ForEach-Object Name) }

# Batches that already passed live: summary.json Passed=true for (catalog, batch).
$done = @{}
Get-ChildItem "$Sol7\live" -Directory -EA 0 | ForEach-Object {
  $s = Join-Path $_.FullName 'summary.json'
  if (Test-Path $s) { try { $j = Get-Content $s -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($j.Passed -and $_.Name -match '^batch-(\d{4})') { $done["$($j.Catalog)|$([int]$Matches[1])"] = 1 } } catch {} }
}
Say "start catalogs=$($Catalogs -join ',') alreadyPassed=$($done.Count)"
# One watcher for the whole run: every visible window of every Map.App started after this script (the owner's own
# Map, already running, is excluded), once per second.
$ownerPids = @(Get-Process Map.App -EA 0 | ForEach-Object Id)
$watch = Start-Job -ArgumentList (,$ownerPids), $Rects, $Log -ScriptBlock {
  param($skip, $rects, $log)
  Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LJ { public delegate bool P(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 public struct R { public int L, T, Rt, B; } }
'@
  while ($true) {
    $ids = @(Get-Process Map.App -EA 0 | Where-Object { $skip -notcontains $_.Id } | ForEach-Object Id)
    if ($ids) {
      [LJ]::EnumWindows({ param($h, $l) $p = 0; [void][LJ]::GetWindowThreadProcessId($h, [ref]$p)
        if ($ids -contains [int]$p -and [LJ]::IsWindowVisible($h)) { $r = New-Object LJ+R; [void][LJ]::GetWindowRect($h, [ref]$r)
          "$(Get-Date -f HH:mm:ss.fff) pid $p rect $($r.L),$($r.T),$($r.Rt),$($r.B)" | Out-File $rects -Append -Encoding utf8
          if ($r.L -ge 0) { "$(Get-Date -f HH:mm:ss.fff) MAIN-DISPLAY pid $p $($r.L),$($r.T)" | Out-File $log -Append -Encoding utf8 } }; $true }, [IntPtr]::Zero) | Out-Null
    }
    Start-Sleep -Milliseconds 250
  }
}
# start_test_map's Map inherits the script's stdout: reading it through a pipe would wait for Map to exit (13.11 hang).
# Output goes to a file through cmd, and only the starter process is awaited.
function Start-TestMap {
  $o = Join-Path $env:TEMP ('map-live-start-' + [guid]::NewGuid().ToString('N') + '.txt')
  $p = Start-Process cmd -ArgumentList '/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$Lib\pipeline\start_test_map.ps1`" > `"$o`" 2>&1" -WindowStyle Hidden -PassThru
  $p.WaitForExit()
  $id = 0; [void][int]::TryParse(((Get-Content $o -EA 0 | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1) -as [string]), [ref]$id)
  Remove-Item $o -Force -EA 0
  return $id
}
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LR { public delegate bool P(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd); }
'@
# A minimized test window is rejected by live_batch2: restore it without taking focus (SW_SHOWNOACTIVATE = 4 keeps
# the maximized laptop-display placement it had).
function Restore-TestMap([int]$id) {
  [LR]::EnumWindows({ param($h, $l) $p = 0; [void][LR]::GetWindowThreadProcessId($h, [ref]$p)
    if ($p -eq $id -and [LR]::IsIconic($h)) { [void][LR]::ShowWindow($h, 4); Say "restored minimized test window pid $id" }; $true }, [IntPtr]::Zero) | Out-Null
}
$total = 0; $passed = 0; $failed = @()
foreach ($cat in $Catalogs) {
  $batches = @(Get-ChildItem "$Checks\$cat" -Filter 'batch-*.json' -EA 0 | Sort-Object Name | Where-Object {
    $r = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json; $r.Files -gt 0 } |
    ForEach-Object { [int]($_.BaseName -replace 'batch-', '') } | Where-Object { -not $done["$cat|$_"] })
  if (-not $batches) { Say "$cat nothing to do"; continue }
  $mapPid = Start-TestMap
  Say "$cat Map pid $mapPid batches $($batches.Count)"
  foreach ($b in $batches) {
    if (-not $mapPid -or -not (Get-Process -Id $mapPid -EA 0)) { Say "$cat Map exited, restart"; $mapPid = Start-TestMap }
    Restore-TestMap $mapPid
    $t = Get-Date
    $out = powershell -NoProfile -ExecutionPolicy Bypass -File "$Sol7\live_batch2.ps1" -ProcessId $mapPid -Batch $b -Catalog $cat -Place $Place 2>&1
    $code = $LASTEXITCODE
    $total++
    $run = Get-ChildItem "$Sol7\live" -Directory | Where-Object Name -like ('batch-{0:D4}-*' -f $b) | Sort-Object LastWriteTime | Select-Object -Last 1
    $why = ''
    if ($run -and (Test-Path "$($run.FullName)\summary.json")) { $j = Get-Content "$($run.FullName)\summary.json" -Raw -Encoding UTF8 | ConvertFrom-Json; $why = (@($j.Errors) -join '; ') }
    if ($code -eq 0) { $passed++ } else { $failed += "$cat#$b" }
    Say ("{0} batch {1} exit {2} {3}s {4} {5}" -f $cat, $b, $code, [int]((Get-Date) - $t).TotalSeconds, $run.Name, $why.Substring(0, [Math]::Min(200, $why.Length)))
  }
  Stop-Process -Id $mapPid -Force -EA 0
  Start-Sleep 2
}
Stop-Job $watch; Remove-Job $watch -Force
Remove-Item (Join-Path $env:TEMP 'map-live-profile') -Recurse -Force -EA 0
Remove-Item (Join-Path $env:TEMP 'map-live-start-*.txt') -Force -EA 0
$main = @(Get-Content $Log -EA 0 | Select-String 'MAIN-DISPLAY').Count
Say "DONE total $total passed $passed failed $($failed.Count) [$($failed -join ' ')] main-display-lines $main"
