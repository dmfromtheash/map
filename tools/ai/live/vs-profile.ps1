# Profiles a Map.Performance smoke scenario with the Visual Studio 2026 profiler (DEC-051) from the
# command line, without opening Visual Studio. The scenario runs in its own window with an isolated
# test profile; the profiler attaches right after start and the .diagsession is written when it ends:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/live/vs-profile.ps1 `
#       -Scenario native-paint-perf -Agent CpuUsageHigh -Out artifacts\vs-profile-2026-10-19\hex `
#       -Vars "MAP_HEX_STRESS=1;MAP_PAINT_W=2560;MAP_PAINT_H=1369"
# -Agent is a file name from ...\Team Tools\DiagnosticsHub\Collector\AgentConfigs (CpuUsageHigh,
# CpuUsageBase, DotNetObjectAllocLow, DotNetCountersBase, ...). Open the .diagsession in Visual Studio
# (File > Open) for hot paths; the CPU agent's .etl inside (-Expand) also opens in WPA or the ETW tools.
# Build first: dotnet build tools/Map.Performance -c Release (it is not in Map.slnx).
param(
    [string]$Scenario = "native-paint-perf",
    [string]$Agent = "CpuUsageHigh",
    [string]$Out = "",
    [string]$Vars = "",
    [switch]$Expand,
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$collector = 'C:\Program Files\Microsoft Visual Studio\18\Community\Team Tools\DiagnosticsHub\Collector'
$diag = Join-Path $collector 'VSDiagnostics.exe'
if (-not (Test-Path $diag)) { throw "Visual Studio profiler is not installed: $diag" }
$config = Join-Path $collector "AgentConfigs\$Agent.json"
if (-not (Test-Path $config)) { throw "No agent config $config" }
$exe = "$root\tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe"
if (-not (Test-Path $exe)) { throw "No Map.Performance build at $exe" }
if ($Out -eq "") { $Out = "$root\artifacts\vs-profile-" + (Get-Date -Format 'yyyy-MM-dd-HHmm') }
$Out = [IO.Path]::GetFullPath($Out)
$profile = "$Out\profile"
New-Item -ItemType Directory -Force "$profile\Recovery" | Out-Null

$env:MAP_UI_TEST_MODE = '1'
$env:MAP_UI_TEST_PROFILE_ROOT = $profile
$env:MAP_UI_TEST_RECOVERY_ROOT = "$profile\Recovery"
$env:MAP_UI_TEST_SHORTCUT_SETTINGS_PATH = "$profile\shortcuts.json"
foreach ($pair in ($Vars -split ';')) {
    if ($pair -match '^\s*([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1].Trim())" -Value $Matches[2] }
}

$session = Get-Random -Minimum 10 -Maximum 250
$report = "$Out\$Scenario.json"
$started = Get-Date
$p = Start-Process -FilePath $exe -ArgumentList "--profile $Scenario --output `"$report`"" -PassThru `
    -RedirectStandardOutput "$Out\stdout.txt" -RedirectStandardError "$Out\stderr.txt"
$null = $p.Handle # keeps ExitCode readable after the process ends
& $diag start $session "/attach:$($p.Id)" "/loadConfig:$config" "/scratchLocation:$Out" | Out-Host
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; Write-Warning "timeout" }
$seconds = [int]((Get-Date) - $started).TotalSeconds
$session_file = "$Out\$Scenario-$Agent.diagsession"
& $diag stop $session "/output:$session_file" | Out-Host
if ($Expand) { & $diag expandDiagSession $session_file | Out-Host }
"exit=$($p.ExitCode) seconds=$seconds report=$report diagsession=$session_file"
