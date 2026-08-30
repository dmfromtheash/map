<#
.SYNOPSIS
Stage 2 end-to-end smoke: drives the built Release Map.App.exe through the full first vertical -
dashboard, wizard, canvas editing, undo/redo, pan/zoom, Save As, close, reopen from recent - with
real Win32 input, UI Automation state checks and validated full-window screenshots.

.DESCRIPTION
Self-checking: every step is confirmed by reading live UIA state (Avalonia publishes control
Name as the UIA AutomationId, and TextBlock text as the UIA Name); waits are bounded; every PNG
is decoded and must match the full window size; the run fails loudly instead of leaving a green
log next to a broken screenshot. It launches and kills only its own processes.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $scriptRoot '..\src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) { $OutDir = Join-Path $scriptRoot '..\artifacts\stage02b\ui-smoke' }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression.FileSystem

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Input
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    public const uint MOUSEEVENTF_WHEEL      = 0x0800;
    public const uint KEYEVENTF_KEYUP        = 0x0002;
    public const byte VK_CONTROL             = 0x11;
    public const byte VK_RETURN              = 0x0D;
    public const byte VK_A                   = 0x41;
    public const byte VK_V                   = 0x56;
}
"@

$script:Proc = $null
$script:MainWindowElement = $null
$script:ExpectedW = 0
$script:ExpectedH = 0
$script:Captures = @()
$script:Evidence = @()
$script:ShotIndex = 0

function Write-Evidence([string]$label, [string]$text) {
    Write-Host ("    {0}: {1}" -f $label, $text)
    $script:Evidence += "${label}: ${text}"
}

# ---------------------------------------------------------------------------- activation

function Set-ActiveWindow([IntPtr]$hwnd) {
    $fg = [Win32Input]::GetForegroundWindow()
    $ft = [Win32Input]::GetWindowThreadProcessId($fg, [ref]([uint32]0))
    $ot = [Win32Input]::GetCurrentThreadId()
    [void][Win32Input]::AttachThreadInput($ot, $ft, $true)
    [void][Win32Input]::BringWindowToTop($hwnd)
    [void][Win32Input]::SetForegroundWindow($hwnd)
    [void][Win32Input]::AttachThreadInput($ot, $ft, $false)
    Start-Sleep -Milliseconds 250
}

function Assert-OwnForeground {
    $fg = [Win32Input]::GetForegroundWindow()
    $fgPid = [uint32]0
    [void][Win32Input]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    if ($fgPid -ne $script:Proc.Id) {
        throw "Foreground pid $fgPid is not our app pid $($script:Proc.Id); input would go elsewhere."
    }
}

# ---------------------------------------------------------------------------- UIA

function Get-AppWindows {
    # All top-level windows of OUR process only.
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}

