<#
.SYNOPSIS
Stage 10D native smoke for the full object-inspector slice.

.DESCRIPTION
Creates an isolated map and asset catalog, drives the real Release window through UI Automation,
exercises Shape, Line, Text, Stamp and Brush controls, saves/reopens the project, and writes PNG
evidence plus report.json. In background mode the owned window is parked outside the desktop,
never receives raw keyboard/mouse input, and is captured with PrintWindow.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir,
    [switch]$Background = $true
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspace = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
if (-not $Exe) { $Exe = Join-Path $workspace 'src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) {
    $OutDir = Join-Path $workspace ("artifacts\stage10d-object-inspectors\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$catalogRoot = Join-Path $OutDir 'catalog'
$recoveryRoot = Join-Path $OutDir 'recovery'
$mapRoot = Join-Path $OutDir 'maps'
$fixture = Join-Path $workspace 'tests\fixtures\assets\diagnostic-stamp-01.png'
$savedMap = Join-Path $mapRoot 'stage10d.mapproj'
if (-not (Test-Path -LiteralPath $fixture)) { throw "Fixture is missing: $fixture" }
New-Item -ItemType Directory -Force -Path $OutDir, $catalogRoot, $recoveryRoot, $mapRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Stage10DNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage10DNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr hWnd, bool enable);
}
"@
}

$script:Proc = $null
$script:Root = $null
$script:WindowHandle = [IntPtr]::Zero
$script:Shots = @()
$script:Checks = [ordered]@{}
$script:Partial = @()
$script:Index = 0

# ---------------------------------------------------------------- UI Automation helpers

function Find-Uia([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    foreach ($attempt in 1..15) {
        try {
            $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $found) { return $found }
        }
        catch [System.Runtime.InteropServices.COMException] { }
        Start-Sleep -Milliseconds 100
    }
    return $null
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
    throw "TIMEOUT waiting for '$automationId'. Last name: '$last'"
}

function Wait-State([scriptblock]$condition, [string]$what, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "TIMEOUT waiting for state: $what"
}

function Require-Uia([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { throw "Element '$automationId' was not found." }
    return $element
}

function Invoke-UiaElement([System.Windows.Automation.AutomationElement]$element, [string]$what) {
    if ($null -eq $element) { throw "Element to invoke was not found: $what" }
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
    }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    }
    else { throw "Element '$what' exposes no invokable UIA pattern." }
    Start-Sleep -Milliseconds 320
}

function Invoke-Uia([string]$automationId) {
    foreach ($attempt in 1..3) {
        try {
            Invoke-UiaElement (Require-Uia $automationId) $automationId
            return
        }
        catch [System.ArgumentException] { Start-Sleep -Milliseconds 400 }
        catch [System.Windows.Automation.ElementNotAvailableException] { Start-Sleep -Milliseconds 400 }
    }
    throw "Element '$automationId' could not be invoked after retries."
}

function Set-UiaValue([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 300
}

function Set-UiaRange([string]$automationId, [double]$value) {
    $element = Require-Uia $automationId
    $element.SetFocus()
    $range = $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $bounded = [Math]::Min(
        [double]$range.Current.Maximum,
        [Math]::Max([double]$range.Current.Minimum, $value))
    $range.SetValue($bounded)
    Start-Sleep -Milliseconds 220
}

function Set-UiaExpanded([string]$automationId, [bool]$expanded = $true) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expanded -and $pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $pattern.Expand()
    }
    elseif (-not $expanded -and $pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Collapse()
    }
    Start-Sleep -Milliseconds 350
}

function Select-UiaChoice([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $expand = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 250
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $value)
    $item = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) { $item = $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) }
    if ($null -eq $item) {
        $owned = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $owned)) {
            $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $item) { break }
        }
    }
    if ($null -eq $item) { throw "Choice '$value' is not offered by '$automationId'." }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

function Get-UiaName([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { return '' }
    return $element.Current.Name
}

function Find-RowLike([string]$fragment) {
    $type = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $all = $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)
    foreach ($element in $all) {
        if ($element.Current.Name -like "*$fragment*") { return $element }
    }
    return $null
}

function Select-Row([string]$fragment, [string]$expected) {
    foreach ($attempt in 1..5) {
        $row = Find-RowLike $fragment
        if ($null -ne $row) {
            try { Invoke-UiaElement $row $fragment } catch [System.ArgumentException] { }
        }
        if ((Get-UiaName 'SelectionStatusText') -like "*$expected*") { return }
    }
    throw "Selecting '$fragment' never produced '$expected'."
}

function Select-UiaTab([string]$name) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $element = $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)))
    if ($null -eq $element) { throw "Tab '$name' was not found." }
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 350
}

