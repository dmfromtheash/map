# Starts the isolated test Map (owner profile untouched), creates a standard map and opens «Объекты». Prints the PID.
# Profile folder is temporary: delete it after the live check (owner rule «только актуальное»).
$p = Join-Path $env:TEMP 'map-live-profile'
New-Item -ItemType Directory -Force $p | Out-Null
$psi = New-Object Diagnostics.ProcessStartInfo 'C:\Projects\Map\src\Map.App\bin\Release\net10.0\win-x64\Map.App.exe'
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['MAP_UI_TEST_MODE'] = '1'
$psi.EnvironmentVariables['MAP_UI_TEST_PROFILE_ROOT'] = $p
$psi.EnvironmentVariables['MAP_UI_TEST_RECOVERY_ROOT'] = "$p\Recovery"
$psi.EnvironmentVariables['MAP_UI_TEST_SHORTCUT_SETTINGS_PATH'] = "$p\shortcuts.json"
$psi.EnvironmentVariables['MAP_DRIVE_LIBRARY_ROOTS'] = 'H:\MapLibrary'
$proc = [Diagnostics.Process]::Start($psi)
$u = 'H:\MapLibrary\sol\sol7\uia.ps1'
function Step($id, $wait) {
    for ($i = 0; $i -lt 20; $i++) {
        $r = powershell -NoProfile -ExecutionPolicy Bypass -File $u -Action invoke -Match $id -ProcessId $proc.Id
        if ($r -match '^INVOKED|^SELECTED') { Start-Sleep $wait; return }
        Start-Sleep 2
    }
    throw "not found: $id"
}
Step 'NewMapButton' 3
Step 'ChooseCategoryButton' 3
Step 'CreateMapButton' 6
Step 'WorkspaceObjectsTab' 2
$proc.Id
