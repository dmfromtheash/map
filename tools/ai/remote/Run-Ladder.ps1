# FPS ladder on the owner's PC: the same build and tool, object count step by step, so it is visible where the
# frame rate starts to fall. Each step is one full live run (Start-PcLiveRun.ps1, window on the laptop display);
# after each step one line goes to stdout and a row per route to ladder.csv in the base folder.
# Owner 2026-11-15: "лесенка постоянная, разбить на грамотные куски, чтобы видеть прогресс и где начинается сбой".
# ASCII only (Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Tree,
    [string]$Base = "$env:TEMP\map-ladder",
    [string]$Tag = (Get-Date -Format 'MMdd-HHmm'),
    [int[]]$Counts = @(10000, 25000, 50000, 100000, 250000, 500000, 1000000, 2000000),
    # Paced: each input as soon as the previous one is on a frame (what the program can deliver); otherwise a fixed
    # 60 Hz input, which beats against a 144 Hz display and cannot show 60 fresh frames even at 0 ms per frame.
    [switch]$Paced
)
$ErrorActionPreference = 'Stop'
if ($Paced) { $env:MAP_PERF_INPUT = 'paced'; $Tag = "$Tag-paced" } else { Remove-Item Env:MAP_PERF_INPUT -ErrorAction SilentlyContinue }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csv = Join-Path $Base 'ladder.csv'
New-Item -ItemType Directory -Force -Path $Base | Out-Null
if (-not (Test-Path $csv)) { 'tag,objects,side,route,fps,low1,frameWorkP95,intervalMedian,openReadySeconds,sharpSeconds' | Set-Content -Encoding ascii $csv }

function Get-Side([int]$n) { if ($n -le 500000) { 32768 } elseif ($n -le 1000000) { 65536 } else { 131072 } }

foreach ($n in $Counts) {
    $side = Get-Side $n
    $step = "$Tag-$n"
    $started = Get-Date
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'Start-PcLiveRun.ps1') -Tag $step -Tree $Tree -Base $Base -Side $side -Count $n | Out-Null
    $run = Join-Path $Base "runs\$step"
    $progress = Join-Path $run 'report.progress.json'
    if (-not (Test-Path $progress)) { "step $n : no report"; continue }
    $report = Get-Content $progress -Raw | ConvertFrom-Json
    $scenario = $report.Scenarios[0]
    $intervals = @()
    $frames = Join-Path $run 'frames.tsv'
    if (Test-Path $frames) {
        $intervals = Get-Content $frames | ForEach-Object { [double](($_ -split "`t")[1]) } | Where-Object { $_ -gt 0 -and $_ -lt 200 } | Sort-Object
    }
    $intervalMedian = if ($intervals.Count) { $intervals[[int]($intervals.Count / 2)] } else { '' }
    $ready = if ($scenario.OpenToExactTerrainMs) { [math]::Round($scenario.OpenToExactTerrainMs / 1000, 1) } else { '' }
    $moving = @()
    foreach ($route in $scenario.Routes) {
        if ($null -eq $route.Fps) { continue }
        $low = if ($null -ne $route.OnePercentLowFps) { [math]::Round($route.OnePercentLowFps, 1) } else { '' }
        $work = if ($null -ne $route.FrameWorkP95) { [math]::Round($route.FrameWorkP95, 1) } else { '' }
        # Time until every stamp in view has its asked mip (blur -> sharp); "+" marks a wait that hit the 30 s limit.
        $sharp = if ($null -ne $route.SharpDelayMs) { [math]::Round($route.SharpDelayMs / 1000, 2).ToString([Globalization.CultureInfo]::InvariantCulture) + $(if ($route.SharpCompleted -eq $false) { '+' } else { '' }) } else { '' }
        "$Tag,$n,$side,$($route.Name),$([math]::Round($route.Fps,1)),$low,$work,$intervalMedian,$ready,$sharp" | Add-Content -Encoding ascii $csv
        if ($route.Name -notlike 'edit/*') { $moving += $route }
    }
    $avg = if ($moving.Count) { [math]::Round(($moving | Measure-Object Fps -Average).Average, 1) } else { 0 }
    $worst = if ($moving.Count) { $moving | Sort-Object Fps | Select-Object -First 1 } else { $null }
    $lowMin = if ($moving.Count) { [math]::Round((($moving | Where-Object { $null -ne $_.OnePercentLowFps }) | Measure-Object OnePercentLowFps -Minimum).Minimum, 1) } else { 0 }
    $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    "step {0,8} objects: move avg {1} FPS, worst route {2} {3} FPS, worst 1% low {4}, frame interval median {5} ms, whole map ready {6} s ({7} min)" -f `
        $n, $avg, $worst.Name, [math]::Round($worst.Fps, 1), $lowMin, $intervalMedian, $ready, $minutes
}