function Assert-True([bool]$condition, [string]$what) {
    if (-not $condition) { throw "$what : condition was false" }
    Write-Host "      ok  $what"
    $script:Checks[$what] = 'true'
}

function Assert-Contains([string]$actual, [string]$fragment, [string]$what) {
    if ($actual -notlike "*$fragment*") { throw "$what : '$actual' does not contain '$fragment'" }
    Write-Host "      ok  $what contains '$fragment'"
    $script:Checks[$what] = $actual
}

# ---------------------------------------------------------------- owned native window

function Protect-AppWindow {
    if (-not $Background -or $script:WindowHandle -eq [IntPtr]::Zero) { return }
    [void][Stage10DNative]::ShowWindow($script:WindowHandle, 6)
    Start-Sleep -Milliseconds 120
    [void][Stage10DNative]::ShowWindow($script:WindowHandle, 4)
    [void][Stage10DNative]::SetWindowPos(
        $script:WindowHandle, [IntPtr]::Zero, -32000, 0, 2000, 1250, 0x54)
    [void][Stage10DNative]::EnableWindow($script:WindowHandle, $false)
}

function Start-TestApp([string]$mapPath = '') {
    $env:MAP_UI_TEST_MODE = '1'
    $env:MAP_UI_TEST_ASSET_ROOT = $catalogRoot
    $env:MAP_UI_TEST_RECOVERY_ROOT = $recoveryRoot
    $env:MAP_UI_TEST_IMPORT_PATHS = $fixture
    $env:MAP_UI_TEST_MAP_PATH = $mapPath
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $script:Proc.Refresh()
        if ($script:Proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Map window did not appear.' }
    $script:WindowHandle = $script:Proc.MainWindowHandle
    if ($Background) { Protect-AppWindow } else { [void][Stage10DNative]::ShowWindow($script:WindowHandle, 3) }
    Start-Sleep -Milliseconds 700
    $script:Root = [System.Windows.Automation.AutomationElement]::FromHandle($script:WindowHandle)
}

function Stop-TestApp {
    if ($null -ne $script:Proc -and -not $script:Proc.HasExited) {
        $script:Proc.Kill()
        if (-not $script:Proc.WaitForExit(10000)) { throw 'Owned Map process did not terminate.' }
    }
    $script:Proc = $null
    $script:Root = $null
    $script:WindowHandle = [IntPtr]::Zero
}

function Save-Shot([string]$label) {
    $script:Index++
    $path = Join-Path $OutDir ("{0:d2}-{1}.png" -f $script:Index, $label)
    $rect = New-Object Stage10DNative+RECT
    [void][Stage10DNative]::GetWindowRect($script:WindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Invalid window bounds ${width}x${height}." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage10DNative]::PrintWindow($script:WindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' }
    }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $script:Shots += $path
    Write-Host ("  [{0:d2}] {1}  {2}x{3}" -f $script:Index, $label, $width, $height)
}

function New-TestMap {
    Wait-Uia 'DashboardTitle' { param($t) $t -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'NewMapButton'
    Wait-Uia 'WizardTitleBox' { param($t) $true } | Out-Null
    if ($Background) {
        $owned = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $owned)) {
            $handle = [IntPtr]$window.Current.NativeWindowHandle
            if ($handle -ne [IntPtr]::Zero -and $handle -ne $script:WindowHandle) {
                [void][Stage10DNative]::ShowWindow($handle, 6)
                [void][Stage10DNative]::ShowWindow($handle, 4)
                [void][Stage10DNative]::EnableWindow($handle, $false)
            }
        }
    }
    Set-UiaValue 'WizardTitleBox' 'Stage 10D inspectors'
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '2048'
    Set-UiaValue 'WizardHeightBox' '1400'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Protect-AppWindow
}

function Add-ObjectVia([string]$toolButton, [string]$toolTitle, [string]$addButton) {
    Invoke-Uia $toolButton
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like "*$toolTitle*" } | Out-Null
    Invoke-Uia $addButton
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Свойства*' } | Out-Null
}

