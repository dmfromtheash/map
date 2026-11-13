param([Parameter(Mandatory=$true)][string]$Profile, [string]$RunId = '', [switch]$ZoomedOut)
$ErrorActionPreference = 'Stop'
if (!$RunId) { $RunId = $Profile }
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Unsafe RunId' }
$otherMap = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID -and $_.ProcessName -in @('Map.App','Map.Performance') })
if ($otherMap.Count) { throw ('BLOCKED: another Map process is running: ' + (($otherMap | ForEach-Object { $_.ProcessName + ' PID=' + $_.Id }) -join ', ')) }
$root = 'D:\MapChecks'
$runDir = Join-Path $root "runs\$RunId"
$profileDir = Join-Path $root "profiles\$RunId"
New-Item -ItemType Directory -Path $runDir,$profileDir -Force | Out-Null
$exe = 'C:\Projects\Map\tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe'
$report = Join-Path $runDir 'report.json'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $exe
$start.Arguments = '--profile ' + $Profile + ' --output ' + $report
$start.WorkingDirectory = 'C:\Projects\Map'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.EnvironmentVariables['MAP_UI_TEST_MODE'] = '1'
$start.EnvironmentVariables['MAP_UI_TEST_PROFILE_ROOT'] = $profileDir
$start.EnvironmentVariables['MAP_UI_TEST_RECOVERY_ROOT'] = Join-Path $profileDir 'recovery'
$start.EnvironmentVariables['MAP_UI_TEST_SHORTCUT_SETTINGS_PATH'] = Join-Path $profileDir 'shortcuts.json'
if ($Profile -in @('native-lights','native-atlas','native-iso','native-planet','native-regional-roles','native-land-materials')) {
    $env:MAP_FRAME_TRACE = Join-Path $runDir 'frames.tsv'
} else { Remove-Item Env:\MAP_FRAME_TRACE -ErrorAction SilentlyContinue }
if ($ZoomedOut) { $start.EnvironmentVariables['MAP_R09_ZOOMED_OUT'] = '1' }
foreach ($name in @('MAP_UI_TEST_MODE','MAP_UI_TEST_PROFILE_ROOT','MAP_UI_TEST_RECOVERY_ROOT','MAP_UI_TEST_SHORTCUT_SETTINGS_PATH')) { [Environment]::SetEnvironmentVariable($name,$start.EnvironmentVariables[$name]) }
if ($ZoomedOut) { $env:MAP_R09_ZOOMED_OUT = '1' }
$env:TEMP = $profileDir
$env:TMP = $profileDir
$stdoutPath = Join-Path $runDir 'stdout.txt'
$stderrPath = Join-Path $runDir 'stderr.txt'
$began = Get-Date
$proc = Start-Process -FilePath $exe -ArgumentList $start.Arguments -WorkingDirectory $start.WorkingDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$ownedHandle = $proc.Handle
$samples = [Collections.Generic.List[object]]::new()
$timedOut = $false
$stoppedAfterException = $false
while (!$proc.HasExited) {
    $proc.Refresh()
    $samples.Add([pscustomobject]@{elapsed_s=[math]::Round(((Get-Date)-$began).TotalSeconds,2); working_set_mib=[math]::Round($proc.WorkingSet64/1MB,2); private_mib=[math]::Round($proc.PrivateMemorySize64/1MB,2); cpu_s=$proc.TotalProcessorTime.TotalSeconds})
    if (((Get-Date)-$began).TotalSeconds -gt 5 -and (Test-Path -LiteralPath $stderrPath) -and (Select-String -LiteralPath $stderrPath -Pattern 'Unhandled exception.' -Quiet)) { $stoppedAfterException=$true; $proc.Kill(); break }
    if (((Get-Date)-$began).TotalMinutes -gt 20) { $timedOut=$true; $proc.Kill(); break }
    Start-Sleep -Seconds 1
}
$proc.WaitForExit()
$errorText = Get-Content -LiteralPath $stderrPath -Raw -Encoding UTF8
if (!$errorText) { $errorText = '' }
$samples | Export-Csv -LiteralPath (Join-Path $runDir 'memory.csv') -NoTypeInformation -Encoding UTF8
$meta = [ordered]@{profile=$Profile; run_id=$RunId; command=($exe+' '+$start.Arguments); started=$began.ToString('o'); duration_s=[math]::Round(((Get-Date)-$began).TotalSeconds,3); pid=$proc.Id; exit_code=$proc.ExitCode; timeout=$timedOut; stopped_after_unhandled_exception=$stoppedAfterException; report=$report; samples=$samples.Count; peak_working_set_mib=($samples | Measure-Object working_set_mib -Maximum).Maximum; peak_private_mib=($samples | Measure-Object private_mib -Maximum).Maximum; zoomed_out=[bool]$ZoomedOut}
$meta | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDir 'run-meta.json') -Encoding UTF8
$json = $null
if (Test-Path -LiteralPath $report) { $json = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json }
$status = if ($null -ne $json -and $json.GatePassed) { 'FACT' } elseif ($null -ne $json) { 'PARTIAL' } else { 'BLOCKED' }
$entry = [ordered]@{status=$status; report=$(if ($json) {$report} else {$null}); meta=(Join-Path $runDir 'run-meta.json'); exit_code=$proc.ExitCode; duration_s=$meta.duration_s; peak_working_set_mib=$meta.peak_working_set_mib; peak_private_mib=$meta.peak_private_mib; gate=$(if ($json) {$json.GatePassed} else {$null}); error=$errorText.Trim(); checks=$(if ($json) {$json.Checks} else {$null}); measurements=$json}
$summaryPath = Join-Path $root 'summary.json'
$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
$summary.profiles | Add-Member -MemberType NoteProperty -Name $RunId -Value $entry -Force
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$errorLine = ($errorText -split '\r?\n' | Select-Object -First 1)
if ($json -and $json.Failure) { $errorLine = ($json.Failure -split '\r?\n' | Select-Object -First 1) }
$checkSummary = if ($json.Checks) { $cs=@($json.Checks.PSObject.Properties); ' checks=' + @($cs | Where-Object {$_.Value -eq $true}).Count + '/' + $cs.Count } else { '' }
$row = '| ' + $RunId + ' | `' + $meta.command + '` | ' + $status + ' | exit=' + $proc.ExitCode + '; ' + $meta.duration_s + ' s; peak WS ' + $meta.peak_working_set_mib + ' MiB; private ' + $meta.peak_private_mib + ' MiB. ' + $errorLine + ' |'
Add-Content -LiteralPath (Join-Path $root 'REPORT.md') -Value "`r`n$row`r`n" -Encoding UTF8
Add-Content -LiteralPath (Join-Path $root 'REPORT.md') -Value ('gate=' + $entry.gate + $checkSummary + '; cwd=C:\Projects\Map; comparison: see final tables.') -Encoding UTF8
$resolvedProfile = [IO.Path]::GetFullPath($profileDir)
if (!$resolvedProfile.StartsWith('D:\MapChecks\profiles\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path' }
Remove-Item -LiteralPath $resolvedProfile -Recurse -Force
foreach ($localProfile in @(Get-ChildItem -LiteralPath $runDir -Directory -Recurse | Where-Object {$_.Name -eq 'profile'})) {
    $checkedPath = [IO.Path]::GetFullPath($localProfile.FullName)
    if (!$checkedPath.StartsWith($runDir + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe nested profile cleanup' }
    Remove-Item -LiteralPath $checkedPath -Recurse -Force
}
$meta | ConvertTo-Json -Depth 10
if (Test-Path -LiteralPath $report) {
    $json = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    [pscustomobject]@{gate=$json.GatePassed; failure=$json.Failure; checks=$json.Checks; notes=$json.Notes} | ConvertTo-Json -Depth 10
}
