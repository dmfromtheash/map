[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$mapRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
foreach ($relative in @('AGENTS.md', 'CLAUDE.md', 'Map.slnx', 'docs/STATE.md')) {
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
# docs/STATE.md is the only source of the current state (DEC-054); the journal is history.
$stateLines = @(Get-Content -LiteralPath (Join-Path $mapRoot 'docs/STATE.md') -Encoding UTF8)
$state = $stateLines -join "`n"
$updated = [regex]::Match($state, '(?m)^\*\*Обновлено:\*\*\s*(.+)$').Groups[1].Value.Trim()
$nextAction = [regex]::Match($state, '(?ms)^## Следующий шаг\s*\n(.*?)(?=^## |\z)').Groups[1].Value.Trim()
$warnings = @()
if ($stateLines.Count -gt 60) { $warnings += "docs/STATE.md has $($stateLines.Count) lines (limit 60): rewrite it." }
if (-not $nextAction) { $warnings += 'docs/STATE.md has no "## Следующий шаг" section.' }
if ($mapRoot -like 'C:\Projects\Map*') { $warnings += 'C:\Projects\Map is the stale checkout: work in the product worktree from docs/STATE.md.' }
$commands = @('git', 'rg', 'dotnet', 'powershell', 'claude', 'codex', 'gh') | ForEach-Object {
    $command = Get-Command $_ -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{ name = $_; available = $null -ne $command }
}
$context = [pscustomobject]@{
    root = $mapRoot
    branch = [string](Read-MapGit @('branch', '--show-current'))
    head = [string](Read-MapGit @('rev-parse', 'HEAD'))
    changes = @(Read-MapGit @('status', '--porcelain=v1', '--untracked-files=all'))
    recentHistory = @(Read-MapGit @('log', '--all', '-6', '--oneline'))
    stateUpdated = $updated
    nextAction = $nextAction
    warnings = @($warnings)
    tools = @($commands)
}
if ($AsJson) { $context | ConvertTo-Json -Depth 5 } else { $context }
