# Build ladder: the editor builds a map step by step like a person does (continents, rivers and lakes, mountains and
# forests in the four placement modes, roads, towns with an interior, areas with rules, labels, decorations, then more
# objects up to the step's count) and measures every phase: edit-command times, camera routes (FPS, 1% low, frame work,
# time until sharp), memory, shots. Profile: tools/Map.Performance --profile native-build-ladder (MATRIX.md).
# Owner 2026-11-16: "zhivoy progon poocheredno zapolnyaet raznymi obyektami ... kak obychno lyudi delayut karty;
# kazhdyy progon raznyy po vizualu; i na PC, i na USER; kak kholodnym, tak i goryachim".
# Each step = one build process (window on the laptop display, like Start-PcLiveRun.ps1) that saves the built map,
# then a second process that opens that saved file and measures the open (time to a sharp fit, routes in the first 60 s).
#   -Mode Cold : empty local derived cache before the build AND before the reopen (a fresh machine / cleared cache).
#   -Mode Warm : the derived cache is kept (the build warms it; the reopen uses it).
#   -Mode Both : Cold then Warm with the same seed; rows tagged ...-cold / ...-warm.
# Rows go to ladder.csv in the base folder (same columns as Run-Ladder.ps1), route = <phase>/<route>, e.g. rivers/wheel/in-100,
# edit commands as <phase>/edit/<command> (fps = commands per second incl. the frame after, frameWorkP95 = command UI work p95 ms).
# Same script on USER from its own clone: -Tree C:\Projects\Map -Library D:\MapLibrary.
# ASCII only (Windows PowerShell 5.1).
param(
    [string]$Tree = 'C:\Projects\Map',
    [string]$Base = "$env:TEMP\map-build-ladder",
    [string]$Tag = (Get-Date -Format 'MMdd-HHmm'),
    [int[]]$Counts = @(10000, 25000, 50000, 100000, 250000, 500000, 1000000, 2000000),
    [ValidateSet('Cold', 'Warm', 'Both')][string]$Mode = 'Both',
    # Same seed = same map; 0 = a new seed per step from the clock (every step looks different). Written to the report.
    [int]$Seed = 0,
    # Library root (PC: H:\MapLibrary, USER: D:\MapLibrary); empty = the first of them that exists.
    [string]$Library = '',
    # Catalogs to mount (semicolon list); empty = the profile's own choice (fantasy-world, fantasy-regional, parchment-world*, modern-interiors-1 when present).
    [string]$Catalogs = '',
    # Keep the saved maps (they can be large); default: deleted after the reopen.
    [switch]$KeepMaps
)
$ErrorActionPreference = 'Stop'
if (-not $Library) { foreach ($c in 'H:\MapLibrary', 'D:\MapLibrary') { if (Test-Path $c) { $Library = $c; break } } }
if (-not $Library -or -not (Test-Path $Library)) { throw "Library not found; pass -Library" }
$csv = Join-Path $Base 'ladder.csv'
$profiles = Join-Path $Base 'profiles'
$derived = Join-Path $Base 'derived-assets'
New-Item -ItemType Directory -Force -Path $Base, $profiles | Out-Null
if (-not (Test-Path $csv)) { 'tag,objects,side,route,fps,low1,frameWorkP95,intervalMedian,openReadySeconds,sharpSeconds' | Set-Content -Encoding ascii $csv }
$exe = Join-Path $Tree 'tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe'
if (-not (Test-Path $exe)) { throw "Build tools\Map.Performance (Release, win-x64) first: $exe" }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Set-RunEnv([string]$run, [string]$shots, [string]$tag) {
    $env:MAP_DRIVE_LIBRARY_ROOTS = $Library
    if ($Catalogs) { $env:MAP_PERF_CATALOGS = $Catalogs } else { Remove-Item Env:MAP_PERF_CATALOGS -ErrorAction SilentlyContinue }
    $env:MAP_PERF_DERIVED_ROOT = $derived
    $env:MAP_PERF_RETAIN_SAMPLES = '1'; $env:MAP_PERF_SETTLE_SECONDS = '60'
    $env:MAP_PERF_PROFILE_ROOT = $profiles; $env:MAP_PERF_SHOTS = $shots; $env:MAP_BUILD_TAG = $tag
    $env:MAP_FRAME_TRACE = Join-Path $run 'frames.tsv'
    foreach ($n in 'MAP_MEASURE_GPU_FLUSH','MAP_WIN32_COMPOSITION','MAP_PERF_INPUT','MAP_BUILD_OPEN','MAP_BUILD_SEED','MAP_BUILD_SIDE','MAP_PERF_ASSET_SHA_FILE') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
}

function Fmt($v, [int]$digits) { if ($null -eq $v) { '' } else { [math]::Round([double]$v, $digits).ToString($inv) } }

