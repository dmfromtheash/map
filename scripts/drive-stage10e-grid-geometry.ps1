<#
.SYNOPSIS
Stage 10E native smoke for grid geometry and the complete grid property sheet.

.DESCRIPTION
Creates an isolated map, drives the exact Release Map window through UI Automation without raw
mouse or keyboard input, verifies four grid geometries, exact properties, undo/redo, snapping,
save/reopen and export policy, and writes numbered PNG evidence plus report.json.
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
    $OutDir = Join-Path $workspace ("artifacts\stage10e-grid-geometry\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}
$Exe = [IO.Path]::GetFullPath($Exe)
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$recoveryRoot = Join-Path $OutDir 'recovery'
$mapRoot = Join-Path $OutDir 'maps'
$savedMap = Join-Path $mapRoot 'stage10e-grid-geometry.mapproj'
$exportWithGrid = Join-Path $OutDir '07-export-with-grid.png'
$exportWithoutGrid = Join-Path $OutDir '08-export-without-grid.png'
$exportDiff = Join-Path $OutDir 'diff-export-with-vs-without-grid.png'
New-Item -ItemType Directory -Force -Path $OutDir, $recoveryRoot, $mapRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Stage10ENative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage10ENative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr hWnd, bool enable);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
"@
}

$script:Proc = $null
$script:Root = $null
$script:WindowHandle = [IntPtr]::Zero
$script:Shots = @()
$script:Checks = [ordered]@{}
$script:Actions = New-Object Collections.Generic.List[string]
$script:Partial = New-Object Collections.Generic.List[string]
$script:Blocked = New-Object Collections.Generic.List[string]
$script:WindowEvidence = $null
$script:SaveReopen = [ordered]@{}
$script:ProcessCleanup = $false

function Add-Action([string]$value) {
    $script:Actions.Add($value)
    Write-Host "      action  $value"
}

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

