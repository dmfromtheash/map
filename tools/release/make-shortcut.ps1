# Creates the desktop shortcut "Map — конструктор карт" for the Map.App.exe next to this script
# and starts Map. Run it from "Создать ярлык на рабочем столе.cmd" in the unpacked folder.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir 'Map.App.exe'
if (-not (Test-Path $exe)) { throw "Рядом со скриптом нет Map.App.exe: $dir" }
$desktop = [Environment]::GetFolderPath('Desktop')
$link = Join-Path $desktop 'Map — конструктор карт.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $dir
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Map — конструктор карт'
$shortcut.Save()
Write-Host "Ярлык создан: $link"
Write-Host 'Запускаю Map...'
Start-Process -FilePath $exe -WorkingDirectory $dir