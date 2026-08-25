<#
.SYNOPSIS
Stage 9A native smoke: create dirty work, wait for a background autosave in an isolated recovery
root, terminate only that app process, relaunch, restore the offered copy, and verify honest state.

.DESCRIPTION
The driver uses UI Automation Invoke/Value patterns and PrintWindow, so it does not type into or
click the user's other applications. MAP_UI_TEST_RECOVERY_ROOT keeps all recovery data under the
requested artifact directory. Every wait is bounded and both app processes are owned by the run.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspace = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
if (-not $Exe) { $Exe = Join-Path $workspace 'src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) {
    $OutDir = Join-Path $workspace ("artifacts\stage09-autosave\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$recoveryRoot = Join-Path $OutDir 'recovery'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $recoveryRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not ('Stage09Native' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage09Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
}
"@
}

$script:Proc = $null
$script:Root = $null
$script:Shots = @()
$script:Evidence = [ordered]@{}

function Find-Uia([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Uia([string]$automationId, [scriptblock]$until, [int]$timeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $element = Find-Uia $automationId
        if ($null -ne $element) {
            $last = $element.Current.Name
            if (& $until $last) { return $element }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "TIMEOUT waiting for '$automationId'. Last text: '$last'"
}

function Invoke-Uia([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $element = Find-Uia $automationId $root
    if ($null -eq $element) { throw "Button '$automationId' was not found." }
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
    }
    else {
        throw "Element '$automationId' exposes neither Invoke nor Toggle pattern."
    }
    Start-Sleep -Milliseconds 250
}

function Select-UiaTab([string]$name) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    $element = $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Tab '$name' was not found." }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 300
}

function Wait-UiaName([string]$name, [int]$timeoutMs = 10000) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $element = $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    }
    throw "TIMEOUT waiting for UIA name '$name'."
}

function Set-UiaValue(
    [string]$automationId, [string]$value, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $element = Find-Uia $automationId $root
    if ($null -eq $element) { throw "Value control '$automationId' was not found." }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
}

function Start-TestApp {
    $environment = @{
        'MAP_UI_TEST_MODE' = '1'
        'MAP_UI_TEST_RECOVERY_ROOT' = $recoveryRoot
    }
    $script:Proc = Start-Process -FilePath $Exe -Environment $environment -PassThru
    for ($index = 0; $index -lt 60; $index++) {
        Start-Sleep -Milliseconds 250
        $script:Proc.Refresh()
        if ($script:Proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Map window did not appear.' }
    [void][Stage09Native]::ShowWindow($script:Proc.MainWindowHandle, 3)
    Start-Sleep -Milliseconds 600
    $script:Root = [System.Windows.Automation.AutomationElement]::FromHandle(
        $script:Proc.MainWindowHandle)
}

function Stop-TestApp {
    if ($null -ne $script:Proc -and -not $script:Proc.HasExited) {
        $script:Proc.Kill()
        if (-not $script:Proc.WaitForExit(10000)) { throw 'Owned Map process did not terminate.' }
    }
}

function Save-Shot([string]$name) {
    $path = Join-Path $OutDir $name
    $rect = New-Object Stage09Native+RECT
    [void][Stage09Native]::GetWindowRect($script:Proc.MainWindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Invalid window bounds ${width}x${height}." }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage09Native]::PrintWindow($script:Proc.MainWindowHandle, $hdc, 2)) {
            throw 'PrintWindow failed.'
        }
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $decoded = [System.Drawing.Image]::FromFile($path)
    try {
        if ($decoded.Width -ne $width -or $decoded.Height -ne $height) {
            throw "Screenshot dimensions differ from the window: $path"
        }
    }
    finally { $decoded.Dispose() }
    $script:Shots += $path
}

try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }

    Start-TestApp
    Wait-Uia 'DashboardTitle' { param($text) $text -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'NewMapButton'
    Wait-Uia 'WizardTitleBox' { param($text) $true } | Out-Null
    Set-UiaValue 'WizardTitleBox' 'Stage 9 autosave'
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '1600'
    Set-UiaValue 'WizardHeightBox' '900'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'SaveStateText' { param($text) $text -like 'Не сохранено*' } | Out-Null
    Invoke-Uia 'AddRectangleButton'
    Wait-Uia 'SelectionStatusText' { param($text) $text -eq 'Прямоугольник' } | Out-Null

    $autosave = Wait-Uia 'AutosaveStatusText' {
        param($text) $text -like 'Автокопия: *'
    } 30000
    $manualState = (Find-Uia 'SaveStateText').Current.Name
    if ($manualState -notlike 'Не сохранено*') {
        throw "Autosave falsely changed manual save state to '$manualState'."
    }
    $script:Evidence['firstAutosaveStatus'] = $autosave.Current.Name
    $script:Evidence['manualStateAfterAutosave'] = $manualState

    $listeners = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $script:Proc.Id }
    if ($listeners) { throw 'Map opened a TCP listener during autosave.' }
    Save-Shot '01-autosaved-dirty-editor.png'
    Stop-TestApp

    $records = @(Get-ChildItem -LiteralPath $recoveryRoot -Filter 'autosave.json' -Recurse -File)
    if ($records.Count -ne 1) { throw "Expected one autosave.json, found $($records.Count)." }
    $record = Get-Content -Raw -LiteralPath $records[0].FullName | ConvertFrom-Json
    $candidatePath = Join-Path $records[0].DirectoryName $record.snapshotFileName
    if (-not (Test-Path -LiteralPath $candidatePath)) { throw 'Recorded autosave snapshot is missing.' }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($candidatePath)
    try {
        foreach ($required in 'manifest.json', 'scene.json', 'asset-manifest.json', 'preview.png') {
            if ($null -eq $archive.GetEntry($required)) { throw "Autosave is missing $required." }
        }
    }
    finally { $archive.Dispose() }
    $script:Evidence['snapshotFile'] = Split-Path -Leaf $candidatePath
    $script:Evidence['snapshotRevision'] = [long]$record.revision

    Start-TestApp
    $recoveryText = Wait-Uia 'RecoveryText0' {
        param($text) $text -like 'Автосохранённая копия «Stage 9 autosave»*'
    } 30000
    $script:Evidence['startupOffer'] = $recoveryText.Current.Name
    Save-Shot '02-startup-recovery-offer.png'

    Invoke-Uia 'RestoreButton'
    $restoredState = Wait-Uia 'SaveStateText' { param($text) $text -like 'Не сохранено*' } 30000
    Wait-Uia 'AutosaveStatusText' { param($text) $text -like 'Автокопия*' } | Out-Null
    $script:Evidence['restoredState'] = $restoredState.Current.Name
    Select-UiaTab 'Объекты'
    Wait-UiaName 'Прямоугольник · Прямоугольник' | Out-Null
    $script:Evidence['restoredObject'] = 'Прямоугольник'
    Save-Shot '03-restored-dirty-editor.png'

    $listeners = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $script:Proc.Id }
    if ($listeners) { throw 'Restored Map process opened a TCP listener.' }
}
finally {
    Stop-TestApp
}

$script:Evidence['status'] = 'FACT'
$script:Evidence['recoveryRoot'] = $recoveryRoot
$script:Evidence['screenshots'] = @($script:Shots | ForEach-Object { Split-Path -Leaf $_ })
$script:Evidence | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $OutDir 'evidence.json') -Encoding UTF8
Write-Host "Stage 9A autosave smoke passed: $OutDir"
