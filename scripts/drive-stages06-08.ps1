<# Bounded native UIA smoke for Stages 6-8. Evidence is written only under ignored artifacts. #>
[CmdletBinding()]
param([string]$Exe, [string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $root '..\src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root '..\artifacts\stages06-08\ui-smoke' }
$Exe = [IO.Path]::GetFullPath($Exe); $OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path -LiteralPath $Exe)) { throw "Release executable not found: $Exe" }
[IO.Directory]::CreateDirectory($OutDir) | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Stage8Capture {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
"@

$process = $null
function Windows {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}
function Find-Id([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    foreach ($window in Windows) {
        $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $item) { return $item }
    }
    return $null
}
function Wait-Id([string]$id, [int]$timeout = 15000) {
    $end = (Get-Date).AddMilliseconds($timeout)
    while ((Get-Date) -lt $end) {
        $item = Find-Id $id; if ($null -ne $item) { return $item }
        Start-Sleep -Milliseconds 150
    }
    throw "TIMEOUT: '$id'"
}
function Invoke-Id([string]$id) {
    $item = Wait-Id $id; $pattern = $null
    if (-not $item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "'$id' has no InvokePattern"
    }
    $pattern.Invoke(); Start-Sleep -Milliseconds 180
}
function Set-Value([string]$id, [string]$value) {
    $item = Wait-Id $id
    $pattern = $item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
}
function Select-Tab([int]$index) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $tabs = Wait-Id 'EditorSideTabs'
    $items = $tabs.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($items.Count -gt $index) {
            $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 200; return
    }
    throw "Tab index '$index' is absent."
}
function Save-Capture([string]$path) {
    $process.Refresh(); $rect = New-Object Stage8Capture+RECT
    [Stage8Capture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $width = $rect.Right-$rect.Left; $height = $rect.Bottom-$rect.Top
    $bitmap = New-Object Drawing.Bitmap($width,$height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,(New-Object Drawing.Size($width,$height)))
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    return "${width}x${height}"
}

try {
    $process = Start-Process -FilePath $Exe -PassThru
    Wait-Id 'NewMapButton' 20000 | Out-Null
    Invoke-Id 'NewMapButton'; Set-Value 'WizardTitleBox' 'Stages 06-08 smoke'
    Set-Value 'WizardWidthBox' '65536'; Set-Value 'WizardHeightBox' '65536'; Invoke-Id 'CreateMapButton'
    Wait-Id 'MapCanvas' 30000 | Out-Null

    Invoke-Id 'AddLineButton'; Select-Tab 1; Invoke-Id 'MoveVectorNodeButton'; Invoke-Id 'AddPortalButton'
    Invoke-Id 'AddShapeEllipseButton'; Invoke-Id 'AddShapeEllipseButton'; Invoke-Id 'BooleanSubtractButton'
    Invoke-Id 'UndoButton'; Invoke-Id 'RedoButton'
    Invoke-Id 'AddTextButton'; Select-Tab 1; Invoke-Id 'TextMissingFontButton'
    Select-Tab 0; Invoke-Id 'AddGridLayerButton'; Invoke-Id 'GridGeometryButton'; Invoke-Id 'GridSnapObjectButton'
    Invoke-Id 'AddFilterLayerButton'; Invoke-Id 'LayerMaskButton'; Invoke-Id 'FilterStyleButton'
    Select-Tab 3; Invoke-Id 'AddWorldNoteButton'
    Select-Tab 1; Invoke-Id 'MakeCompositionButton'; Invoke-Id 'CopyCompositionButton'
    Invoke-Id 'PasteCompositionButton'; Invoke-Id 'FlattenCompositionButton'

    $capture = Join-Path $OutDir '01-stages06-08-editor.png'
    $size = Save-Capture $capture
    Invoke-Id 'SecondWindowButton'; Start-Sleep -Milliseconds 500
    $windowCount = @(Windows).Count
    if ($windowCount -lt 2) { throw 'Second native map window did not appear.' }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object OwningProcess -eq $process.Id)
    if ($listeners.Count -ne 0) { throw 'Offline editor opened a TCP listener.' }
    [ordered]@{
        status='FACT'; recordedUtc=[DateTimeOffset]::UtcNow; executable=$Exe; processId=$process.Id
        requestedCanvas='65536x65536'; nativeWindowCount=$windowCount; tcpListeners=0
        exercised=@('line/node/portal','shape boolean undo/redo','missing-font text','note','grid snap','masked filter','editable composition','process clipboard paste','explicit flatten')
        automatedEvidence='cross-document structure/remap is additionally covered by two-EditorViewModel tests'
        partial=@('second window presence verified, but file-dialog save/reopen not driven in this bounded smoke')
        screenshot=[IO.Path]::GetFileName($capture); screenshotSize=$size
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'evidence.json') -Encoding UTF8
    Write-Host "Stages 06-08 smoke passed: $OutDir"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill(); $process.WaitForExit(10000) | Out-Null
    }
}
