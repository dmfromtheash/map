param([Parameter(Mandatory)][string]$Code, [int]$Wait = 60, [string[]]$Fetch = @(), [string]$To = '')
# Runs on DM: sends one command to agent.ps1 on USER, prints its output; -Fetch copies shots\<name>.png to -To.
$name = (Get-Date -Format 'HHmmss_fff')
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Code))
$remote = @"
`$c = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64'))
[IO.File]::WriteAllText('C:\MapAudit\live\cmd\$name.tmp', `$c, (New-Object Text.UTF8Encoding `$true))
Move-Item 'C:\MapAudit\live\cmd\$name.tmp' 'C:\MapAudit\live\cmd\$name.ps1'
`$res = 'C:\MapAudit\live\res\$name.txt'
for (`$i = 0; `$i -lt $($Wait * 4); `$i++) { if (Test-Path `$res) { Start-Sleep -Milliseconds 150; Get-Content `$res -Encoding utf8; Remove-Item `$res; return }; Start-Sleep -Milliseconds 250 }
'TIMEOUT waiting for $name'
"@
& C:\Users\Leshe\.map-remote\r.ps1 $remote 2>$null
foreach ($f in $Fetch) {
  if (-not $To) { throw 'Give -To <local folder> for fetched shots (scratchpad).' }
  New-Item -ItemType Directory -Force $To | Out-Null
  scp -q -i C:\Users\Leshe\.map-remote\user1_ed25519 -o UserKnownHostsFile=C:\Users\Leshe\.map-remote\known_hosts "user1@192.168.31.44:C:/MapAudit/live/shots/$f.png" "$To\$f.png"
}
