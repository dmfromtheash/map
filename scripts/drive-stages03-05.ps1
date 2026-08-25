<#
.SYNOPSIS
Bounded native-window smoke for the Stage 3-5 editor surface.

.DESCRIPTION
Creates a 65,536 square map, exercises brush/mask/land gestures, land shapes and
nondestructive layer controls through Windows UI Automation, captures one real-window PNG,
and writes machine-readable evidence under artifacts. Asset decode/import is intentionally
covered by the Stage 4 automated benchmark; this smoke verifies the native asset controls.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $scriptRoot '..\src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) { $OutDir = Join-Path $scriptRoot '..\artifacts\stages03-05\ui-smoke' }
$Exe = [IO.Path]::GetFullPath($Exe)
$OutDir = [IO.Path]::GetFullPath($OutDir)

if (-not (Test-Path -LiteralPath $Exe)) { throw "Release executable not found: $Exe" }
[IO.Directory]::CreateDirectory($OutDir) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MapSmokeInput
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
}
"@

$process = $null

function Get-ProcessWindows {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}

function Find-Element([string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    foreach ($window in Get-ProcessWindows) {
        $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $found) { return $found }
    }
    return $null
}

function Wait-Element([string]$automationId, [int]$timeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $found = Find-Element $automationId
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 200
    }
    throw "TIMEOUT: UIA element '$automationId' did not appear."
}

function Set-ElementValue([string]$automationId, [string]$value) {
    $element = Wait-Element $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
}

function Invoke-Element([string]$automationId) {
    $element = Wait-Element $automationId
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    else {
        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "Element '$automationId' has no clickable bounds." }
        [MapSmokeInput]::SetCursorPos([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2)) | Out-Null
        [MapSmokeInput]::mouse_event([MapSmokeInput]::LeftDown, 0, 0, 0, [IntPtr]::Zero)
        [MapSmokeInput]::mouse_event([MapSmokeInput]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 250
}

function Invoke-CanvasGesture([System.Windows.Automation.AutomationElement]$canvas) {
    $rect = $canvas.Current.BoundingRectangle
    if ($rect.Width -lt 100 -or $rect.Height -lt 100) { throw 'Canvas UIA bounds are invalid.' }
    $x = [int]($rect.X + ($rect.Width * 0.55))
    $y = [int]($rect.Y + ($rect.Height * 0.50))
    [MapSmokeInput]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    [MapSmokeInput]::SetCursorPos($x, $y) | Out-Null
    [MapSmokeInput]::mouse_event([MapSmokeInput]::LeftDown, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [MapSmokeInput]::mouse_event([MapSmokeInput]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Select-SideTab([int]$index) {
    $tabs = $null
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    foreach ($window in Get-ProcessWindows) {
        $found = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found.Count -ge 3) { $tabs = $found; break }
    }
    if ($null -eq $tabs -or $tabs.Count -le $index) { throw "Side tab index $index is unavailable." }
    $pattern = $tabs[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 300
}

function Get-LayerEvidence {
    $items = [Collections.Generic.List[string]]::new()
    foreach ($window in Get-ProcessWindows) {
        $all = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $all) {
            if ($element.Current.AutomationId -like 'Layer_*') {
                $items.Add($element.Current.Name)
            }
        }
    }
    return @($items | Sort-Object -Unique)
}

function Save-WindowCapture([string]$path) {
    $process.Refresh()
    $rect = New-Object MapSmokeInput+RECT
    [MapSmokeInput]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Unexpected window bounds ${width}x${height}." }
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object Drawing.Size($width, $height)))
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    return "${width}x${height}"
}

try {
    $process = Start-Process -FilePath $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $process.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw 'Map.App did not create a native window.' }

    Invoke-Element 'NewMapButton'
    Set-ElementValue 'WizardTitleBox' 'Stages 03-05 smoke'
    Set-ElementValue 'WizardWidthBox' '65536'
    Set-ElementValue 'WizardHeightBox' '65536'
    Invoke-Element 'CreateMapButton'

    $canvas = Wait-Element 'MapCanvas' 30000
    if ($canvas.Current.ControlType -ne [System.Windows.Automation.ControlType]::Pane) {
        throw "Canvas UIA control type is '$($canvas.Current.ControlType.ProgrammaticName)', expected Pane."
    }

    Invoke-Element 'AddBrushLayerButton'
    Invoke-Element 'BrushToolButton'
    Invoke-CanvasGesture $canvas
    Invoke-Element 'LayerMaskButton'
    Invoke-Element 'MaskToolButton'
    Invoke-CanvasGesture $canvas

    Invoke-Element 'AddLandLayerButton'
    Invoke-Element 'LandRectangleButton'
    Invoke-Element 'LandStyleButton'
    Invoke-Element 'LayerBlendButton'
    Invoke-Element 'LandMoveButton'
    Invoke-Element 'LandToolButton'
    Invoke-CanvasGesture $canvas
    Invoke-Element 'LayerMaskButton'
    Invoke-Element 'UndoButton'
    Invoke-Element 'RedoButton'

    foreach ($required in @('EditorSideTabs', 'LandPolygonButton', 'LandFillButton')) {
        Wait-Element $required | Out-Null
    }

    $layers = @(Get-LayerEvidence)
    if ($layers.Count -lt 3) { throw "Expected background, brush and land layers; found $($layers.Count)." }

    Select-SideTab 1
    Wait-Element 'ObjectSearchBox' | Out-Null
    Select-SideTab 2
    foreach ($required in @('AssetSearchBox', 'ImportAssetButton', 'PlaceStampButton', 'ScatterStampButton')) {
        Wait-Element $required | Out-Null
    }

    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $process.Id })
    if ($listeners.Count -ne 0) { throw 'The offline editor opened a TCP listener.' }

    $capturePath = Join-Path $OutDir '01-stages03-05-editor.png'
    $captureSize = Save-WindowCapture $capturePath
    $evidence = [ordered]@{
        status = 'FACT'
        recordedUtc = [DateTimeOffset]::UtcNow
        processId = $process.Id
        executable = $Exe
        canvas = [ordered]@{
            name = $canvas.Current.Name
            automationId = $canvas.Current.AutomationId
            controlType = $canvas.Current.ControlType.ProgrammaticName
        }
        requestedCanvas = '65536x65536'
        exercised = @('brush gesture', 'mask gesture', 'land rectangle', 'land brush gesture', 'land style', 'land move', 'layer blend', 'undo/redo')
        assetUi = 'controls present; import/decode covered outside this smoke'
        layers = $layers
        tcpListeners = 0
        screenshot = [IO.Path]::GetFileName($capturePath)
        screenshotSize = $captureSize
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'evidence.json') -Encoding UTF8
    Write-Host "Stage 03-05 smoke passed. Evidence: $OutDir"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(10000) | Out-Null
    }
}
