# Status of the Sol run on USER: session policy, task state, codex.log tail, report size, Map processes, memory.
$f = Get-ChildItem C:\Users\user1\.codex\sessions -Recurse -Filter 'rollout-*.jsonl' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite'); $buf = New-Object byte[] ([Math]::Min(200000, $fs.Length)); [void]$fs.Read($buf, 0, $buf.Length); $fs.Close()
$t = [Text.Encoding]::UTF8.GetString($buf)
foreach ($p in @('"sandbox_policy":\{[^}]{0,160}\}', '"cwd":"[^"]*"', '"model":"[^"]*"', '"effort":"[^"]*"')) { [regex]::Match($t, $p).Value }
"codex.log age {0:N0}s" -f ((Get-Date) - (Get-Item D:\MapChecks\astra\codex.log).LastWriteTime).TotalSeconds
"task: " + (Get-ScheduledTask MapAstra -ErrorAction SilentlyContinue).State
Get-Content D:\MapChecks\astra\run.log -ErrorAction SilentlyContinue
if (Test-Path D:\MapChecks\astra\REPORT.md) { "REPORT.md {0:N0} KB" -f ((Get-Item D:\MapChecks\astra\REPORT.md).Length / 1KB) }
Get-Process Map.App, Map.Performance -ErrorAction SilentlyContinue | ForEach-Object { "{0} {1} {2:N0} MB" -f $_.ProcessName, $_.Id, ($_.WorkingSet64 / 1MB) }
$os = Get-CimInstance Win32_OperatingSystem; "RAM free {0:N1} GB" -f ($os.FreePhysicalMemory / 1MB)
"--- codex.log tail"; Get-Content D:\MapChecks\astra\codex.log -Tail 8 -Encoding UTF8