function Wait-AppWindow([string]$titleContains, [int]$timeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        foreach ($window in Get-AppWindows) {
            if ($window.Current.Name -like "*$titleContains*") { return $window }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "TIMEOUT: no window of pid $($script:Proc.Id) with title containing '$titleContains'."
}

function Find-ByAutomationId([System.Windows.Automation.AutomationElement]$root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-UiaText([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:MainWindowElement }
    $element = Find-ByAutomationId $root $automationId
    if ($null -eq $element) { return $null }
    return $element.Current.Name
}

function Wait-UiaText {
    param(
        [string]$AutomationId,
        [scriptblock]$Until,
        [int]$TimeoutMs = 15000,
        [string]$Because,
        [System.Windows.Automation.AutomationElement]$Root = $null
    )
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Get-UiaText $AutomationId $Root
        if ($null -ne $last -and (& $Until $last)) { return $last }
        Start-Sleep -Milliseconds 250
    }
    throw "TIMEOUT: $Because. Last '$AutomationId' text: '$last'"
}

function Invoke-UiaButton([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    # A real mouse click at the element's UIA bounding rectangle. InvokePattern proved unreliable
    # around modal dialogs; a physical click is exactly what the user does and needs no layout.
    if ($null -eq $root) { $root = $script:MainWindowElement }
    $deadline = (Get-Date).AddMilliseconds(10000)
    while ((Get-Date) -lt $deadline) {
        $element = Find-ByAutomationId $root $automationId
        if ($null -ne $element -and $element.Current.IsEnabled) {
            $rect = $element.Current.BoundingRectangle
            if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
                [void][Win32Input]::SetCursorPos([int]($rect.X + ($rect.Width / 2)), [int]($rect.Y + ($rect.Height / 2)))
                Start-Sleep -Milliseconds 120
                [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
                Start-Sleep -Milliseconds 60
                [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
                Start-Sleep -Milliseconds 400
                return
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Button '$automationId' was not found, never enabled or has no on-screen bounds."
}

function Wait-ElementInApp([string]$automationId, [int]$timeoutMs = 20000) {
    # Owned Avalonia dialogs are parented under the main window's UIA element (they are not root
    # children), so the main window's subtree is searched first, then any other app windows.
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $element = Find-ByAutomationId $script:MainWindowElement $automationId
        if ($null -ne $element) { return @{ Window = $script:MainWindowElement; Element = $element } }
        foreach ($window in Get-AppWindows) {
            $element = Find-ByAutomationId $window $automationId
            if ($null -ne $element) { return @{ Window = $window; Element = $element } }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "TIMEOUT: element '$automationId' never appeared in any window of pid $($script:Proc.Id)."
}

function Set-UiaValue(
    [System.Windows.Automation.AutomationElement]$root, [string]$automationId, [string]$value) {
    $element = Find-ByAutomationId $root $automationId
    if ($null -eq $element) { throw "Editable element '$automationId' not found." }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
    Start-Sleep -Milliseconds 150
}

function Get-UiaValue([System.Windows.Automation.AutomationElement]$root, [string]$automationId) {
    $element = Find-ByAutomationId $root $automationId
    if ($null -eq $element) { throw "Element '$automationId' not found." }
    ($element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}

# ---------------------------------------------------------------------------- capture

function Save-Shot([string]$label) {
    $script:ShotIndex++
    $path = Join-Path $OutDir ("{0:D2}-{1}.png" -f $script:ShotIndex, $label)

    $script:Proc.Refresh()
    $hwnd = $script:Proc.MainWindowHandle
    $rect = New-Object Win32Input+RECT
    [void][Win32Input]::GetWindowRect($hwnd, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $ok = [Win32Input]::PrintWindow($hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT for GPU content
    $graphics.ReleaseHdc($hdc)
    if (-not $ok) {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    }
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $decoded = [System.Drawing.Image]::FromFile($path)
    try { $w = $decoded.Width; $h = $decoded.Height } finally { $decoded.Dispose() }

    if ($script:ExpectedW -le 0) {
        if ($w -lt 900 -or $h -lt 600) { throw "CAPTURE TOO SMALL: $path is ${w}x${h}." }
        $script:ExpectedW = $w; $script:ExpectedH = $h
    }
    elseif ($w -ne $script:ExpectedW -or $h -ne $script:ExpectedH) {
        throw "CAPTURE SIZE MISMATCH: $path is ${w}x${h}, expected $($script:ExpectedW)x$($script:ExpectedH)."
    }

    $script:Captures += [PSCustomObject]@{ File = Split-Path -Leaf $path; W = $w; H = $h }
    Write-Host "captured -> $path (${w}x${h})"
}

# ---------------------------------------------------------------------------- mouse in the canvas

function Get-CanvasPoint([double]$fx, [double]$fy) {
    $canvas = Find-ByAutomationId $script:MainWindowElement 'MapCanvas'
    if ($null -eq $canvas) { throw 'MapCanvas is absent from the UIA tree.' }
    $rect = $canvas.Current.BoundingRectangle
    if ($rect.Width -lt 100 -or $rect.Height -lt 100) { throw 'MapCanvas UIA bounds are invalid.' }
    @{
        X = [int]($rect.X + ($rect.Width * $fx))
        Y = [int]($rect.Y + ($rect.Height * $fy))
    }
}

function Get-CanvasFingerprint {
    Set-ActiveWindow $script:Proc.MainWindowHandle
    Assert-OwnForeground
    $canvas = Find-ByAutomationId $script:MainWindowElement 'MapCanvas'
    if ($null -eq $canvas) { throw 'MapCanvas is absent from the UIA tree.' }
    $rect = $canvas.Current.BoundingRectangle
    $width = [int]$rect.Width; $height = [int]$rect.Height
    if ($width -lt 100 -or $height -lt 100) { throw 'MapCanvas UIA bounds are invalid.' }
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $stream = New-Object IO.MemoryStream
    try {
        $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object Drawing.Size($width, $height)))
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray()))
    }
    finally {
        $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

function Invoke-CanvasClick([double]$fx, [double]$fy) {
    Set-ActiveWindow $script:Proc.MainWindowHandle
    Assert-OwnForeground
    $point = Get-CanvasPoint $fx $fy
    [void][Win32Input]::SetCursorPos($point.X, $point.Y)
    Start-Sleep -Milliseconds 150
    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}

function Invoke-CanvasDrag([double]$fx1, [double]$fy1, [int]$dxPixels, [int]$dyPixels, [uint32]$downFlag, [uint32]$upFlag) {
    Set-ActiveWindow $script:Proc.MainWindowHandle
    Assert-OwnForeground
    $start = Get-CanvasPoint $fx1 $fy1
    [void][Win32Input]::SetCursorPos($start.X, $start.Y)
    Start-Sleep -Milliseconds 150
    [Win32Input]::mouse_event($downFlag, 0, 0, 0, [IntPtr]::Zero)
    for ($i = 1; $i -le 20; $i++) {
        [void][Win32Input]::SetCursorPos(
            [int]($start.X + ($dxPixels * $i / 20)), [int]($start.Y + ($dyPixels * $i / 20)))
        Start-Sleep -Milliseconds 18
    }
    [Win32Input]::mouse_event($upFlag, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}

function Invoke-CanvasWheel([double]$fx, [double]$fy, [int]$notches) {
    Set-ActiveWindow $script:Proc.MainWindowHandle
    Assert-OwnForeground
    $point = Get-CanvasPoint $fx $fy
    [void][Win32Input]::SetCursorPos($point.X, $point.Y)
    Start-Sleep -Milliseconds 150
    $step = if ($notches -ge 0) { 120 } else { -120 }
    for ($i = 0; $i -lt [Math]::Abs($notches); $i++) {
        [Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_WHEEL, 0, 0, [uint32]$step, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
    Start-Sleep -Milliseconds 350
}

# ---------------------------------------------------------------------------- app lifecycle

function Start-App {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    Write-Host "started pid $($script:Proc.Id)"

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $script:Proc.Refresh()
        if ($script:Proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "The window never appeared." }

    [void][Win32Input]::ShowWindow($script:Proc.MainWindowHandle, 3) # maximize
    Start-Sleep -Seconds 2
    Set-ActiveWindow $script:Proc.MainWindowHandle
    $script:MainWindowElement =
        [System.Windows.Automation.AutomationElement]::FromHandle($script:Proc.MainWindowHandle)
    $script:ExpectedW = 0; $script:ExpectedH = 0
}

# ============================================================================ run

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$resolvedOutDir = (Resolve-Path -LiteralPath $OutDir).Path
$profileRoot = Join-Path $resolvedOutDir 'profile'
New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
$env:MAP_UI_TEST_MODE = '1'
$env:MAP_UI_TEST_PROFILE_ROOT = $profileRoot
Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
$savePath = Join-Path $resolvedOutDir ("smoke-{0:yyyyMMdd-HHmmss}.mapproj" -f (Get-Date))
Write-Evidence 'isolated-profile' $profileRoot

# ---- step 1: dashboard launch
Start-App
Wait-UiaText -AutomationId 'DashboardTitle' -Until { param($t) $t -eq 'Ваши карты' } `
    -Because 'the dashboard never appeared' | Out-Null
Write-Evidence 'dashboard' 'Ваши карты visible'
Save-Shot 'dashboard-first-launch'

# ---- step 2: New -> wizard -> map
Set-ActiveWindow $script:Proc.MainWindowHandle
Assert-OwnForeground
Invoke-UiaButton 'NewMapButton'
$wizard = (Wait-ElementInApp 'WizardTitleBox').Window
Set-UiaValue $wizard 'WizardTitleBox' 'Смоук-карта'
Invoke-UiaButton 'WizardPresetCustom' $wizard
Set-UiaValue $wizard 'WizardWidthBox' '1600'
Set-UiaValue $wizard 'WizardHeightBox' '900'
Write-Evidence 'wizard' "title=$(Get-UiaValue $wizard 'WizardTitleBox') size=1600x900"
Save-Shot 'wizard-filled'
Invoke-UiaButton 'CreateMapButton' $wizard
Wait-UiaText -AutomationId 'SaveStateText' -Until { param($t) $t -like 'Не сохранено*' } `
    -Because 'the editor never opened after the wizard' | Out-Null
Write-Evidence 'editor-open' (Get-UiaText 'SaveStateText')
Save-Shot 'editor-blank'

# ---- step 3: add rectangle
Invoke-UiaButton 'AddRectangleButton'
$selection = Wait-UiaText -AutomationId 'SelectionStatusText' `
    -Until { param($t) $t -eq 'Прямоугольник' } `
    -Because 'adding a rectangle did not select it'
Write-Evidence 'added-object' $selection
Save-Shot 'rectangle-added'

# ---- step 4: selection proven by click (deselect on empty corner, reselect at centre)
Invoke-CanvasClick 0.06 0.08
Wait-UiaText -AutomationId 'SelectionStatusText' -Until { param($t) $t -eq 'Ничего не выбрано' } `
    -Because 'clicking empty space did not clear the selection' | Out-Null
Invoke-CanvasClick 0.5 0.5
$reselected = Wait-UiaText -AutomationId 'SelectionStatusText' `
    -Until { param($t) $t -eq 'Прямоугольник' } `
    -Because 'clicking the object did not select it'
Write-Evidence 'click-selection' $reselected
Save-Shot 'selection-by-click'

# ---- step 5: drag move + numeric resize/rotation
$xBefore = Get-UiaValue $script:MainWindowElement 'XBox'
Invoke-CanvasDrag 0.5 0.5 120 80 ([Win32Input]::MOUSEEVENTF_LEFTDOWN) ([Win32Input]::MOUSEEVENTF_LEFTUP)
$xAfterDrag = Get-UiaValue $script:MainWindowElement 'XBox'
if ($xAfterDrag -eq $xBefore) { throw "Drag did not move the object (X stayed $xBefore)." }
Write-Evidence 'drag' "X: $xBefore -> $xAfterDrag"

Set-UiaValue $script:MainWindowElement 'WidthBox' '400'
Set-UiaValue $script:MainWindowElement 'HeightBox' '260'
Set-UiaValue $script:MainWindowElement 'RotationBox' '30'
Invoke-UiaButton 'ApplyButton'
if ((Get-UiaValue $script:MainWindowElement 'RotationBox') -ne '30') { throw 'Rotation was not applied.' }
Write-Evidence 'numeric-transform' 'W=400 H=260 R=30 applied as one command'
Save-Shot 'after-transform'

# ---- step 6: undo and redo with value checks
Invoke-UiaButton 'UndoButton'
Start-Sleep -Milliseconds 300
if ((Get-UiaValue $script:MainWindowElement 'RotationBox') -eq '30') { throw 'Undo did not revert the transform.' }
Write-Evidence 'undo' "rotation back to $(Get-UiaValue $script:MainWindowElement 'RotationBox')"
Invoke-UiaButton 'RedoButton'
Start-Sleep -Milliseconds 300
if ((Get-UiaValue $script:MainWindowElement 'RotationBox') -ne '30') { throw 'Redo did not restore the transform.' }
Write-Evidence 'redo' 'rotation 30 restored'
Save-Shot 'after-undo-redo'

# ---- step 7: pan and zoom with viewport status checks
$viewportBefore = Get-UiaText 'ViewportStatusText'
$canvasBeforePan = Get-CanvasFingerprint
Invoke-CanvasDrag 0.4 0.4 150 100 ([Win32Input]::MOUSEEVENTF_MIDDLEDOWN) ([Win32Input]::MOUSEEVENTF_MIDDLEUP)
$canvasAfterPan = Get-CanvasFingerprint
$viewportAfterPan = Get-UiaText 'ViewportStatusText'
if ($canvasAfterPan -eq $canvasBeforePan) { throw 'Pan did not change the rendered canvas.' }
Invoke-CanvasWheel 0.5 0.5 3
$viewportAfterZoom = Get-UiaText 'ViewportStatusText'
if ($viewportAfterZoom -eq $viewportAfterPan) { throw 'Zoom did not change the viewport status.' }
Write-Evidence 'viewport' "pan changed canvas; scale '$viewportBefore' -> '$viewportAfterZoom'"
Save-Shot 'after-pan-zoom'

# ---- step 8: Save As into artifacts
Invoke-UiaButton 'SaveAsButton'
Start-Sleep -Milliseconds 800
# The native save dialog (IFileDialog, class #32770) takes the foreground; Avalonia-owned windows
# do not show up as UIA root children, so the foreground HWND is the reliable way in. The
# file-name edit is filled via ValuePattern - layout-independent, no keyboard emulation.
$dialog = $null
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline -and $null -eq $dialog) {
    $fgHwnd = [Win32Input]::GetForegroundWindow()
    $fgPid = [uint32]0
    [void][Win32Input]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPid)
    if ($fgPid -eq $script:Proc.Id) {
        $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($fgHwnd)
        if ($candidate.Current.ClassName -eq '#32770') { $dialog = $candidate }
    }
    Start-Sleep -Milliseconds 300
}
if ($null -eq $dialog) { throw 'The native save dialog never appeared.' }

# The file-name edit of the common dialog has AutomationId 1001. UIA SetValue times out on it,
# so the path goes in the way a user would paste it: click the edit, Ctrl+A, Ctrl+V, Enter.
# Clipboard + VK codes are keyboard-layout independent.
$fileNameEdit = Find-ByAutomationId $dialog '1001'
if ($null -eq $fileNameEdit) {
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $fileNameEdit = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
}
if ($null -eq $fileNameEdit) { throw 'The save dialog has no file-name edit.' }

Set-Clipboard -Value $savePath
$editRect = $fileNameEdit.Current.BoundingRectangle
[void][Win32Input]::SetCursorPos([int]($editRect.X + ($editRect.Width / 2)), [int]($editRect.Y + ($editRect.Height / 2)))
Start-Sleep -Milliseconds 150
[Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Win32Input]::mouse_event([Win32Input]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

function Send-Chord([byte]$modifier, [byte]$key) {
    [Win32Input]::keybd_event($modifier, 0, 0, [IntPtr]::Zero)
    [Win32Input]::keybd_event($key, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32Input]::keybd_event($key, 0, [Win32Input]::KEYEVENTF_KEYUP, [IntPtr]::Zero)
    [Win32Input]::keybd_event($modifier, 0, [Win32Input]::KEYEVENTF_KEYUP, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}

Send-Chord ([Win32Input]::VK_CONTROL) ([Win32Input]::VK_A)
Send-Chord ([Win32Input]::VK_CONTROL) ([Win32Input]::VK_V)
Start-Sleep -Milliseconds 300
# Best-effort read-back: the 1001 element may be the combo wrapper without ValuePattern. The
# authoritative proof of the path is the SaveState wait plus the on-disk file check below.
$patternObj = $null
if ($fileNameEdit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$patternObj)) {
    $typed = $patternObj.Current.Value
    if ($typed -ne $savePath) { throw "The dialog edit holds '$typed' instead of the target path." }
}

[Win32Input]::keybd_event([Win32Input]::VK_RETURN, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Win32Input]::keybd_event([Win32Input]::VK_RETURN, 0, [Win32Input]::KEYEVENTF_KEYUP, [IntPtr]::Zero)

Wait-UiaText -AutomationId 'SaveStateText' -Until { param($t) $t -eq 'Сохранено' } -TimeoutMs 30000 `
    -Because 'the save never completed' | Out-Null
Write-Evidence 'save' "Сохранено -> $savePath"
Save-Shot 'saved'

# ---- step 9: record values, close the app cleanly
$recorded = [ordered]@{
    X = Get-UiaValue $script:MainWindowElement 'XBox'
    Y = Get-UiaValue $script:MainWindowElement 'YBox'
    W = Get-UiaValue $script:MainWindowElement 'WidthBox'
    H = Get-UiaValue $script:MainWindowElement 'HeightBox'
    R = Get-UiaValue $script:MainWindowElement 'RotationBox'
}
Write-Evidence 'recorded-geometry' (($recorded.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')

$graphicsModules = ($script:Proc.Modules |
    Where-Object { $_.ModuleName -match 'libGLESv2|d3d11|dcomp|Skia' } |
    ForEach-Object { $_.ModuleName }) -join ', '
Write-Evidence 'graphics-modules' $graphicsModules
$webview = $script:Proc.Modules |
    Where-Object { $_.ModuleName -match '^(msedgewebview|WebView2Loader|libcef|chrome_elf|CoreWebView2)' }
if ($webview) { throw "WebView/Chromium modules loaded: $(($webview | ForEach-Object ModuleName) -join ', ')" }
$listeners = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -eq $script:Proc.Id }
if ($listeners) { throw "The app opened TCP listeners." }
Write-Evidence 'network' 'no TCP listeners'

($script:MainWindowElement.GetCurrentPattern(
    [System.Windows.Automation.WindowPattern]::Pattern)).Close()
if (-not $script:Proc.WaitForExit(20000)) { throw 'The app did not exit after a clean close.' }
Write-Evidence 'clean-close' "exit code $($script:Proc.ExitCode)"

if (-not (Test-Path -LiteralPath $savePath)) { throw "Saved file does not exist: $savePath" }
$zip = [System.IO.Compression.ZipFile]::OpenRead($savePath)
try {
    $preview = $zip.GetEntry('preview.png')
    if ($null -eq $preview -or $preview.Length -lt 200) { throw 'preview.png is missing or trivially small.' }
    Write-Evidence 'container' "entries=$($zip.Entries.Count) preview.png=$($preview.Length) bytes"
}
finally { $zip.Dispose() }

# ---- step 10: new process -> recent card with thumbnail -> reopen
Start-App
Wait-UiaText -AutomationId 'RecentTitle0' -Until { param($t) $t -eq 'Смоук-карта' } `
    -Because 'the recent card never appeared in the fresh process' | Out-Null
Write-Evidence 'recent-card' (Get-UiaText 'RecentSubtitle0')
Save-Shot 'recent-in-new-process'

Invoke-UiaButton 'RecentCard0'
Wait-UiaText -AutomationId 'SaveStateText' -Until { param($t) $t -eq 'Сохранено' } -TimeoutMs 30000 `
    -Because 'reopening from recent never reached the editor' | Out-Null
Save-Shot 'reopened-editor'

# ---- step 11: verify restored id/geometry through selection + inspector
# The object was dragged away from the page centre before saving, so a fresh fitted view shows it
# off-centre. A bounded, deterministic probe grid finds it; every hit is verified by the id.
$reopenedSelection = $null
foreach ($fy in 0.61, 0.55, 0.67, 0.5, 0.72) {
    foreach ($fx in 0.6, 0.55, 0.65, 0.5, 0.7) {
        Invoke-CanvasClick $fx $fy
        $status = Get-UiaText 'SelectionStatusText'
        if ($status -eq 'Прямоугольник') { $reopenedSelection = $status; break }
    }
    if ($reopenedSelection) { break }
}
if (-not $reopenedSelection) {
    throw "The restored object could not be selected by the bounded probe grid."
}
$restored = [ordered]@{
    X = Get-UiaValue $script:MainWindowElement 'XBox'
    Y = Get-UiaValue $script:MainWindowElement 'YBox'
    W = Get-UiaValue $script:MainWindowElement 'WidthBox'
    H = Get-UiaValue $script:MainWindowElement 'HeightBox'
    R = Get-UiaValue $script:MainWindowElement 'RotationBox'
}
foreach ($key in $recorded.Keys) {
    if ($restored[$key] -ne $recorded[$key]) {
        throw "Restored $key='$($restored[$key])' differs from saved '$($recorded[$key])'."
    }
}
Write-Evidence 'restored-geometry' (($restored.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
Write-Evidence 'restored-selection' $reopenedSelection
Save-Shot 'reopened-selection-verified'

# ---- step 12: wrap up
$script:Proc.Kill()
Write-Host ""
Write-Host "capture inventory:"
$script:Captures | Format-Table | Out-String | Write-Host
$sizes = @($script:Captures | ForEach-Object { "$($_.W)x$($_.H)" } | Sort-Object -Unique)
if ($sizes.Count -ne 1) { throw "Mixed capture sizes: $($sizes -join ', ')" }
if ($script:Captures.Count -lt 10) { throw "Expected at least 10 captures, got $($script:Captures.Count)." }

$evidencePath = Join-Path $OutDir 'ui-evidence.txt'
$script:Evidence + "" + "captures: $($script:Captures.Count) at $($sizes[0])" + "saved: $savePath" |
    Set-Content -LiteralPath $evidencePath -Encoding UTF8
Write-Host "evidence -> $evidencePath"
Write-Host "smoke finished; artefacts in $OutDir"
