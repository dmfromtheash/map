# Invoke-TestProject.ps1 — runs one test project in CI the honest way (DEC-086, replaces Test-KnownFailures.ps1).
#
#  1. dotnet test with a .trx report. A report with 0 tests is red (the project ran nothing — DEC-054).
#  2. Failures listed in tests/KnownFailures.txt do not count (R00, DEC-052; the list is empty since 2026-10-21).
#  3. If 1–3 other tests failed, they are run once more by name. A test that passes on the retry is a
#     timing-dependent test on a busy runner: a ::warning:: names it (to be fixed), the job stays green.
#     Failing again, or more than 3 failures, is a real regression — red.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Invoke-TestProject.ps1 -Project Map.Core.Tests
#   (-RunSettings 'xUnit.ParallelizeTestCollections=false' passes MSBuild-style test settings after "--")
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Project,
    [string] $ResultsDir = 'artifacts/test-results',
    [string] $KnownList = 'tests/KnownFailures.txt',
    [string[]] $RunSettings = @(),
    [int] $MaxRetried = 3
)

$ErrorActionPreference = 'Stop'
$csproj = "tests/$Project/$Project.csproj"
$extra = @()
if ($RunSettings.Count -gt 0) { $extra = @('--') + $RunSettings }

function Read-Trx([string] $path) {
    [xml] $doc = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $names = @{}
    foreach ($def in $doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $m = $def.SelectSingleNode('t:TestMethod', $ns)
        if ($null -ne $m) { $names[$def.GetAttribute('id')] = $m.GetAttribute('className') + '.' + $m.GetAttribute('name') }
    }
    $total = 0
    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($r in $doc.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        $total++
        if ($r.GetAttribute('outcome') -eq 'Failed') {
            $id = $r.GetAttribute('testId')
            $failed.Add($(if ($names.ContainsKey($id)) { $names[$id] } else { $r.GetAttribute('testName') }))
        }
    }
    return @{ Total = $total; Failed = @($failed | Sort-Object -Unique) }
}

$known = @()
if (Test-Path -LiteralPath $KnownList) {
    $known = @(Get-Content -LiteralPath $KnownList -Encoding UTF8 | ForEach-Object { ($_ -replace '#.*$', '').Trim() } | Where-Object { $_ })
}
function Test-IsKnown([string] $name) {
    foreach ($p in $known) {
        if ($p.EndsWith('*')) { if ($name.StartsWith($p.TrimEnd('*'), [StringComparison]::Ordinal)) { return $true } }
        elseif ($name -eq $p) { return $true }
    }
    return $false
}

dotnet test $csproj -c Release -nologo --logger "trx;LogFileName=$Project.trx" --results-directory $ResultsDir @extra
$trx = Join-Path $ResultsDir "$Project.trx"
if (-not (Test-Path -LiteralPath $trx)) { Write-Host "::error::$Project did not produce a test report"; exit 1 }
$run = Read-Trx $trx
if ($run.Total -eq 0) { Write-Host "::error::$Project ran 0 tests"; exit 1 }

$new = @($run.Failed | Where-Object { -not (Test-IsKnown $_) })
foreach ($n in @($run.Failed | Where-Object { Test-IsKnown $_ })) { Write-Host "  known failure: $n" }
Write-Host "$Project : $($run.Total) tests, $($run.Failed.Count) failed, $($new.Count) not in the known list"
if ($new.Count -eq 0) { exit 0 }
if ($new.Count -gt $MaxRetried) {
    foreach ($n in $new) { Write-Host "::error::$Project failing test: $n" }
    exit 1
}

# Retry by method name; a theory's "(args)" is cut so the filter matches every case of it.
$filter = ($new | ForEach-Object { 'FullyQualifiedName~' + ($_ -replace '\(.*$', '') }) -join '|'
$retryDir = Join-Path $ResultsDir 'retry'
Write-Host "Retrying once: $filter"
dotnet test $csproj -c Release -nologo --no-build --filter $filter --logger "trx;LogFileName=$Project.trx" --results-directory $retryDir @extra
$retryTrx = Join-Path $retryDir "$Project.trx"
if (-not (Test-Path -LiteralPath $retryTrx)) { Write-Host "::error::$Project retry produced no report"; exit 1 }
$retry = Read-Trx $retryTrx
if ($retry.Total -eq 0 -or $retry.Failed.Count -gt 0) {
    foreach ($n in $new) { Write-Host "::error::$Project failing test (failed twice): $n" }
    exit 1
}

foreach ($n in $new) { Write-Host "::warning::timing-dependent test failed once and passed on retry - fix it: $n" }
exit 0
