# DEC-116 source/diff guard. ASCII; Windows PowerShell 5.1 and pwsh, no packages.
# Baseline entries: exact finding key | nonempty review reason. No wildcard exceptions.
# -Base compares that commit with the working tree (including uncommitted tracked edits).
# A nonempty test-change: explanation in the commit range permits diff weakening only;
# source violations still require defect: reasons or individually named baseline entries.
# Exit 0 = clean, 1 = findings, 2 = invalid input/tool failure. -SelfTest uses red/green fixtures.
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$Base = '',
    [switch]$UpdateBaseline,
    [string]$BaselineReason = '',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
try {
    if (-not ('MapTestGuards.Guard' -as [type])) {
        $compile = @{ Path = (Join-Path $PSScriptRoot 'Test-GuardSupport.cs') }
        if ($PSVersionTable.PSEdition -ne 'Core') { $compile.ReferencedAssemblies = @('System.dll', 'System.Core.dll', 'System.Xml.dll') }
        Add-Type @compile
    }
    if ($SelfTest) {
        [MapTestGuards.Guard]::SelfTest((Join-Path $PSScriptRoot 'test-guard-cases.xml'))
        exit 0
    }
    $Repo = (Resolve-Path -LiteralPath $Repo).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $baselinePath = Join-Path $Repo 'tools/ai/test-integrity-baseline.txt'
    $paths = New-Object System.Collections.Generic.List[string]
    $texts = New-Object System.Collections.Generic.List[string]
    $testRoot = Join-Path $Repo 'tests'
    if (-not (Test-Path -LiteralPath $testRoot -PathType Container)) { throw "Missing tests directory: $testRoot" }
    foreach ($file in (Get-ChildItem -LiteralPath $testRoot -Recurse -Filter *.cs -File | Sort-Object FullName)) {
        if ($file.FullName -match '[/\\](bin|obj)[/\\]') { continue }
        $paths.Add($file.FullName.Substring($Repo.Length + 1).Replace('\', '/'))
        $texts.Add([IO.File]::ReadAllText($file.FullName))
    }
    if ($paths.Count -eq 0) { throw 'No C# test sources found' }
    $silent = @([MapTestGuards.Guard]::ScanSources($paths.ToArray(), $texts.ToArray()))
    $current = @($silent | ForEach-Object { $_.Key } | Sort-Object -Unique)
    $known = [MapTestGuards.Guard]::ReadBaseline('')
    $findings = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $baselinePath) {
        $known = [MapTestGuards.Guard]::ReadBaseline([IO.File]::ReadAllText($baselinePath))
    }
    if ($UpdateBaseline) {
        if (-not $BaselineReason.Trim() -or $BaselineReason -match '[\r\n]') { throw '-UpdateBaseline requires a nonempty, single-line -BaselineReason after owner review' }
        $entries = @('# Exact finding key | review reason (DEC-116). No wildcard entries.')
        $entries += @($current | ForEach-Object {
            $reason = $BaselineReason
            if ($known.ContainsKey($_)) { $reason = $known[$_] }
            "$_ | $reason"
        })
        [IO.File]::WriteAllText($baselinePath, (($entries -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
        "baseline written: $($current.Count) named findings -> $baselinePath"
        exit 0
    }
    foreach ($s in $silent) {
        if (-not $known.ContainsKey($s.Key)) { $findings.Add("NEW $s") }
        else { "KNOWN $s | $($known[$s.Key])" }
    }
    $gone = @($known.Keys | Where-Object { $current -notcontains $_ })
    "source findings: $($silent.Count) occurrences, $($current.Count) named keys (baseline $($known.Count), stale $($gone.Count))"
    if (-not $Base) {
        $Base = ((& git -C $Repo merge-base HEAD origin/main 2>$null) -join '').Trim()
        if ($LASTEXITCODE -ne 0 -or -not $Base) { throw 'Cannot determine diff base; supply -Base explicitly' }
    }
    & git -C $Repo rev-parse --verify "$Base^{commit}" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Invalid diff base: $Base" }
    $messages = (& git -C $Repo log --format=%B "$Base..HEAD" 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'git log failed' }
    $justified = [MapTestGuards.Guard]::HasJustification($messages)
    $diff = (& git -C $Repo diff --no-ext-diff --no-color --unified=20 $Base -- tests 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
    $weak = @([MapTestGuards.Guard]::ScanDiff($diff))
    "weakened lines since ${Base}: $($weak.Count) (justified by test-change: $justified)"
    foreach ($w in $weak) {
        if ($justified) { "JUSTIFIED $w" } else { $findings.Add("WEAKENED $w") }
    }
    foreach ($f in $findings) { $f }
    if ($findings.Count -gt 0) { "Test-Integrity: FAIL ($($findings.Count))"; exit 1 }
    'Test-Integrity: OK'
    exit 0
}
catch { Write-Error -ErrorAction Continue "Test-Integrity: ERROR: $_"; exit 2 }
