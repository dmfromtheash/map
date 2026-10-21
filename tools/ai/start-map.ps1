# What the desktop shortcut "Map — конструктор карт" runs: it starts the owner's local copy in
# C:\Users\Leshe\Apps\Map and, when anything under src/ (or the root build files) is newer than that
# copy, republishes it first with publish-local-r2r.ps1, so the shortcut always opens the current code,
# committed or not. If this worktree is gone, it just starts the copy that is there.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = 'C:\Users\Leshe\Apps\Map'
$exe = "$target\Map.App.exe"

$running = Get-Process Map.App -ErrorAction SilentlyContinue
if (-not $running -and (Test-Path "$root\src")) {
    $built = if (Test-Path "$target\.published") { (Get-Item "$target\.published").LastWriteTimeUtc } else { [datetime]::MinValue }
    $sources = @(Get-ChildItem "$root\src" -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
        @(Get-ChildItem $root -File -Filter 'Directory.*' -ErrorAction SilentlyContinue)
    $newest = ($sources | Measure-Object LastWriteTimeUtc -Maximum).Maximum
    if ($newest -gt $built) {
        Write-Host 'Map: найдены изменения, обновляю программу (около минуты)...'
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\publish-local-r2r.ps1"
            if ($LASTEXITCODE -ne 0) { throw "publish exit $LASTEXITCODE" }
        }
        catch {
            Write-Host "Не удалось обновить: $_"
            Write-Host 'Запускаю прежнюю версию. Нажмите Enter...'
            [void][Console]::ReadLine()
        }
    }
}
Start-Process -FilePath $exe -WorkingDirectory $target
