# Runs PresentMon on USER from Claude's elevated ssh session when Astra drops
# D:\MapChecks\astra\presentmon-request.json {"tag","pid","seconds","out","allProcesses"}.
# Writes "PresentMon IDET (running)" to the top of Astra's INBOX, waits, prints "PRESENTMON DONE tag=... lines=N".
# Notes (2026-11-14): Map presents through DirectComposition by default, so a capture by PID shows nothing -
# use allProcesses (dwm.exe presents) or LowLatencyDxgiSwapChain mode (Map presents itself). ASCII only.
& C:\Users\Leshe\.map-remote\r.ps1 -Script @'
$f = 'D:\MapChecks\astra\presentmon-request.json'
if (-not (Test-Path $f)) { 'no-request'; return }
$req = Get-Content $f -Raw | ConvertFrom-Json
$out = [string]$req.out
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$err = Join-Path (Split-Path $out) 'presentmon-error.log'
$args2 = @('--output_file', $out, '--qpc_time', '--timed', [string]$req.seconds, '--terminate_after_timed', '--no_console_stats')
if (-not $req.allProcesses) { $args2 = @('--process_id', [string]$req.pid) + $args2 }
$p = Start-Process -FilePath D:\MapChecks\tools\PresentMon\PresentMon.exe -PassThru -NoNewWindow -ArgumentList $args2 -RedirectStandardError $err
Start-Sleep -Seconds 2
$inbox = Get-Content D:\MapChecks\astra\INBOX.md -Raw -Encoding UTF8
$i = $inbox.IndexOf("`n")
$note = "`n## " + (Get-Date -Format 'HH:mm') + " USER Claude - PresentMon IDET (running): tag $($req.tag), pid $($req.pid), all=$([bool]$req.allProcesses), $($req.seconds) s, out $out. Start the route now (+10 s).`n`n"
[IO.File]::WriteAllText('D:\MapChecks\astra\INBOX.md', $inbox.Substring(0,$i+1) + $note + $inbox.Substring($i+1), (New-Object Text.UTF8Encoding $false))
Rename-Item $f ("presentmon-request." + $req.tag + ".started.json")
$p.WaitForExit()
"PRESENTMON DONE tag=$($req.tag) lines=" + ((Get-Content $out -ErrorAction SilentlyContinue | Measure-Object -Line).Lines)
'@
