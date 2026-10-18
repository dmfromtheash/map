# Builds the owner's local copy of Map with ReadyToRun (precompiled code: the first frame of a map
# 1.5 -> 1.0 s, fewer JIT pauses on first actions; measured 2026-10-18) into C:\Users\Leshe\Apps\Map,
# where the desktop shortcut "Map — конструктор карт" points. Local use on this PC only: not a release.
# Run after changes, with Map closed:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/publish-local-r2r.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = 'C:\Users\Leshe\Apps\Map'
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
    $shortcut.TargetPath = "$target\Map.App.exe"; $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = "$target\Map.App.exe,0"
    $shortcut.Description = "Map · сборка $built (ускоренная, коммит $commit)"
    $shortcut.Save()
}
Write-Output "Published to $target"
