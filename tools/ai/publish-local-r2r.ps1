# Builds the owner's local copy of Map with ReadyToRun (precompiled code: the first frame of a map
# 1.5 -> 1.0 s, fewer JIT pauses on first actions; measured 2026-10-18) into C:\Users\Leshe\Apps\Map,
# which the desktop shortcut "Map — конструктор карт" starts directly. Run it at the end of every session
# that changed code (AGENTS.md); start-map.ps1 remains for a manual "update if needed, then start".
# Local use on this PC only: not a release.
# Run after changes, with Map closed:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/publish-local-r2r.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = 'C:\Users\Leshe\Apps\Map'
$exe = "$target\Map.App.exe"
if (Get-Process Map.App -ErrorAction SilentlyContinue) { throw 'Map is running; close it first.' }
dotnet publish "$root\src\Map.App\Map.App.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }
# The regional pack is not in the repository; it lives beside the Release build and is copied as is.
$atlas = "$root\src\Map.App\bin\Release\net10.0\win-x64\RegionalAtlas"
if (Test-Path $atlas) { robocopy $atlas "$target\RegionalAtlas" /E /NFL /NDL /NJH /NJS | Out-Null }
# The desktop shortcut names the build it starts (its tooltip) and takes the icon from the program.
$link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Map — конструктор карт.lnk'
if (Test-Path $link) {
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    $built = (Get-Item "$target\Map.App.dll").LastWriteTime.ToString('dd.MM HH:mm')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    # The shortcut starts the program itself. Going through start-map.ps1 cost about a second on every
    # start (PowerShell) and a minute after each code change (republish); the copy is now refreshed by
    # the executor at the end of a session that changed code (owner, 2026-10-24).
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = ""
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = "$target\Map.App.exe,0"
    $shortcut.Description = "Map · сборка $built (ускоренная, коммит $commit)"
    $shortcut.Save()
}
# start-map.ps1 compares the code with this mark, not with file dates the publish may keep.
Set-Content -Path "$target\.published" -Value (git -C $root rev-parse --short HEAD).Trim()
Write-Output "Published to $target"
