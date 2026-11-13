# Windows build + full tests of Astra's review branch on the PC (16 threads), then back to the work branch.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:VSLANG = '1033'
$log = Join-Path $env:TEMP 'map-fps\pc-review-tests.log'; New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Set-Location C:\Projects\Map
"start $(Get-Date -f HH:mm:ss) $(git rev-parse --short review/astra-fps-1m)" | Out-File $log -Encoding utf8
git switch -q review/astra-fps-1m 2>&1 | Out-File $log -Append -Encoding utf8
dotnet build-server shutdown | Out-Null
$t = Get-Date
$b = dotnet build Map.slnx -c Release --nologo -v q 2>&1
($b | Select-String ' error |Build succeeded|Warning\(s\)|Error\(s\)' | Select-Object -Last 4) | Out-File $log -Append -Encoding utf8
"build $([int]((Get-Date)-$t).TotalSeconds) s" | Out-File $log -Append -Encoding utf8
foreach ($p in 'Map.Core.Tests','Map.Assets.Tests','Map.Persistence.Tests','Map.Presentation.Tests','Map.Rendering.Tests') {
  $t = Get-Date
  $r = dotnet test "tests\$p" -c Release --nologo 2>&1
  ($r | Select-String '^\s+Failed |Passed!|Failed!|Total tests' | Select-Object -Last 15) | Out-File $log -Append -Encoding utf8
  "$p $([int]((Get-Date)-$t).TotalSeconds) s" | Out-File $log -Append -Encoding utf8
}
dotnet build-server shutdown | Out-Null
git switch -q codex/stage-18-editable-areas 2>&1 | Out-File $log -Append -Encoding utf8
"DONE $(Get-Date -f HH:mm:ss) branch $(git branch --show-current)" | Out-File $log -Append -Encoding utf8
