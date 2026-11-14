# Live perf run on the owner's PC (window on the LAPTOP display, full screen - the perf host places it there).
# Same route as Astra's product grid (tools/Map.Performance --profile native-million-assets, giant case).
# Rules (owner, DEC-113): compare two runs only with the same prepared-image cache warmth and a complete overview
# (use tools/Map.PerfCompare); no heavy test runs on the PC while measuring; delete the run folder when done.
# ASCII only (Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Tree = 'C:\Projects\Map',
    [string]$Base = "$env:TEMP\map-pclive",
    [int]$Side = 131072,
    [int]$Count = 2000000,
    [switch]$Trace
)
$ErrorActionPreference = 'Stop'
$run = Join-Path $Base "runs\$Tag"; $shots = Join-Path $Base "shots\$Tag"; $profiles = Join-Path $Base 'profiles'
New-Item -ItemType Directory -Force -Path $run, $shots, $profiles | Out-Null
$env:MAP_DRIVE_LIBRARY_ROOTS = 'H:\MapLibrary'
$env:MAP_PERF_CATALOGS = 'battlemaps-classic;fantasy-battlemaps-1;fantasy-battlemaps-2;fantasy-battlemaps-3;modern-interiors-1;textures'
Remove-Item Env:MAP_PERF_ASSET_SHA_FILE -ErrorAction SilentlyContinue   # the PC library mounts 16 208 assets, not USER's 1 804
$env:MAP_MILLION_FIRST_ONLY = '0'; $env:MAP_PERF_FPS_GRID = '1'; $env:MAP_PERF_PHASE_EVIDENCE = '1'
$env:MAP_MILLION_SIDE = "$Side"; $env:MAP_MILLION_STYLE = 'off'
$env:MAP_PERF_DERIVED_ROOT = Join-Path $Base 'derived-assets'
$env:MAP_PERF_RETAIN_SAMPLES = '1'; $env:MAP_PERF_SETTLE_SECONDS = '180'; $env:MAP_PERF_WAIT_BACKGROUND = '0'
$env:MAP_PERF_PROFILE_ROOT = $profiles; $env:MAP_PERF_SHOTS = $shots; $env:MAP_MILLION_CASE = 'giant'
$env:MAP_FRAME_TRACE = Join-Path $run 'frames.tsv'
foreach ($n in 'MAP_MEASURE_GPU_FLUSH','MAP_PERF_BLUR_REVIEW','MAP_WIN32_COMPOSITION','MAP_MILLION_FUNCTIONS','MAP_PERF_PRESENTMON_GATE','MAP_PERF_GRID_RESUME_EDITS') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
Set-Location $Tree
$exe = '.\tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe'
if ($Trace) {
    & dotnet-trace collect --profile dotnet-sampled-thread-time,gc-collect --format Speedscope --duration 00:00:04:30 --output (Join-Path $run 'cpu.nettrace') --show-child-io -- $exe --profile native-million-assets --object-count $Count --output (Join-Path $run 'report.json') *> (Join-Path $run 'console.log')
} else {
    & $exe --profile native-million-assets --object-count $Count --output (Join-Path $run 'report.json') *> (Join-Path $run 'console.log')
}
"exit=$LASTEXITCODE"
Get-Content (Join-Path $run 'console.log') -Tail 6
Get-ChildItem $profiles -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
