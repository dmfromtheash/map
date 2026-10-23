# Shows files changed outside the stage card's file list (docs/00_STAGE_PROTOCOL.md, step 9; DEC-054).
# Warns only; -Strict returns exit code 1 when something is outside the list.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Check-Scope.ps1 -Allowed 'src/Map.Export/','docs/journal/' [-Base <commit>]
# -Allowed: path prefixes or wildcards relative to the repository root ('src/Map.Core/Geometry/', '*.md').
# -Base: also count files committed since this commit (for a stage spread over several commits).
# Journal, STATE, PROGRESS and CHANGELOG are always allowed: every stage updates them.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Allowed,
    [string]$Base = '',
    [string]$ProjectRoot = '',
    [switch]$Strict
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
function Read-Git([string[]]$GitArguments) {
    $output = @(& git -C $root -c core.quotepath=false @GitArguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($output -join ' ')" }
    return $output
}
# powershell -File passes 'a','b' as one string "a,b": split it back into rules.
$Allowed = @($Allowed | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$always = @('docs/journal/', 'docs/STATE.md', 'docs/ROADMAP_2026-10_PROGRESS.md', 'CHANGELOG.md')
$changed = New-Object System.Collections.Generic.SortedSet[string]
foreach ($line in Read-Git @('status', '--porcelain=v1', '--untracked-files=all')) {
    $path = ([string]$line).Substring(3)
    if ($path -match ' -> ') { $path = ($path -split ' -> ')[1] }
    [void]$changed.Add($path.Trim('"'))
}
if ($Base) {
    foreach ($line in Read-Git @('diff', '--name-only', "$Base..HEAD")) { [void]$changed.Add([string]$line) }
}
function Test-Allowed([string]$Path) {
    foreach ($rule in @($Allowed) + $always) {
        $normal = $rule.Replace('\', '/')
        if ($normal.Contains('*') -or $normal.Contains('?')) { if ($Path -like $normal) { return $true } }
        elseif ($Path.StartsWith($normal, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
$outside = @($changed | Where-Object { -not (Test-Allowed $_) })
[pscustomobject]@{
    changed = $changed.Count
    outside = $outside
    status = if ($outside.Count -eq 0) { 'IN SCOPE' } else { 'OUTSIDE SCOPE: remove from the commit or explain in the report' }
} | ConvertTo-Json -Depth 3
if ($Strict -and $outside.Count -gt 0) { exit 1 }