function Get-SceneObject {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($savedMap)
    try {
        $entry = $archive.Entries | Where-Object FullName -eq 'scene.json'
        $reader = New-Object IO.StreamReader($entry.Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

# ================================================================= the run

try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
    Write-Host "Stage 10D native smoke (background: $Background)"
    Write-Host "  exe : $Exe"
    Write-Host "  out : $OutDir"

    Write-Host "`n[1] Create an isolated map"
    Start-TestApp -mapPath $savedMap
    New-TestMap
    Assert-True ($script:Proc.Id -gt 0) 'owned Release process started'

    Write-Host "`n[2] Import one deterministic PNG into the isolated library"
    Select-UiaTab 'Библиотека'
    Invoke-Uia 'ImportAssetButton'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'one library asset is available' 15000
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 1' 'isolated import completed'
    Save-Shot 'library-asset'

    Write-Host "`n[3] Create a Shape and apply the asset as its repeating fill"
    Add-ObjectVia 'ShapeToolButton' 'Инструмент: Фигура' 'AddVectorRectangleButton'
    Select-UiaTab 'Библиотека'
    Set-UiaExpanded 'AssetAdvanced'
    Invoke-Uia 'UseAssetShapeFillButton'
    Assert-Contains (Get-UiaName 'StatusDetailText') 'залив' 'shape-fill command reports its result'

    Write-Host "`n[4] Exercise real Shape controls, effects, undo and redo"
    Select-UiaTab 'Объекты'
    Select-Row 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Assert-True ($null -ne (Find-Uia 'ShapeFillColorBox')) 'shape fill color is a real editor'
    Assert-True ($null -ne (Find-Uia 'ShapeStrokeWidthSlider')) 'shape stroke width is a real slider'
    Set-UiaExpanded 'ShapeAppearance'
    Invoke-Uia 'ShapeStrokeShadowStandardButton'
    Invoke-Uia 'UndoButton'
    Invoke-Uia 'RedoButton'
    Save-Shot 'shape-inspector'

    Write-Host "`n[5] Create a Line and edit pattern plus shadow"
    Add-ObjectVia 'LineToolButton' 'Инструмент: Линия' 'AddLineButton'
    Select-UiaChoice 'LineStrokePatternChoice' 'Штрихами'
    Set-UiaExpanded 'LineStrokeAppearance'
    Invoke-Uia 'LineStrokeShadowStandardButton'
    Assert-True ($null -ne (Find-Uia 'LineStrokeBlurSlider')) 'line blur is exposed by the inspector'
    Save-Shot 'line-inspector'

    Write-Host "`n[6] Create Text and edit weight, alignment, flip and shadow"
    Add-ObjectVia 'TextToolButton' 'Инструмент: Текст' 'AddTextButton'
    Invoke-Uia 'TextBoldToggle'
    Select-UiaChoice 'TextAlignmentChoice' 'По центру'
    Set-UiaExpanded 'TextAppearance'
    Invoke-Uia 'TextFlipHorizontalToggle'
    Invoke-Uia 'TextShadowStandardButton'
    Invoke-Uia 'TextMissingFontButton'
    Assert-Contains (Get-UiaName 'TextFontStateText') 'используется' 'missing font has an explicit fallback'
    Save-Shot 'text-inspector'

    Write-Host "`n[7] Place a Stamp from the same library asset"
    Select-UiaTab 'Библиотека'
    Invoke-Uia 'PlaceStampButton'
    Wait-State { (Get-UiaName 'StatusDetailText') -like '*Размещён*' } 'stamp placement completes'
    Assert-Contains (Get-UiaName 'StatusDetailText') 'Размещён' 'stamp placement is reported'

    Write-Host "`n[8] Exercise Stamp scale, flips, appearance and undo"
    Assert-True ($null -ne (Find-Uia 'StampScaleSlider')) 'stamp scale is a real slider'
    Invoke-Uia 'StampFlipHorizontalToggle'
    Set-UiaExpanded 'StampAppearance'
    Invoke-Uia 'StampShadowStandardButton'
    Invoke-Uia 'UndoButton'
    Invoke-Uia 'RedoButton'
    Save-Shot 'stamp-inspector'

    Write-Host "`n[9] Turn the library image into a named Brush tip"
    Select-UiaTab 'Библиотека'
    Set-UiaExpanded 'AssetAdvanced'
    Invoke-Uia 'UseAssetBrushTipButton'
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Кисть*' } | Out-Null
    Set-UiaExpanded 'BrushAdvanced'
    Assert-Contains (Get-UiaName 'BrushTipNameText') 'diagnostic-stamp-01' 'brush tip keeps the asset name'

    Write-Host "`n[10] Exercise the full Brush settings surface"
    Assert-True ($null -ne (Find-Uia 'BrushColorBox')) 'brush color is a real editor'
    foreach ($id in 'BrushSizeSlider','BrushOpacitySlider','BrushSoftnessSlider','BrushSpacingSlider',
        'BrushScatterSlider','BrushAngleSlider','BrushSizeJitterSlider','BrushOpacityJitterSlider') {
        Assert-True ($null -ne (Find-Uia $id)) "$id is exposed"
    }
    Set-UiaRange 'BrushSizeSlider' 48
    Set-UiaRange 'BrushSoftnessSlider' 35
    Set-UiaRange 'BrushScatterSlider' 20
    Invoke-Uia 'BrushRandomFlipHorizontal'
    Save-Shot 'brush-controls'

    Write-Host "`n[11] Save and verify Stage 10D data in the actual archive"
    Invoke-Uia 'SaveButton'
    Wait-State { Test-Path -LiteralPath $savedMap } 'map archive is written' 15000
    Start-Sleep -Milliseconds 1000
    $scene = Get-SceneObject
    $objects = @($scene.layers | ForEach-Object { @($_.objects) })
    $shape = $objects | Where-Object { $null -ne $_.shape } | Select-Object -First 1
    $line = $objects | Where-Object { $null -ne $_.line } | Select-Object -First 1
    $text = $objects | Where-Object { $null -ne $_.text } | Select-Object -First 1
    $stamp = $objects | Where-Object { $null -ne $_.stamp } | Select-Object -First 1
    Assert-True ($shape.shape.fill.textureAssetSha256.Length -eq 64) 'shape texture hash persisted'
    Assert-True ($shape.shape.stroke.shadowOpacity -gt 0) 'shape shadow persisted'
    Assert-True ($line.line.stroke.pattern -eq 'Dashed') 'line pattern persisted'
    Assert-True ($text.text.fontWeight -eq 700) 'text weight persisted'
    Assert-True ($text.text.flipHorizontal -eq $true) 'text flip persisted'
    Assert-True ($stamp.stamp.flipHorizontal -eq $true) 'stamp flip persisted'

    Write-Host "`n[12] Reopen and prove the inspectors still expose the stored objects"
    Stop-TestApp
    Start-TestApp -mapPath $savedMap
    Wait-Uia 'DashboardTitle' { param($t) $t -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'OpenFileButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Select-UiaTab 'Объекты'
    Select-Row 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Assert-True ($null -ne (Find-Uia 'ShapeFillColorBox')) 'shape inspector survives reopen'
    Select-Row 'Текст · Текст' 'Текст'
    Assert-True ($null -ne (Find-Uia 'TextContentBox')) 'text inspector survives reopen'
    Save-Shot 'reopened-inspectors'

    Write-Host "`n[13] Clean shutdown"
    Stop-TestApp
    $left = @(Get-Process -Name 'Map.App' -ErrorAction SilentlyContinue)
    Assert-True ($left.Count -eq 0) 'no Map.App process is left behind'

    # PrintWindow returning seven files is not enough: a frozen or blank surface could still make
    # the UIA assertions pass. Prove two deliberately different inspector states are visibly
    # different and retain the difference image beside the evidence set.
    $compare = Join-Path $workspace 'scripts\compare-ui-screenshots.ps1'
    $diff = Join-Path $OutDir 'diff-shape-vs-brush.png'
    $metric = & powershell -ExecutionPolicy Bypass -File $compare `
        -Reference $script:Shots[1] -Actual $script:Shots[5] -Difference $diff `
        -MaximumNormalizedRmse 1.0
    $rmseLine = $metric | Select-String 'NormalizedRmse\s+:' | Select-Object -First 1
    if ($null -eq $rmseLine) { throw 'The screenshot RMSE could not be parsed.' }
    $rmse = [double]::Parse((($rmseLine -split ':')[1].Trim()),
        [Globalization.CultureInfo]::InvariantCulture)
    Assert-True ($rmse -gt 0.02) 'shape and brush screenshots are visibly different'
    $script:Checks['shape vs brush normalized RMSE'] = $rmse

    $report = [ordered]@{
        result = 'PASS'
        exe = $Exe
        outDir = $OutDir
        background = [bool]$Background
        shots = $script:Shots
        checks = $script:Checks
        partial = $script:Partial
        finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $report | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host ("`nPASS  {0} screenshots, {1} checks" -f $script:Shots.Count, $script:Checks.Count)
    Write-Host "Report: $(Join-Path $OutDir 'report.json')"
}
catch {
    try {
        if ($script:WindowHandle -ne [IntPtr]::Zero) { Save-Shot 'failure-state' }
    }
    catch { Write-Host "Failure screenshot unavailable: $($_.Exception.Message)" }
    $report = [ordered]@{
        result = 'FAIL'
        error = $_.Exception.Message
        at = $_.ScriptStackTrace
        outDir = $OutDir
        shots = $script:Shots
        checks = $script:Checks
        partial = $script:Partial
    }
    $report | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host "FAIL: $($_.Exception.Message)"
    throw
}
finally {
    Stop-TestApp
    Remove-Item Env:MAP_UI_TEST_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_ASSET_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_RECOVERY_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_IMPORT_PATHS -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_MAP_PATH -ErrorAction SilentlyContinue
}
