# Compares the repository copy of the library pipeline with the deployed copies on H:\MapLibrary and on the Mac
# (~/MapWork) by MD5. Read-only: nothing is copied, started or stopped. Prints one line per file: same / DIFF / missing.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\library-pipeline\Compare-Deployed.ps1 [-Library H:\MapLibrary] [-NoMac]
param(
    [string]$Library = 'H:\MapLibrary',
    [string]$MacHost = 'user@192.168.31.25',
    [switch]$NoMac
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# repository path -> deployed path (H: relative to -Library, Mac relative to ~)
$pc = [ordered]@{
    'upscale\finish_asset.py'    = 'pipeline\finish_asset.py'
    'upscale\audit_blocks.py'    = 'pipeline\audit_blocks.py'
    'upscale\sheet.py'           = 'pipeline\sheet.py'
    'upscale\start_upscale.cmd'  = 'pipeline\start_upscale.cmd'
    'upscale\run_upscale2.py'    = 'sol\sol1\run_upscale2.py'
    'upscale\progress.py'        = 'sol\sol1\progress.py'
    'upscale\rerun_outliers.py'  = 'sol\sol1\rerun_outliers.py'
    'upscale\test_pipeline.py'   = 'sol\sol1\test_pipeline.py'
    'mac\mac_feeder.py'          = 'pipeline\mac_feeder.py'
    'mac\start_mac_feeder.cmd'   = 'pipeline\start_mac_feeder.cmd'
    'mac\mac_worker.py'          = 'pipeline\mac_worker.py'
    'mac\bench.py'               = 'pipeline\mac\bench.py'
    'plan\plan.py'               = 'pipeline\plan.py'
    'plan\apply_fixes.py'        = 'pipeline\apply_fixes.py'
    'plan\make_lists.py'         = 'pipeline\make_lists.py'
    'plan\quality.py'            = 'pipeline\quality.py'
    'plan\look_sheets.py'        = 'pipeline\look_sheets.py'
    'live-check\live_batch2.ps1' = 'sol\sol7\live_batch2.ps1'
    'live-check\LiveBatchNative.cs' = 'sol\sol7\LiveBatchNative.cs'
    'live-check\shot_target.ps1' = 'sol\sol7\shot_target.ps1'
    'live-check\uia.ps1'         = 'sol\sol7\uia.ps1'
    'live-check\click.ps1'       = 'sol\sol7\click.ps1'
    'live-check\README.md'       = 'sol\sol7\README.md'
    'live-check\start_test_map.ps1' = 'pipeline\start_test_map.ps1'
    'monitor\watch_mac.sh'       = 'pipeline\mac\watch_mac.sh'
    'agents\run_agent.sh'        = 'pipeline\mac\run_agent.sh'
    'agents\stage.py'            = 'pipeline\mac\stage.py'
    'agents\COMMON.md'           = 'pipeline\mac\COMMON.md'
}
$mac = [ordered]@{
    'upscale\finish_asset.py' = 'MapWork/upscale/finish_asset.py'
    'mac\mac_worker.py'       = 'MapWork/upscale/mac_worker.py'
    'mac\jobs.sh'             = 'MapWork/upscale/jobs.sh'
    'mac\env.sh'              = 'MapWork/env.sh'
    'mac\setup.sh'            = 'MapWork/setup.sh'
    'agents\run_agent.sh'     = 'MapWork/agents/run_agent.sh'
}

function Md5([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm MD5).Hash.ToLowerInvariant() }

foreach ($k in $pc.Keys) {
    $target = Join-Path $Library $pc[$k]
    if (-not (Test-Path -LiteralPath $target)) { "missing  PC  $k -> $target"; continue }
    $state = if ((Md5 (Join-Path $root $k)) -eq (Md5 $target)) { 'same' } else { 'DIFF' }
    '{0,-8} PC  {1} -> {2}' -f $state, $k, $target
}
if ($NoMac) { return }
$list = ($mac.Values | ForEach-Object { "~/$_" }) -join ' '
$remote = @(ssh -o BatchMode=yes -o ConnectTimeout=10 $MacHost "md5 -r $list 2>/dev/null" 2>$null)
$byPath = @{}
foreach ($line in $remote) { $p = $line -split ' ', 2; if ($p.Count -eq 2) { $byPath[$p[1].Trim()] = $p[0] } }
foreach ($k in $mac.Keys) {
    $hit = $byPath.Keys | Where-Object { $_.EndsWith('/' + $mac[$k]) } | Select-Object -First 1
    if (-not $hit) { "missing  Mac $k -> ~/$($mac[$k])"; continue }
    $state = if ((Md5 (Join-Path $root $k)) -eq $byPath[$hit]) { 'same' } else { 'DIFF' }
    '{0,-8} Mac {1} -> ~/{2}' -f $state, $k, $mac[$k]
}
