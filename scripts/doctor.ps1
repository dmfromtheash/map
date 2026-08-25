[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$expectedSdk = '10.0.400'
$dotnetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$failed = $false

function Write-Check {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Details
    )

    $label = if ($Ok) { 'OK' } else { 'FAIL' }
    $color = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}: {2}" -f $label, $Name, $Details) -ForegroundColor $color
    if (-not $Ok) { $script:failed = $true }
}

Write-Host 'Map environment check' -ForegroundColor Cyan

$dotnetExists = Test-Path -LiteralPath $dotnetPath -PathType Leaf
Write-Check '.NET executable' $dotnetExists $dotnetPath

if ($dotnetExists) {
    $sdks = & $dotnetPath --list-sdks 2>&1
    $hasExpectedSdk = [bool]($sdks | Select-String -SimpleMatch $expectedSdk)
    Write-Check '.NET SDK' $hasExpectedSdk (($sdks | Out-String).Trim())

    $templates = & $dotnetPath new list avalonia 2>&1
    $hasAvalonia = [bool]($templates | Select-String -Pattern 'avalonia\.(app|mvvm)')
    Write-Check 'Avalonia templates' $hasAvalonia 'avalonia.app and avalonia.mvvm expected'
}

foreach ($commandName in @('git', 'rg', 'claude')) {
    $command = Get-Command $commandName -ErrorAction SilentlyContinue
    Write-Check $commandName ([bool]$command) $(if ($command) { $command.Source } else { 'not found in PATH' })
}

$requiredDocs = @(
    'AGENTS.md',
    'CLAUDE.md',
    'docs\00_START_HERE.md',
    'docs\06_DEVELOPMENT_ROADMAP.md',
    'docs\07_OPUS_RUNBOOK.md',
    'docs\STATE_HANDOFF.md'
)

foreach ($relativePath in $requiredDocs) {
    $projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $fullPath = Join-Path $projectRoot $relativePath
    Write-Check $relativePath (Test-Path -LiteralPath $fullPath -PathType Leaf) $fullPath
}

if ($failed) {
    Write-Host 'Environment check failed.' -ForegroundColor Red
    exit 1
}

Write-Host 'Environment is ready for an approved technical spike.' -ForegroundColor Green
exit 0
