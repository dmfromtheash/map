# Test integrity guard (DEC-116). ASCII only (Windows PowerShell 5.1 reads files without BOM as ANSI).
#
# 1. Silent passes in tests/: a test body that returns early on an environment variable, a missing file/folder or the
#    OS, and Skip reasons without "defect:". Known ones are listed in tools/ai/test-integrity-baseline.txt; a NEW one
#    fails. -UpdateBaseline rewrites the list (only after the owner-visible review of the change).
# 2. Weakened tests in the commits since -Base: added Skip, removed Assert lines, larger numbers in lines that carry a
#    budget/threshold/timeout, a test moved to a slow/separate collection. Fails unless a commit message in the range
#    carries "test-change:" with the reason.
# Exit 0 = clean, 1 = findings. Output: one line per finding.
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Base = '',
    [switch]$UpdateBaseline
)
$ErrorActionPreference = 'Stop'
Set-Location $Repo
$baselinePath = Join-Path $Repo 'tools\ai\test-integrity-baseline.txt'
$findings = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- 1. silent passes
$silent = New-Object System.Collections.Generic.List[string]
$early = '^\s*if \(.*(GetEnvironmentVariable|File\.Exists|Directory\.Exists|OperatingSystem\.Is).*\)\s*return\s*;'
$skip = 'Skip\s*=\s*"([^"]*)"'
Get-ChildItem (Join-Path $Repo 'tests') -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
    $rel = $_.FullName.Substring($Repo.Length + 1).Replace('\', '/')
    $lines = [IO.File]::ReadAllLines($_.FullName)
    $method = ''
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*(public|internal|private)\s+(async\s+)?(void|Task)\s+(\w+)\s*\(') { $method = $Matches[4] }
        if ($line -match $early) { $silent.Add("silent-return $rel :: $method") }
        foreach ($m in [regex]::Matches($line, $skip)) {
            if ($m.Groups[1].Value -notmatch '^defect:') { $silent.Add("skip-without-defect $rel :: $method") }
        }
    }
}
$current = $silent | Sort-Object -Unique
if ($UpdateBaseline) {
    [IO.File]::WriteAllLines($baselinePath, [string[]]$current)
    "baseline written: $($current.Count) known silent passes -> $baselinePath"
    exit 0
}
$known = @{}
if (Test-Path $baselinePath) { foreach ($k in [IO.File]::ReadAllLines($baselinePath)) { if ($k) { $known[$k] = $true } } }
foreach ($s in $current) { if (-not $known.ContainsKey($s)) { $findings.Add("NEW $s") } }
$gone = @($known.Keys | Where-Object { $current -notcontains $_ })
"silent passes: $($current.Count) (known $($known.Count), fixed since baseline $($gone.Count))"

# ---------------------------------------------------------------- 2. weakened tests in the range
if (-not $Base) { $Base = (git merge-base HEAD origin/main 2>$null) }
if ($Base) {
    $messages = (git log --format=%B "$Base..HEAD" 2>$null) -join "`n"
    $justified = $messages -match 'test-change:'
    $diff = git diff --unified=0 "$Base" -- tests 2>$null
    $file = ''
    $removed = New-Object System.Collections.Generic.List[string]
    $weak = New-Object System.Collections.Generic.List[string]
    $threshold = '(Assert|Budget|budget|Timeout|TimeSpan|Milliseconds|Seconds|Ratio|ratio|<=|>=|Max|Limit|Within)'
    foreach ($line in $diff) {
        if ($line -like '+++ b/*') { $file = $line.Substring(6); $removed.Clear(); continue }
        if ($line -like '@@*') { $removed.Clear(); continue }
        if ($line -like '-*' -and $line -notlike '---*') {
            $text = $line.Substring(1)
            if ($text -match 'Assert\.' ) { $weak.Add("assert-removed $file :: $($text.Trim())") }
            $removed.Add($text)
            continue
        }
        if ($line -like '+*' -and $line -notlike '+++*') {
            $text = $line.Substring(1)
            if ($text -match $skip -and $Matches[1] -notmatch '^defect:') { $weak.Add("skip-added $file :: $($text.Trim())") }
            if ($text -match '\[Collection\(.*(Slow|Starved|Serial|RealTime)') { $weak.Add("moved-to-collection $file :: $($text.Trim())") }
            if ($text -match $threshold) {
                $shape = [regex]::Replace($text, '\d+(\.\d+)?', '#')
                foreach ($old in $removed) {
                    if ([regex]::Replace($old, '\d+(\.\d+)?', '#') -ne $shape) { continue }
                    $a = [regex]::Matches($old, '\d+(\.\d+)?') | ForEach-Object { [double]$_.Value }
                    $b = [regex]::Matches($text, '\d+(\.\d+)?') | ForEach-Object { [double]$_.Value }
                    for ($k = 0; $k -lt [Math]::Min(@($a).Count, @($b).Count); $k++) {
                        if (@($b)[$k] -gt @($a)[$k]) { $weak.Add("number-raised $file :: $($old.Trim()) -> $($text.Trim())"); break }
                    }
                }
            }
        }
    }
    # An assert moved inside the same change (removed and re-added) is not a removal.
    $added = $diff | Where-Object { $_ -like '+*' } | ForEach-Object { $_.Substring(1).Trim() }
    $weak = $weak | Where-Object { -not ($_ -like 'assert-removed*' -and $added -contains (($_ -split ' :: ', 2)[1])) }
    "weakened lines since $($Base.Substring(0, [Math]::Min(8, $Base.Length))): $(@($weak).Count) (justified by test-change: $justified)"
    if (-not $justified) { foreach ($w in $weak) { $findings.Add("WEAKENED $w") } }
    else { foreach ($w in $weak) { "  (justified) $w" } }
}

foreach ($f in $findings) { $f }
if ($findings.Count -gt 0) { "Test-Integrity: FAIL ($($findings.Count))"; exit 1 }
"Test-Integrity: OK"
exit 0
