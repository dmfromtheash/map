# Blur -> sharp experiment on the owner's PC (2026-11-16): which limit keeps stamps blurry for seconds after a zoom?
# One build, one object count, one warm cache; each variant changes ONE limit through an environment switch (product
# defaults are unchanged). Per route: FPS, 1 % low, frame work p95 and sharpSeconds (Run-Ladder column).
# ASCII only (Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Tree,
    [string]$Base = "$env:TEMP\map-sharp",
    # 100 000: pan/1600 drew 55 stamps in 188 ms (PC 2026-11-16) - 55 full-size originals cannot fit 256 MB of GPU cache.
    [int]$Count = 100000,
    [string[]]$Variants = @('base', 'gpu1536', 'adopt64', 'stamp2048', 'all')
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$switches = 'MAP_GPU_CACHE_MB', 'MAP_STAMP_CACHE_MB', 'MAP_MIP_ADOPT_MB', 'MAP_MIP_GESTURE_ADOPT_MB'
$settings = @{
    'base'      = @{}
    'gpu1536'   = @{ MAP_GPU_CACHE_MB = '1536' }
    'adopt64'   = @{ MAP_MIP_ADOPT_MB = '64'; MAP_MIP_GESTURE_ADOPT_MB = '16' }
    'stamp2048' = @{ MAP_STAMP_CACHE_MB = '2048' }
    'all'       = @{ MAP_GPU_CACHE_MB = '1536'; MAP_MIP_ADOPT_MB = '64'; MAP_MIP_GESTURE_ADOPT_MB = '16'; MAP_STAMP_CACHE_MB = '2048' }
}
# Warm the derived cache once so the first variant does not pay the cold build (same rule as the ladder).
$warm = Join-Path $Base 'runs\warm'
if (-not (Test-Path (Join-Path $warm 'report.progress.json'))) {
    foreach ($n in $switches) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'Run-Ladder.ps1') -Tree $Tree -Base $Base -Tag 'warm' -Counts $Count -Paced | Out-Null
    'warm done'
}
foreach ($v in $Variants) {
    foreach ($n in $switches) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
    foreach ($kv in $settings[$v].GetEnumerator()) { Set-Item "Env:$($kv.Key)" $kv.Value }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'Run-Ladder.ps1') -Tree $Tree -Base $Base -Tag "sharp-$v" -Counts $Count -Paced
}
foreach ($n in $switches) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
