[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$mapRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
foreach ($relative in @('AGENTS.md', 'CLAUDE.md', 'Map.slnx', 'docs/STATE_HANDOFF.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $mapRoot $relative) -PathType Leaf)) {
        throw "Not a Map project root: missing $relative in $mapRoot"
    }
}
function Read-MapGit([string[]]$GitArguments) {
    $output = @(& git -C $mapRoot @GitArguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($output -join ' ')" }
    return $output
}
$gitRoot = [IO.Path]::GetFullPath([string](Read-MapGit @('rev-parse', '--show-toplevel')))
if (-not [string]::Equals($gitRoot.TrimEnd('\', '/'), $mapRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected directory is not the Git root of Map.'
}
$handoff = Get-Content -LiteralPath (Join-Path $mapRoot 'docs/STATE_HANDOFF.md') -Raw -Encoding UTF8
$current = ($handoff -split '(?m)^## HISTORICAL', 2)[0].Trim()
$heading = [regex]::Match($current, '(?m)^## .+$').Value.Trim()
$nextAction = [regex]::Match($current, '(?m)^Следующее действие:[\s\S]*?(?=\r?\n\s*\r?\n|\z)').Value.Trim()
$commands = @('git', 'rg', 'dotnet', 'pwsh', 'claude') | ForEach-Object {
    $command = Get-Command $_ -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{ name = $_; available = $null -ne $command }
}
$context = [pscustomobject]@{
    root = $mapRoot
    branch = [string](Read-MapGit @('branch', '--show-current'))
    head = [string](Read-MapGit @('rev-parse', 'HEAD'))
    changes = @(Read-MapGit @('status', '--porcelain=v1', '--untracked-files=all'))
    recentHistory = @(Read-MapGit @('log', '--all', '-6', '--oneline'))
    handoffHeading = $heading
    nextAction = $nextAction
    tools = @($commands)
}
if ($AsJson) { $context | ConvertTo-Json -Depth 5 } else { $context }
