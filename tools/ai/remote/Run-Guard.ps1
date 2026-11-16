# Regression guard (owner 2026-11-16: "tselnoe yadro optimizatsii", every function tested, nothing gets worse unseen).
# Builds a given branch or commit in its own worktree (C:\Projects\Map\.worktrees\guard), runs the build ladder
# (tools/Map.Performance --profile native-build-ladder through Run-BuildLadder.ps1) with FIXED seeds, cold and warm,
# 2 repeats, then compares the result with the accepted baseline (Compare-Ladder.ps1, medians of the repeats) and
# writes the Markdown verdict into -Out. Exit code: 0 accepted, 1 worse than the baseline (or a phase/route missing),
# 2 could not run (build failed, no baseline - then the run is written as baseline-candidate.csv).
# It registers nothing (no scheduled task); start it by hand or from a task the owner approves.
# ASCII only (Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Ref,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Repo = 'C:\Projects\Map',
    [string]$Baseline = '',
    [int[]]$Seeds = @(101, 202),
    [int]$Repeats = 2,
    # Adds the 1 000 000 step (hours).
    [switch]$Full,
    [string]$Library = ''
)
# Native tools write progress to stderr; with 'Stop' Windows PowerShell 5.1 would turn that into errors. Exit codes are checked.
$ErrorActionPreference = 'Continue'
$guard = Join-Path $Repo '.worktrees\guard'
if (-not $Baseline) { $Baseline = Join-Path $Repo 'docs\perf\baseline-build-ladder.csv' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$verdictPath = Join-Path $Out 'verdict.md'
function Finish([int]$code, [string]$text) {
    if (-not (Test-Path $verdictPath) -or $code -eq 2) { [IO.File]::WriteAllText($verdictPath, $text + "`r`n", [Text.UTF8Encoding]::new($false)) }
    "guard: $text (exit $code)"
    exit $code
}

# 1. The ref in its own worktree (the owner's working folder is never switched).
& git -C $Repo fetch --quiet origin 2>$null
if (Test-Path (Join-Path $guard '.git')) {
    & git -C $guard checkout --quiet --detach $Ref
    if ($LASTEXITCODE -ne 0) { & git -C $guard checkout --quiet --detach "origin/$Ref" }
} else {
    & git -C $Repo worktree add --detach $guard $Ref 2>$null
    if ($LASTEXITCODE -ne 0) { & git -C $Repo worktree add --detach $guard "origin/$Ref" }
}
if ($LASTEXITCODE -ne 0) { Finish 2 "# Guard: NOT RUN - ref '$Ref' could not be checked out" }
$commit = (& git -C $guard rev-parse HEAD).Trim()

# 2. Release build of the measuring tool (it references the program's projects, so it builds them too).
$buildLog = Join-Path $Out 'build.log'
& dotnet build (Join-Path $guard 'tools\Map.Performance\Map.Performance.csproj') -c Release -r win-x64 *> $buildLog
if ($LASTEXITCODE -ne 0) { Finish 2 "# Guard: NOT RUN - build of $Ref ($commit) failed, see $buildLog" }

# 3. The ladder: fixed seeds x repeats, cold then warm each time.
$counts = @(10000, 100000); if ($Full) { $counts += 1000000 }
$base = Join-Path $Out 'ladder'
$runner = Join-Path $guard 'tools\ai\remote\Run-BuildLadder.ps1'
$started = Get-Date
foreach ($seed in $Seeds) {
    foreach ($r in 1..([math]::Max(1, $Repeats))) {
        $runArgs = @{ Tree = $guard; Base = $base; Tag = "g$seed-r$r"; Counts = $counts; Mode = 'Both'; Seed = $seed }
        if ($Library) { $runArgs.Library = $Library }
        & $runner @runArgs | Tee-Object -FilePath (Join-Path $Out 'ladder.log') -Append
    }
}
$csv = Join-Path $base 'ladder.csv'
if (-not (Test-Path $csv)) { Finish 2 "# Guard: NOT RUN - the ladder wrote no rows ($commit), see $Out" }

# 4. Verdict against the accepted baseline.
if (-not (Test-Path $Baseline)) {
    Copy-Item $csv (Join-Path $Out 'baseline-candidate.csv') -Force
    Finish 2 "# Guard: NO BASELINE at $Baseline - this run ($commit) is saved as baseline-candidate.csv for the owner to accept"
}
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'tools\ai\remote\Compare-Ladder.ps1') -Baseline $Baseline -Current $csv -Out $verdictPath
$code = $LASTEXITCODE
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
Add-Content -Encoding ascii $verdictPath "`r`nref: $Ref, commit: $commit, seeds: $($Seeds -join ','), repeats: $Repeats, counts: $($counts -join ','), ladder minutes: $minutes"
"guard: $Ref $commit -> exit $code ($minutes min), verdict $verdictPath"
exit $code