function Add-Rows([string]$reportPath, [string]$rowTag, [int]$n, [string]$framesPath) {
    if (-not (Test-Path $reportPath)) { return $null }
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    $side = $report.Environment.'canvas-side'
    $intervalMedian = ''
    if (Test-Path $framesPath) {
        $iv = @(Get-Content $framesPath | ForEach-Object { [double](($_ -split "`t")[1]) } | Where-Object { $_ -gt 0 -and $_ -lt 200 } | Sort-Object)
        if ($iv.Count) { $intervalMedian = Fmt $iv[[int]($iv.Count / 2)] 2 }
    }
    foreach ($scenario in $report.Scenarios) {
        $phase = $scenario.Name
        $ready = if ($scenario.OpenToExactTerrainMs) { Fmt ($scenario.OpenToExactTerrainMs / 1000) 1 } else { '' }
        foreach ($route in $scenario.Routes) {
            # Moving routes, plus the one-step fit / open routes that carry the time until sharp.
            if ($null -eq $route.Fps -and $null -eq $route.SharpDelayMs) { continue }
            $sharp = if ($null -ne $route.SharpDelayMs) { (Fmt ($route.SharpDelayMs / 1000) 2) + $(if ($route.SharpCompleted -eq $false) { '+' } else { '' }) } else { '' }
            "$rowTag,$n,$side,$phase/$($route.Name),$(Fmt $route.Fps 1),$(Fmt $route.OnePercentLowFps 1),$(Fmt $route.FrameWorkP95 1),$intervalMedian,$ready,$sharp" | Add-Content -Encoding ascii $csv
        }
    }
    foreach ($phase in $report.BuildPhases) {
        foreach ($edit in $phase.Edits) {
            if ($edit.Count -eq 0) { continue }
            $perSecond = if ($edit.FrameLatencyMs.Count) { 1000 / (($edit.FrameLatencyMs | Measure-Object -Average).Average) } else { $null }
            "$rowTag,$n,$side,$($phase.Name)/edit/$($edit.Name),$(Fmt $perSecond 1),,$(Fmt $edit.WorkP95 1),,," | Add-Content -Encoding ascii $csv
        }
    }
    return $report
}

$modes = if ($Mode -eq 'Both') { @('Cold', 'Warm') } else { @($Mode) }
foreach ($n in $Counts) {
    $stepSeed = if ($Seed -ne 0) { $Seed } else { [int]((Get-Date).Ticks % 1000000000) }
    foreach ($m in $modes) {
        $kind = $m.ToLowerInvariant()
        $step = "$Tag-$kind-$n"
        $run = Join-Path $Base "runs\$step"; $shots = Join-Path $Base "shots\$step"
        New-Item -ItemType Directory -Force -Path $run, $shots | Out-Null
        $started = Get-Date
        if ($m -eq 'Cold') { Remove-Item $derived -Recurse -Force -ErrorAction SilentlyContinue }
        Set-RunEnv $run $shots "$step-build"
        $map = Join-Path $run 'built.mapproj'
        $env:MAP_BUILD_SAVE = $map
        & $exe --profile native-build-ladder --object-count $n --seed $stepSeed --output (Join-Path $run 'build.json') *> (Join-Path $run 'build.log')
        $buildExit = $LASTEXITCODE
        $report = Add-Rows (Join-Path $run 'build.progress.json') "$Tag-$kind" $n (Join-Path $run 'frames.tsv')
        if ($null -eq $report) { "step $n $kind : build wrote no report (exit $buildExit), see $run\build.log"; continue }
        # Reopen the saved map in a new process.
        if (Test-Path $map) {
            if ($m -eq 'Cold') { Remove-Item $derived -Recurse -Force -ErrorAction SilentlyContinue }
            Set-RunEnv $run $shots "$step-reopen"
            $env:MAP_FRAME_TRACE = Join-Path $run 'frames-reopen.tsv'
            $env:MAP_BUILD_OPEN = $map
            & $exe --profile native-build-ladder --object-count $n --seed $stepSeed --output (Join-Path $run 'reopen.json') *> (Join-Path $run 'reopen.log')
            Remove-Item Env:MAP_BUILD_OPEN -ErrorAction SilentlyContinue
            $reopen = Add-Rows (Join-Path $run 'reopen.progress.json') "$Tag-$kind" $n (Join-Path $run 'frames-reopen.tsv')
            if (-not $KeepMaps) { Remove-Item $map -Force -ErrorAction SilentlyContinue }
        } else { $reopen = $null }
        Remove-Item Env:MAP_BUILD_SAVE -ErrorAction SilentlyContinue
        $moving = @($report.Scenarios | ForEach-Object { $_.Routes } | Where-Object { $null -ne $_.Fps -and $_.Name -like '*/*' })
        $avg = if ($moving.Count) { [math]::Round(($moving | Measure-Object Fps -Average).Average, 1) } else { 0 }
        $worst = if ($moving.Count) { $moving | Sort-Object Fps | Select-Object -First 1 } else { $null }
        $ready = if ($reopen) { [math]::Round($reopen.Scenarios[0].OpenToExactTerrainMs / 1000, 1) } else { '' }
        $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        "step {0,8} {1}: seed {2}, objects {3}, move avg {4} FPS, worst {5} {6} FPS, reopen ready {7} s, gate exit {8} ({9} min)" -f `
            $n, $kind, $stepSeed, $report.Environment.'objects-final', $avg, $worst.Name, $(if ($worst) { [math]::Round($worst.Fps, 1) } else { '' }), $ready, $buildExit, $minutes
    }
}
Get-ChildItem $profiles -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
