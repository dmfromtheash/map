# Automatic check of every new Astra commit (owner: «больше автоматизации»): bundle from USER -> PC review branch -> Mac clone
# -> Mac tests (Core, Persistence, Rendering) -> short result into Astra's INBOX and into auto_check.log (Claude watches it).
$ErrorActionPreference = 'Continue'
$S = Join-Path $env:TEMP 'map-fps'; New-Item -ItemType Directory -Force $S | Out-Null
$log = "$S\auto_check.log"
$key = @('-i', 'C:\Users\Leshe\.map-remote\user1_ed25519', '-o', 'UserKnownHostsFile=C:\Users\Leshe\.map-remote\known_hosts', '-o', 'HostKeyAlias=192.168.31.44', '-o', 'BatchMode=yes')
$last = (git -C C:\Projects\Map rev-parse --short review/astra-fps-1m).Trim()
"start $(Get-Date -f s) base $last" | Out-File $log -Append -Encoding utf8
while ($true) {
  Start-Sleep 90
  $head = (& C:\Users\Leshe\.map-remote\r.ps1 'cd C:\Projects\Map; git rev-parse --short astra/fps-1m' 2>$null | Select-Object -Last 1)
  if (-not $head -or $head.Trim() -eq $last) { continue }
  $head = $head.Trim()
  & C:\Users\Leshe\.map-remote\r.ps1 "cd C:\Projects\Map; git bundle create D:\MapChecks\astra\fps.bundle $last..astra/fps-1m" 2>$null | Out-Null
  scp -q @key 'user1@USER:D:/MapChecks/astra/fps.bundle' "$S\fps.bundle"
  git -C C:\Projects\Map fetch -q "$S\fps.bundle" 'astra/fps-1m:review/astra-fps-1m' 2>&1 | Out-Null
  $subject = (git -C C:\Projects\Map log --format='%h %s' -1 review/astra-fps-1m)
  git -C C:\Projects\Map bundle create "$S\review.bundle" "$last..review/astra-fps-1m" 2>&1 | Out-Null
  scp -q "$S\review.bundle" user@192.168.31.25:MapWork/review.bundle
  $mac = ssh user@192.168.31.25 "source ~/MapWork/env.sh; cd ~/MapWork/map && git fetch -q ~/MapWork/review.bundle review/astra-fps-1m:auto-$head && git checkout -q auto-$head && bash ~/MapWork/tests/mac_tests.sh 2>&1 | grep -E 'всего|Не пройден|Failed|DONE'"
  $summary = ($mac | ForEach-Object { $_ -replace '\s+-\s+Map\..*$', '' }) -join "`n"
  "$(Get-Date -f s) $subject`n$summary" | Out-File $log -Append -Encoding utf8
  $entry = "## $(Get-Date -f HH:mm) Claude-авто — тесты Mac для $subject`r`n`r`n$summary`r`n(Core/Persistence/Rendering на osx-arm64; известное платформенное падение — golden сетки Stage10E. Windows-набор и живой ПК — отдельно.)`r`n"
  $tmp = "$S\inbox_auto.tmp"; [IO.File]::WriteAllText($tmp, $entry, (New-Object Text.UTF8Encoding $false))
  scp -q @key $tmp 'user1@USER:D:/MapChecks/astra/inbox_entry.tmp'
  & C:\Users\Leshe\.map-remote\r.ps1 '$u=New-Object Text.UTF8Encoding $false; $f="D:\MapChecks\astra\INBOX.md"; $t="D:\MapChecks\astra\inbox_entry.tmp"; $e=[IO.File]::ReadAllText($t,$u); $old=[IO.File]::ReadAllText($f,$u); $p=$old -split "\r?\n",2; $rest=""; if($p.Count -gt 1){$rest=$p[1]}; [IO.File]::WriteAllText($f, $p[0]+"`r`n`r`n"+$e+"`r`n"+$rest, $u); Remove-Item $t' 2>$null | Out-Null
  $last = $head
}
