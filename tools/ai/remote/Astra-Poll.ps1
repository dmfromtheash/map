# One poll of the Astra mailbox on the USER laptop (used by a Monitor loop in Claude's session).
# Prints one line: H=<newest OUTBOX header>|R=<PresentMon request exists>|P=<running Astra codex exec count>|E=<last dbg.log line>
# ASCII only: Windows PowerShell 5.1 reads script files without BOM as ANSI.
& C:\Users\Leshe\.map-remote\r.ps1 -Script @'
$h = (Select-String -Path D:\MapChecks\astra\OUTBOX.md -Pattern '^## ' -Encoding UTF8 | Select-Object -First 1).Line
$r = Test-Path D:\MapChecks\astra\presentmon-request.json
$p = @(Get-CimInstance Win32_Process -Filter "Name='codex.exe'" | Where-Object { $_.CommandLine -match 'ignore-user-config|exec resume' }).Count
$e = (Get-Content D:\MapChecks\astra\dbg.log -Tail 1)
"H=$h|R=$r|P=$p|E=$e"
'@
