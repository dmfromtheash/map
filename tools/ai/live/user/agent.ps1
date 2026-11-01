# Live helper for the audit laptop USER (DEC-083). Runs in user1's desktop session (start it with a one-shot
# scheduled task, LogonType Interactive - see README.md). Executes C:\MapAudit\live\cmd\*.ps1 one by one with
# helpers.ps1 loaded and writes each output to res\<name>.txt. Stops when C:\MapAudit\live\stop exists.
$ErrorActionPreference = 'Continue'
$root = 'C:\MapAudit\live'
foreach ($d in 'cmd', 'res', 'shots') { New-Item -ItemType Directory -Force "$root\$d" | Out-Null }
. "$root\helpers.ps1"
"agent started $(Get-Date -f s) pid=$PID" | Out-File "$root\agent.log" -Encoding utf8
while (-not (Test-Path "$root\stop")) {
  $next = Get-ChildItem "$root\cmd" -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
  if (-not $next) { Start-Sleep -Milliseconds 250; continue }
  $name = $next.BaseName
  $code = Get-Content $next.FullName -Raw -Encoding utf8
  Remove-Item $next.FullName -Force
  try { $out = . ([scriptblock]::Create($code)) 2>&1 | Out-String -Width 220 }
  catch { $out = "ERROR: " + $_.Exception.Message + "`n" + $_.ScriptStackTrace }
  $out | Out-File "$root\res\$name.txt" -Encoding utf8
}
"agent stopped $(Get-Date -f s)" | Out-File "$root\agent.log" -Append -Encoding utf8
Remove-Item "$root\stop" -Force -ErrorAction SilentlyContinue
