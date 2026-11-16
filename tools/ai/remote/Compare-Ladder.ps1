# Regression gate for the ladders (owner 2026-11-16: "чтобы каждый день мы не делали то, что должны были решить вчера").
# Compares a current ladder.csv against an accepted baseline row by row (same objects, same mode, same route; use fixed seeds
# so the maps are the same) and writes a Markdown table "baseline -> current" with a verdict per row. Exit code 1 when any
# row is worse than the thresholds or a baseline route is missing (a crashed or skipped phase). ASCII only (PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Baseline,
    [Parameter(Mandatory = $true)][string]$Current,
    # Suffix of the tag that marks the mode (cold/warm); rows are matched as <mode>|objects|route.
    [string[]]$Modes = @('cold', 'warm'),
    [string]$Out = '',
    [double]$FpsDrop = 0.15,        # route FPS may fall at most 15 %
    [double]$LowDrop = 0.25,        # 1 % low may fall at most 25 % (medians of repeats; a single run moves 15-20 %)
    [double]$EditFpsDrop = 0.30,    # an edit route has 10 frames: its FPS is coarse
    [double]$WorkRise = 0.25,       # frame / edit work p95 may rise at most 25 % ...
    [double]$WorkFloorMs = 3,       # ... and only frame differences above this many ms count (noise)
    [double]$EditFloorMs = 15,      # ... and only edit differences above this many ms count
    [double]$SharpRise = 0.5,       # time to sharp may rise at most 50 % ...
    [double]$SharpFloorS = 0.5,     # ... and at least this many seconds
    [double]$OpenRise = 0.25        # reopen ready time may rise at most 25 %
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
function Num($s) { $v = 0.0; if ([double]::TryParse([string]$s, [Globalization.NumberStyles]::Float, $inv, [ref]$v)) { $v } else { $null } }
# Repeat runs of a key (same mode/objects/route) are folded into one row of MEDIANS: a single run's 1 % low moves by
# 15-20 % and a 2-15 ms edit by a few ms (dry run 2026-11-16, cold vs warm of one build and seed: 10 "worse" rows of noise).
function Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    if ($v.Count % 2) { $v[[int][math]::Floor($v.Count / 2)] } else { ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2 }
}
function Load([string]$path) {
    $groups = @{}
    foreach ($r in (Import-Csv $path)) {
        if ($r.route -match '(^|/)fit$') { continue }   # a single still frame, not a route
        $mode = ($Modes | Where-Object { $r.tag -like "*$_*" } | Select-Object -First 1)
        if (-not $mode) { $mode = 'any' }
        $key = "$mode|$($r.objects)|$($r.route)"
        if (-not $groups.ContainsKey($key)) { $groups[$key] = New-Object System.Collections.Generic.List[object] }
        $groups[$key].Add($r)
    }
    $rows = @{}
    foreach ($key in $groups.Keys) {
        $g = $groups[$key]
        $rows[$key] = [pscustomobject]@{
            fps = Median ($g | ForEach-Object { Num $_.fps }); low1 = Median ($g | ForEach-Object { Num $_.low1 })
            frameWorkP95 = Median ($g | ForEach-Object { Num $_.frameWorkP95 })
            sharpSeconds = Median ($g | ForEach-Object { Num ($_.sharpSeconds -replace '\+', '') })
            sharpLimit = [bool]($g | Where-Object { "$($_.sharpSeconds)".EndsWith('+') })
            openReadySeconds = Median ($g | ForEach-Object { Num $_.openReadySeconds }); runs = $g.Count }
    }
    $rows
}
$base = Load $Baseline; $cur = Load $Current
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('| mode | objects | route | metric | baseline | current | change | verdict |')
$lines.Add('|---|---|---|---|---|---|---|---|')
$fail = 0; $better = 0; $same = 0
function Row($key, $metric, $b, $c, $verdict) {
    $p = $key.Split('|'); $change = if ($b -and $b -ne 0) { '{0:+0;-0}%' -f (($c - $b) / [math]::Abs($b) * 100) } else { '' }
    $lines.Add("| $($p[0]) | $($p[1]) | $($p[2]) | $metric | $b | $c | $change | $verdict |")
}
foreach ($key in ($base.Keys | Sort-Object)) {
    $b = $base[$key]
    if (-not $cur.ContainsKey($key)) { Row $key 'route' 'present' 'MISSING' 'FAIL (crash or skipped phase?)'; $fail++; continue }
    $c = $cur[$key]
    $isEdit = $key -like '*/edit/*'
    foreach ($m in @(
            @{ Name = 'fps'; B = $b.fps; C = $c.fps; Up = $true; Drop = $(if ($isEdit) { $EditFpsDrop } else { $FpsDrop }) },
            @{ Name = 'low1'; B = $b.low1; C = $c.low1; Up = $true; Drop = $LowDrop },
            @{ Name = $(if ($isEdit) { 'editWork' } else { 'frameWorkP95' }); B = $b.frameWorkP95; C = $c.frameWorkP95; Up = $false; Floor = $(if ($isEdit) { $EditFloorMs } else { $WorkFloorMs }); Rise = $WorkRise },
            @{ Name = 'sharpSeconds'; B = $b.sharpSeconds; C = $c.sharpSeconds; Up = $false; Floor = $SharpFloorS; Rise = $SharpRise },
            @{ Name = 'openReady'; B = $b.openReadySeconds; C = $c.openReadySeconds; Up = $false; Floor = 0.3; Rise = $OpenRise })) {
        if ($null -eq $m.B -or $null -eq $m.C) { continue }
        if ($m.Up) {
            if ($m.B -gt 0 -and $m.C -lt $m.B * (1 - $m.Drop)) { Row $key $m.Name $m.B $m.C 'FAIL'; $fail++ }
            elseif ($m.C -gt $m.B * (1 + $m.Drop)) { Row $key $m.Name $m.B $m.C 'better'; $better++ }
            else { $same++ }
        } else {
            if ($m.C - $m.B -gt $m.Floor -and $m.C -gt $m.B * (1 + $m.Rise)) { Row $key $m.Name $m.B $m.C 'FAIL'; $fail++ }
            elseif ($m.B - $m.C -gt $m.Floor -and $m.C -lt $m.B * (1 - $m.Rise)) { Row $key $m.Name $m.B $m.C 'better'; $better++ }
            else { $same++ }
        }
    }
    if ($c.sharpLimit -and -not $b.sharpLimit) { Row $key 'sharp' 'complete' 'never sharp (limit)' 'FAIL'; $fail++ }
}
foreach ($key in ($cur.Keys | Where-Object { -not $base.ContainsKey($_) } | Sort-Object)) { Row $key 'route' 'absent' 'new' 'new (no baseline)' }
$verdict = if ($fail -gt 0) { "NOT ACCEPTED: $fail worse rows" } else { 'ACCEPTED: nothing worse than the baseline' }
$head = @("# Ladder gate: $verdict", '', "baseline: $Baseline", "current: $Current", "worse: $fail, better: $better, unchanged metrics: $same", '',
    "Thresholds: FPS/1% low -$([int]($FpsDrop*100))%, work +$([int]($WorkRise*100))% and >$WorkFloorMs ms, sharp +$([int]($SharpRise*100))% and >$SharpFloorS s, reopen +$([int]($OpenRise*100))%.", '')
$text = ($head + $lines) -join "`r`n"
if ($Out) { [IO.File]::WriteAllText($Out, $text, [Text.UTF8Encoding]::new($false)) }
"$verdict (worse $fail, better $better)"
if ($fail -gt 0) { exit 1 } else { exit 0 }