function Require-Uia([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { throw "Element '$automationId' was not found." }
    return $element
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

function Wait-State([scriptblock]$condition, [string]$what, [int]$timeoutMs = 10000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "TIMEOUT waiting for state: $what"
}

function Invoke-UiaElement([System.Windows.Automation.AutomationElement]$element, [string]$what) {
    if ($null -eq $element) { throw "Element to invoke was not found: $what" }
    $pattern = $null
    if (($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::CheckBox) -and
        $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
    }
    elseif (($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::RadioButton) -and
        $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
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

function Get-UiaName([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { return '' }
    return $element.Current.Name
}

function Get-UiaValue([string]$automationId) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Get-UiaSelected([string]$automationId) {
    $element = Require-Uia $automationId
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        return [bool]$pattern.Current.IsSelected
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    }
    throw "Element '$automationId' exposes neither SelectionItem nor Toggle."
}

function Get-UiaChecked([string]$automationId) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Set-UiaValue([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 250
}

function Commit-UiaValue([string]$automationId, [string]$value, [string]$nextAutomationId) {
    if ($Background) { [void][Stage10ENative]::EnableWindow($script:WindowHandle, $true) }
    try {
        $element = Require-Uia $automationId
        $element.SetFocus()
        Start-Sleep -Milliseconds 100
        $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
        Start-Sleep -Milliseconds 100
        (Require-Uia $nextAutomationId).SetFocus()
        Start-Sleep -Milliseconds 350
    }
    finally {
        if ($Background) { [void][Stage10ENative]::EnableWindow($script:WindowHandle, $false) }
    }
}

function Scroll-UiaIntoView([string]$automationId) {
    $element = Require-Uia $automationId
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.ScrollIntoView()
        Start-Sleep -Milliseconds 350
        return
    }
    $script:Partial.Add("UIA ScrollItemPattern is not exposed for $automationId; the 2000x1250 window is used instead.")
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

function Find-RowLike([string]$fragment) {
    $type = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($element in $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)) {
        if ($element.Current.Name -like "*$fragment*") { return $element }
    }
    return $null
}

function Assert-True([bool]$condition, [string]$what, $actual = $null) {
    if (-not $condition) { throw "$what : condition was false; actual=$actual" }
    Write-Host "      ok  $what"
    $script:Checks[$what] = if ($null -eq $actual) { $true } else { $actual }
}

function Assert-Equal($actual, $expected, [string]$what) {
    if ($actual -ne $expected) { throw "$what : actual='$actual', expected='$expected'" }
    Write-Host "      ok  $what = '$actual'"
    $script:Checks[$what] = $actual
}

function Assert-Near([double]$actual, [double]$expected, [double]$tolerance, [string]$what) {
    if ([Math]::Abs($actual - $expected) -gt $tolerance) {
        throw "$what : actual=$actual, expected=$expected, tolerance=$tolerance"
    }
    Write-Host "      ok  $what = $actual"
    $script:Checks[$what] = $actual
}

function Assert-Control([string]$id, [System.Windows.Automation.ControlType]$type, [string]$name) {
    $element = Require-Uia $id
    Assert-Equal $element.Current.ControlType.ProgrammaticName $type.ProgrammaticName "$id UIA role"
    Assert-Equal $element.Current.Name $name "$id accessible name"
    Assert-True $element.Current.IsEnabled "$id is enabled" $element.Current.IsEnabled
}

function Protect-AppWindow {
    if (-not $Background -or $script:WindowHandle -eq [IntPtr]::Zero) { return }
    [void][Stage10ENative]::ShowWindow($script:WindowHandle, 6)
    Start-Sleep -Milliseconds 120
    [void][Stage10ENative]::ShowWindow($script:WindowHandle, 4)
    [void][Stage10ENative]::SetWindowPos(
        $script:WindowHandle, [IntPtr]::Zero, -32000, 0, 2000, 1250, 0x54)
    [void][Stage10ENative]::EnableWindow($script:WindowHandle, $false)
}

function Start-TestApp([string]$mapPath) {
    $env:MAP_UI_TEST_MODE = '1'
    $env:MAP_UI_TEST_RECOVERY_ROOT = $recoveryRoot
    $env:MAP_UI_TEST_MAP_PATH = $mapPath
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    foreach ($attempt in 1..80) {
        Start-Sleep -Milliseconds 250
        $script:Proc.Refresh()
        if ($script:Proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Map window did not appear.' }
    $script:WindowHandle = $script:Proc.MainWindowHandle
    if ($Background) { Protect-AppWindow } else { [void][Stage10ENative]::ShowWindow($script:WindowHandle, 3) }
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

function Save-Shot([string]$fileName) {
    $path = Join-Path $OutDir $fileName
    $rect = New-Object Stage10ENative+RECT
    [void][Stage10ENative]::GetWindowRect($script:WindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Invalid window bounds ${width}x${height}." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage10ENative]::PrintWindow($script:WindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' }
    }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $script:Shots += $path
    Write-Host "      screenshot  $fileName (${width}x${height})"
}

function New-TestMap {
    Wait-Uia 'DashboardTitle' { param($text) $text -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'NewMapButton'
    Wait-Uia 'WizardTitleBox' { param($text) $true } | Out-Null
    Set-UiaValue 'WizardTitleBox' 'Stage 10E grid geometry'
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '1600'
    Set-UiaValue 'WizardHeightBox' '1000'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'EditorSideTabs' { param($text) $true } | Out-Null
    Protect-AppWindow
}

function Save-Map {
    Invoke-Uia 'SaveButton'
    Start-Sleep -Milliseconds 700
    Wait-State { Test-Path -LiteralPath $savedMap } 'map archive exists' 20000
    Wait-State { (Get-UiaName 'SaveStateText') -eq 'Сохранено' } 'editor reports saved' 20000
    Start-Sleep -Milliseconds 400
}

function Get-SceneObject {
    foreach ($attempt in 1..20) {
        try {
            $archive = [IO.Compression.ZipFile]::OpenRead($savedMap)
            try {
                $entry = $archive.Entries | Where-Object FullName -eq 'scene.json'
                $reader = New-Object IO.StreamReader($entry.Open())
                try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
                finally { $reader.Dispose() }
            }
            finally { $archive.Dispose() }
        }
        catch [IO.IOException] { Start-Sleep -Milliseconds 200 }
    }
    throw "Could not read scene.json from $savedMap"
}

function Get-SceneObjects($scene) {
    return @($scene.layers | ForEach-Object { @($_.objects) } | Where-Object { $null -ne $_ })
}

function Get-SceneGrid($scene) {
    return @($scene.layers | ForEach-Object { @($_.grids) } | Where-Object { $null -ne $_ })[0]
}

function Get-FirstLine($scene) {
    return Get-SceneObjects $scene | Where-Object { $null -ne $_.line } | Select-Object -First 1
}

function Get-IsoLatticeResidual([double]$worldX, [double]$worldY, $grid) {
    $angle = [double]$grid.rotationDegrees * [Math]::PI / 180
    $dx = $worldX - [double]$grid.offsetX
    $dy = $worldY - [double]$grid.offsetY
    $localX = $dx * [Math]::Cos($angle) + $dy * [Math]::Sin($angle)
    $localY = -$dx * [Math]::Sin($angle) + $dy * [Math]::Cos($angle)
    $step = [double]$grid.cellSize / [double]$grid.subdivisions
    $a = [Math]::Cos([Math]::PI / 6) * $step
    $b = [Math]::Sin([Math]::PI / 6) * $step
    $u = $localX / (2 * $a) + $localY / (2 * $b)
    $v = $localX / (2 * $a) - $localY / (2 * $b)
    return [Math]::Max([Math]::Abs($u - [Math]::Round($u)), [Math]::Abs($v - [Math]::Round($v)))
}

function Get-NodeWorld($line, $node) {
    $x = [double]$line.x + [double]$node.x * [double]$line.width
    $y = [double]$line.y + [double]$node.y * [double]$line.height
    $cx = [double]$line.x + [double]$line.width / 2
    $cy = [double]$line.y + [double]$line.height / 2
    $angle = [double]$line.rotationDegrees * [Math]::PI / 180
    return [pscustomobject]@{
        X = $cx + ($x - $cx) * [Math]::Cos($angle) - ($y - $cy) * [Math]::Sin($angle)
        Y = $cy + ($x - $cx) * [Math]::Sin($angle) + ($y - $cy) * [Math]::Cos($angle)
    }
}

function Get-PortalWorld($line, $portal) {
    $points = @($line.line.nodes | ForEach-Object { Get-NodeWorld $line $_ })
    $segments = @()
    $total = 0.0
    for ($index = 0; $index -lt $points.Count - 1; $index++) {
        $length = [Math]::Sqrt([Math]::Pow($points[$index + 1].X - $points[$index].X, 2) +
            [Math]::Pow($points[$index + 1].Y - $points[$index].Y, 2))
        $segments += $length
        $total += $length
    }
    $target = [double]$portal.position * $total
    $cursor = 0.0
    for ($index = 0; $index -lt $segments.Count; $index++) {
        if ($target -le $cursor + $segments[$index]) {
            $fraction = ($target - $cursor) / [Math]::Max($segments[$index], 0.001)
            return [pscustomobject]@{
                X = $points[$index].X + ($points[$index + 1].X - $points[$index].X) * $fraction
                Y = $points[$index].Y + ($points[$index + 1].Y - $points[$index].Y) * $fraction
            }
        }
        $cursor += $segments[$index]
    }
    return $points[-1]
}

function Invoke-GoldenExport([string]$destination) {
    $project = Join-Path $workspace 'tools\Map.GridGoldenProposer\Map.GridGoldenProposer.csproj'
    $output = & dotnet run --project $project --configuration Release --no-build -- `
        --export $savedMap $destination 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Grid export evidence tool failed: $output" }
    Assert-True (Test-Path -LiteralPath $destination) "export exists: $(Split-Path -Leaf $destination)" $output
}

function Read-ImageDimensions([string]$path) {
    $image = [System.Drawing.Image]::FromFile($path)
    try { return "{0}x{1}" -f $image.Width, $image.Height }
    finally { $image.Dispose() }
}

function Measure-ScreenshotDifference([string]$reference, [string]$actual, [string]$difference) {
    $compare = Join-Path $workspace 'scripts\compare-ui-screenshots.ps1'
    $metric = & powershell -NoProfile -ExecutionPolicy Bypass -File $compare `
        -Reference $reference -Actual $actual -Difference $difference -MaximumNormalizedRmse 1.0
    if ($LASTEXITCODE -ne 0) { throw "Image comparison failed: $metric" }
    $line = $metric | Select-String 'NormalizedRmse\s+:' | Select-Object -First 1
    if ($null -eq $line) { throw 'Normalized RMSE was not returned.' }
    return [double]::Parse((($line -split ':')[1].Trim()),
        [Globalization.CultureInfo]::InvariantCulture)
}

try {
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Executable not found: $Exe" }
    $expectedExe = [IO.Path]::GetFullPath((Join-Path $workspace 'src\Map.App\bin\Release\net10.0\Map.App.exe'))
    Assert-Equal $Exe $expectedExe 'exact Release EXE path'
    $exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Exe).Hash.ToLowerInvariant()
    $branch = (& git -C $workspace branch --show-current).Trim()
    $head = (& git -C $workspace rev-parse HEAD).Trim()

    Write-Host "Stage 10E native smoke (background: $Background)"
    Write-Host "  exe : $Exe"
    Write-Host "  out : $OutDir"

    Write-Host "`n[1] Start the exact Release process and create an isolated map"
    Start-TestApp $savedMap
    Assert-Equal $script:Proc.Path $Exe 'running process executable identity'
    $rect = New-Object Stage10ENative+RECT
    [void][Stage10ENative]::GetWindowRect($script:WindowHandle, [ref]$rect)
    $dpi = [Stage10ENative]::GetDpiForWindow($script:WindowHandle)
    $script:WindowEvidence = [ordered]@{
        width = $rect.Right - $rect.Left
        height = $rect.Bottom - $rect.Top
        dpi = $dpi
        scale = $dpi / 96.0
        renderer = 'Avalonia native window; MapCanvasControl uses ISkiaSharpApiLeaseFeature/SkiaSharp; runtime GPU backend is not exposed by the app'
    }
    $script:Partial.Add('Runtime GPU/software renderer backend is not exposed by Map.App; the report records the factual SkiaSharp lease path.')
    New-TestMap
    Add-Action 'created 1600x1000 map through supported test-mode wizard'

    Write-Host "`n[2] Add a Grid layer and inspect the complete standard-control property sheet"
    Select-UiaTab 'Слои'
    Invoke-Uia 'AddGridLayerButton'
    Wait-State { $null -ne (Find-Uia 'GridCellSizeBox') } 'grid property sheet appears'
    Assert-Control 'GridGeometrySquareRadio' ([System.Windows.Automation.ControlType]::RadioButton) 'Квадратная'
    Assert-Control 'GridGeometryIsometricSquaresRadio' ([System.Windows.Automation.ControlType]::RadioButton) 'Изометрическая'
    Assert-Control 'GridGeometryHexFlatRadio' ([System.Windows.Automation.ControlType]::RadioButton) 'Шестиугольная, плоский верх'
    Assert-Control 'GridGeometryHexPointyRadio' ([System.Windows.Automation.ControlType]::RadioButton) 'Шестиугольная, острый верх'
    foreach ($field in ([ordered]@{
        GridCellSizeBox='Размер ячейки, px'; GridSubdivisionsBox='Деления'; GridLineWidthBox='Толщина линии, px';
        GridColorBox='Цвет линии'; GridOpacityBox='Непрозрачность, %'; GridOffsetXBox='Смещение X, px';
        GridOffsetYBox='Смещение Y, px'; GridRotationBox='Поворот, °'
    }).GetEnumerator()) {
        Assert-Control $field.Key ([System.Windows.Automation.ControlType]::Edit) $field.Value
    }
    Assert-Control 'GridSnapToggle' ([System.Windows.Automation.ControlType]::CheckBox) 'Привязка к сетке'
    Assert-Control 'GridIncludeInExportToggle' ([System.Windows.Automation.ControlType]::CheckBox) 'Добавлять сетку в экспорт'
    Add-Action 'verified roles, accessible names and enabled state for the full grid property sheet'

    Write-Host "`n[3] Select and capture all four independent geometries"
    $presets = @(
        @{ Id='GridGeometrySquareRadio'; File='01-square.png'; Name='Square' },
        @{ Id='GridGeometryIsometricSquaresRadio'; File='02-isometric-squares.png'; Name='IsometricSquares' },
        @{ Id='GridGeometryHexFlatRadio'; File='03-hex-flat.png'; Name='HexFlat' },
        @{ Id='GridGeometryHexPointyRadio'; File='04-hex-pointy.png'; Name='HexPointy' }
    )
    foreach ($preset in $presets) {
        Invoke-Uia $preset.Id
        Wait-State { Get-UiaSelected $preset.Id } "$($preset.Name) is selected"
        Assert-True (Get-UiaSelected $preset.Id) "$($preset.Name) selected through UIA"
        Save-Shot $preset.File
        Save-Map
        Assert-Equal ([string](Get-SceneGrid (Get-SceneObject)).geometry) $preset.Name `
            "$($preset.Name) committed to scene.json"
        Add-Action "selected $($preset.Name) and captured $($preset.File)"
    }
    $geometryRmse = [ordered]@{}
    for ($index = 1; $index -lt $script:Shots.Count; $index++) {
        $key = "{0}-vs-{1}" -f $presets[$index - 1].Name, $presets[$index].Name
        $diffPath = Join-Path $OutDir ("diff-{0}.png" -f $key.ToLowerInvariant())
        $geometryRmse[$key] = Measure-ScreenshotDifference $script:Shots[$index - 1] $script:Shots[$index] $diffPath
        Assert-True ($geometryRmse[$key] -gt 0.001) "$key produces a visible native result" $geometryRmse[$key]
    }

    Write-Host "`n[4] Commit exact custom values and one toggle per real control"
    Invoke-Uia 'GridGeometryIsometricSquaresRadio'
    Commit-UiaValue 'GridCellSizeBox' '96.5' 'GridSubdivisionsBox'
    Commit-UiaValue 'GridSubdivisionsBox' '4' 'GridLineWidthBox'
    Commit-UiaValue 'GridLineWidthBox' '2.75' 'GridColorBox'
    Commit-UiaValue 'GridColorBox' '#CC224466' 'GridOpacityBox'
    Commit-UiaValue 'GridOpacityBox' '65' 'GridOffsetXBox'
    Commit-UiaValue 'GridOffsetXBox' '13.5' 'GridOffsetYBox'
    Commit-UiaValue 'GridOffsetYBox' '-17.25' 'GridRotationBox'
    Commit-UiaValue 'GridRotationBox' '27' 'GridCellSizeBox'
    foreach ($expected in ([ordered]@{
        GridCellSizeBox='96.5'; GridSubdivisionsBox='4'; GridLineWidthBox='2.75';
        GridColorBox='#CC224466'; GridOpacityBox='65'; GridOffsetXBox='13.5';
        GridOffsetYBox='-17.25'; GridRotationBox='27'
    }).GetEnumerator()) {
        Assert-Equal (Get-UiaValue $expected.Key) $expected.Value "$($expected.Key) visible value"
    }
    Add-Action 'committed all exact numeric/color properties'

    Write-Host "`n[5] Undo and redo one exact property command"
    Invoke-Uia 'UndoButton'
    Wait-State { (Get-UiaValue 'GridRotationBox') -eq '0' } 'rotation undo restores 0'
    Assert-Equal (Get-UiaValue 'GridRotationBox') '0' 'rotation after undo'
    Invoke-Uia 'RedoButton'
    Wait-State { (Get-UiaValue 'GridRotationBox') -eq '27' } 'rotation redo restores 27'
    Assert-Equal (Get-UiaValue 'GridRotationBox') '27' 'rotation after redo'
    Add-Action 'undid and redid the rotation property as one command'

    Invoke-Uia 'GridSnapToggle'
    Assert-True (-not (Get-UiaChecked 'GridSnapToggle')) 'snap toggle turns off'
    Invoke-Uia 'GridSnapToggle'
    Assert-True (Get-UiaChecked 'GridSnapToggle') 'snap toggle returns on'
    Invoke-Uia 'GridIncludeInExportToggle'
    Assert-True (Get-UiaChecked 'GridIncludeInExportToggle') 'include-in-export turns on'
    Add-Action 'toggled snap off/on and include-in-export on through standard UIA TogglePattern'

    Write-Host "`n[6] Prove locked state is factual, readable and read-only"
    Invoke-Uia 'LayerLockButton'
    Wait-State { -not (Require-Uia 'GridCellSizeBox').Current.IsEnabled } 'grid editors become read-only'
    Assert-Equal (Get-UiaValue 'GridCellSizeBox') '96.5' 'locked value remains readable'
    Invoke-Uia 'LayerLockButton'
    Wait-State { (Require-Uia 'GridCellSizeBox').Current.IsEnabled } 'grid editors unlock'
    Assert-True (Require-Uia 'GridCellSizeBox').Current.IsEnabled 'grid editor is enabled after unlock'
    Scroll-UiaIntoView 'GridRotationBox'
    if ($Background) { [void][Stage10ENative]::EnableWindow($script:WindowHandle, $true) }
    try {
        (Require-Uia 'GridRotationBox').SetFocus()
        Start-Sleep -Milliseconds 200
        Assert-True (Require-Uia 'GridRotationBox').Current.HasKeyboardFocus 'programmatic focus reaches rotation field'
        Save-Shot '05-custom-properties.png'
    }
    finally { if ($Background) { [void][Stage10ENative]::EnableWindow($script:WindowHandle, $false) } }

    Write-Host "`n[7] Snap an object, its first vector node and its first portal"
    Invoke-Uia 'LineToolButton'
    Wait-Uia 'ContextPanelTitle' { param($text) $text -like '*Линия*' } | Out-Null
    Invoke-Uia 'AddLineButton'
    Wait-Uia 'ContextPanelTitle' { param($text) $text -like '*Свойства*' } | Out-Null
    Save-Map
    $beforeSnap = Get-SceneObject
    $lineBefore = Get-FirstLine $beforeSnap
    $objectId = [string]$lineBefore.id
    $nodeId = [string]@($lineBefore.line.nodes)[0].id
    Invoke-Uia 'GridSnapObjectButton'
    Save-Map
    $afterObjectSnap = Get-SceneObject
    $lineAfterObject = Get-FirstLine $afterObjectSnap
    $grid = Get-SceneGrid $afterObjectSnap
    Assert-Equal ([string]$lineAfterObject.id) $objectId 'object ID is stable after snap'
    $centreResidual = Get-IsoLatticeResidual `
        ([double]$lineAfterObject.x + [double]$lineAfterObject.width / 2) `
        ([double]$lineAfterObject.y + [double]$lineAfterObject.height / 2) $grid
    Assert-Near $centreResidual 0 0.000001 'snapped object centre lattice residual'
    Set-UiaExpanded 'LineGeometry'
    Invoke-Uia 'GridSnapNodeButton'
    Save-Map
    $afterNodeSnap = Get-SceneObject
    $lineAfterNode = Get-FirstLine $afterNodeSnap
    $nodeAfter = @($lineAfterNode.line.nodes)[0]
    Assert-Equal ([string]$lineAfterNode.id) $objectId 'object ID is stable after node snap'
    Assert-Equal ([string]$nodeAfter.id) $nodeId 'node ID is stable after node snap'
    $nodeWorld = Get-NodeWorld $lineAfterNode $nodeAfter
    Assert-Near (Get-IsoLatticeResidual $nodeWorld.X $nodeWorld.Y (Get-SceneGrid $afterNodeSnap)) `
        0 0.000001 'snapped node lattice residual'
    Invoke-Uia 'AddPortalButton'
    Save-Map
    $beforePortalSnap = Get-SceneObject
    $lineWithPortal = Get-FirstLine $beforePortalSnap
    $portalBefore = @($lineWithPortal.line.portals)[0]
    $portalId = [string]$portalBefore.id
    $nodeIdsBeforePortal = @($lineWithPortal.line.nodes | ForEach-Object { [string]$_.id }) -join ','
    Invoke-Uia 'GridSnapPortalButton'
    Save-Map
    $afterPortalSnap = Get-SceneObject
    $lineAfterPortal = Get-FirstLine $afterPortalSnap
    $portalAfter = @($lineAfterPortal.line.portals)[0]
    Assert-Equal ([string]$lineAfterPortal.id) $objectId 'object ID is stable after portal snap'
    Assert-Equal ([string]$portalAfter.id) $portalId 'portal ID is stable after portal snap'
    Assert-Equal (@($lineAfterPortal.line.nodes | ForEach-Object { [string]$_.id }) -join ',') `
        $nodeIdsBeforePortal 'all node IDs are stable after portal snap'
    $portalWorld = Get-PortalWorld $lineAfterPortal $portalAfter
    Assert-Near (Get-IsoLatticeResidual $portalWorld.X $portalWorld.Y (Get-SceneGrid $afterPortalSnap)) `
        0 0.000001 'snapped portal lattice residual'
    Add-Action 'snapped object, first vector node and first portal with exact coordinate and stable-ID checks'

    Write-Host "`n[8] Verify every property directly in scene.json"
    $scene = Get-SceneObject
    $grid = Get-SceneGrid $scene
    Assert-Equal ([string]$grid.geometry) 'IsometricSquares' 'persisted geometry'
    Assert-Near ([double]$grid.cellSize) 96.5 0.0000001 'persisted cell size'
    Assert-Equal ([int]$grid.subdivisions) 4 'persisted subdivisions'
    Assert-Near ([double]$grid.lineWidth) 2.75 0.0000001 'persisted line width'
    Assert-Equal ([string]$grid.color) '#CC224466' 'persisted line color'
    Assert-Near ([double]$grid.opacity) 0.65 0.0000001 'persisted opacity'
    Assert-Near ([double]$grid.offsetX) 13.5 0.0000001 'persisted offset X'
    Assert-Near ([double]$grid.offsetY) -17.25 0.0000001 'persisted offset Y'
    Assert-Near ([double]$grid.rotationDegrees) 27 0.0000001 'persisted rotation'
    Assert-True ([bool]$grid.snapEnabled) 'persisted snap enabled'
    Assert-True ([bool]$grid.includeInExport) 'persisted export enabled'
    $script:SaveReopen['sceneJsonVerified'] = $true

    Write-Host "`n[9] Close, reopen and verify the same native property state"
    Stop-TestApp
    Start-TestApp $savedMap
    Wait-Uia 'DashboardTitle' { param($text) $text -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'OpenFileButton'
    Wait-Uia 'EditorSideTabs' { param($text) $true } | Out-Null
    Select-UiaTab 'Слои'
    $gridRow = Find-RowLike 'Сетка'
    if ($null -ne $gridRow) { Invoke-UiaElement $gridRow 'grid layer row' }
    Wait-State { $null -ne (Find-Uia 'GridCellSizeBox') } 'reopened grid property sheet appears'
    Assert-True (Get-UiaSelected 'GridGeometryIsometricSquaresRadio') 'reopened isometric radio remains selected'
    foreach ($expected in ([ordered]@{
        GridCellSizeBox='96.5'; GridSubdivisionsBox='4'; GridLineWidthBox='2.75';
        GridColorBox='#CC224466'; GridOpacityBox='65'; GridOffsetXBox='13.5';
        GridOffsetYBox='-17.25'; GridRotationBox='27'
    }).GetEnumerator()) {
        Assert-Equal (Get-UiaValue $expected.Key) $expected.Value "reopened $($expected.Key)"
    }
    Assert-True (Get-UiaChecked 'GridSnapToggle') 'reopened snap remains on'
    Assert-True (Get-UiaChecked 'GridIncludeInExportToggle') 'reopened export remains on'
    Scroll-UiaIntoView 'GridRotationBox'
    Save-Shot '06-undo-redo-reopened.png'
    $script:SaveReopen['reopened'] = $true
    $script:SaveReopen['valuesMatched'] = $true
    Add-Action 'closed and reopened the saved map, then verified every visible value and toggle'

    Write-Host "`n[10] Produce real full-canvas exports with the grid included and excluded"
    Invoke-GoldenExport $exportWithGrid
    Assert-Equal (Read-ImageDimensions $exportWithGrid) '1600x1000' 'export-with-grid dimensions'
    Invoke-Uia 'GridIncludeInExportToggle'
    Wait-State { -not (Get-UiaChecked 'GridIncludeInExportToggle') } 'include-in-export turns off'
    Save-Map
    Invoke-GoldenExport $exportWithoutGrid
    Assert-Equal (Read-ImageDimensions $exportWithoutGrid) '1600x1000' 'export-without-grid dimensions'
    $exportRmse = Measure-ScreenshotDifference $exportWithGrid $exportWithoutGrid $exportDiff
    Assert-True ($exportRmse -gt 0.01) 'exports with and without the grid differ materially' $exportRmse
    Invoke-Uia 'GridIncludeInExportToggle'
    Save-Map
    Add-Action 'rendered and pixel-compared 1600x1000 exports with IncludeInExport on and off'

    Write-Host "`n[11] Clean shutdown and process cleanup proof"
    Stop-TestApp
    $left = @(Get-Process -Name 'Map.App' -ErrorAction SilentlyContinue)
    $script:ProcessCleanup = $left.Count -eq 0
    Assert-True $script:ProcessCleanup 'no Map.App process is left behind' $left.Count

    # Raw keyboard and alternate Windows scales are deliberately not simulated. UIA focus, roles,
    # names, selected/checked state and disabled/readable behavior are factual above.
    $script:Partial.Add('A real physical Tab traversal was not injected in background mode; UIA programmatic focus and standard control roles were verified.')
    $script:Partial.Add("Only the actual current Windows scale ($($script:WindowEvidence.scale)) was observed; 150% and 200% were not simulated.")

    $report = [ordered]@{
        result = 'PASS_WITH_PARTIALS'
        executable = [ordered]@{ path=$Exe; sha256=$exeHash; processPath=$Exe; processId='owned and terminated' }
        git = [ordered]@{ branch=$branch; head=$head; workspace=$workspace }
        window = $script:WindowEvidence
        actions = $script:Actions
        checks = $script:Checks
        geometryScreenshotRmse = $geometryRmse
        paths = [ordered]@{
            mapProject=$savedMap
            screenshots=$script:Shots
            exportWithGrid=$exportWithGrid
            exportWithoutGrid=$exportWithoutGrid
            exportDifference=$exportDiff
            geometryDifferences=@(Get-ChildItem -LiteralPath $OutDir -Filter 'diff-*-vs-*.png' | ForEach-Object FullName)
        }
        saveReopen = $script:SaveReopen
        processCleanup = $script:ProcessCleanup
        partial = $script:Partial
        blocked = $script:Blocked
        finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host "`nPASS_WITH_PARTIALS  $($script:Checks.Count) checks"
    Write-Host "Report: $(Join-Path $OutDir 'report.json')"
}
catch {
    try {
        if ($script:WindowHandle -ne [IntPtr]::Zero) { Save-Shot 'failure-state.png' }
    }
    catch { Write-Host "Failure screenshot unavailable: $($_.Exception.Message)" }
    $report = [ordered]@{
        result = 'FAIL'
        error = $_.Exception.Message
        at = $_.ScriptStackTrace
        executable = $Exe
        outDir = $OutDir
        actions = $script:Actions
        shots = $script:Shots
        checks = $script:Checks
        partial = $script:Partial
        blocked = $script:Blocked
        processCleanup = $script:ProcessCleanup
    }
    $report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host "FAIL: $($_.Exception.Message)"
    throw
}
finally {
    Stop-TestApp
    Remove-Item Env:MAP_UI_TEST_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_RECOVERY_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_MAP_PATH -ErrorAction SilentlyContinue
}
