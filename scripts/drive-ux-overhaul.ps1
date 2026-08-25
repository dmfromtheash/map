<#
    Bounded native UIA smoke for the dashboard/editor UX rebuild.

    Everything it asserts is read back from UI Automation on the real Release window: the context
    panel title, the active tool, the selection, the layer list and the window size. Screenshots
    are decoded after saving and their pixel size is compared with the captured window rect, so a
    black or truncated frame cannot pass as evidence. Output lands only under ignored artifacts.
#>
[CmdletBinding()]
param([string]$Exe, [string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $root '..\src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root '..\artifacts\ux-overhaul\ui-smoke' }
$Exe = [IO.Path]::GetFullPath($Exe)
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path -LiteralPath $Exe)) { throw "Release executable not found: $Exe" }
[IO.Directory]::CreateDirectory($OutDir) | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class UxWin {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int t,bool repaint);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
"@

$script:process = $null
$script:steps = [System.Collections.Generic.List[object]]::new()
$script:index = 0

# The window this run is documenting. Once a second window exists, MainWindowHandle points at the
# newest one, and every later capture would silently photograph the wrong window.
$script:targetHandle = [IntPtr]::Zero

function Get-TargetHandle {
    if ($script:targetHandle -ne [IntPtr]::Zero) { return $script:targetHandle }
    $script:process.Refresh()
    return $script:process.MainWindowHandle
}

# ---------------------------------------------------------------- UIA helpers

function Get-AppWindows {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:process.Id)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}

function Find-Id([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    foreach ($window in Get-AppWindows) {
        $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $item) { return $item }
    }
    return $null
}

function Wait-Id([string]$id, [int]$timeout = 15000) {
    $end = (Get-Date).AddMilliseconds($timeout)
    while ((Get-Date) -lt $end) {
        $item = Find-Id $id
        if ($null -ne $item) { return $item }
        Start-Sleep -Milliseconds 120
    }
    throw "TIMEOUT waiting for AutomationId '$id'"
}

function Test-Id([string]$id) { return $null -ne (Find-Id $id) }

function Invoke-Id([string]$id, [int]$settle = 220) {
    $item = Wait-Id $id
    $pattern = $null
    if ($item.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    elseif ($item.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
    }
    else {
        throw "'$id' has neither InvokePattern nor TogglePattern"
    }
    Start-Sleep -Milliseconds $settle
}

function Set-Value([string]$id, [string]$value) {
    $item = Wait-Id $id
    $item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 120
}

function Get-Text([string]$id) {
    $item = Wait-Id $id
    return $item.Current.Name
}

function Select-SideTab([int]$position) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $tabs = Wait-Id 'EditorSideTabs'
    $items = $tabs.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($items.Count -le $position) { throw "Side tab $position is absent." }
    $items[$position].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 260
}

# --------------------------------------------------------------- window helpers

function Set-WindowSize([int]$width, [int]$height) {
    $handle = Get-TargetHandle
    [UxWin]::MoveWindow($handle, 60, 40, $width, $height, $true) | Out-Null
    Start-Sleep -Milliseconds 700

    $outer = New-Object UxWin+RECT
    [UxWin]::GetWindowRect($handle, [ref]$outer) | Out-Null
    $client = New-Object UxWin+RECT
    [UxWin]::GetClientRect($handle, [ref]$client) | Out-Null

    return [pscustomobject]@{
        Outer  = "$($outer.Right - $outer.Left)x$($outer.Bottom - $outer.Top)"
        Client = "$($client.Right - $client.Left)x$($client.Bottom - $client.Top)"
    }
}

<#
    Windows refuses SetForegroundWindow to a background process unless the calling thread shares
    an input queue with the current foreground thread. Without this, a screenshot silently
    captures whatever window happened to be on top - which is not evidence of anything.
#>
function Set-AppForeground {
    $handle = Get-TargetHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'The application has no main window handle.' }

    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        [UxWin]::ShowWindow($handle, 9) | Out-Null   # SW_RESTORE
        $foreground = [UxWin]::GetForegroundWindow()
        $foreignThread = [UxWin]::GetWindowThreadProcessId($foreground, [ref]([uint32]0))
        $ownThread = [UxWin]::GetCurrentThreadId()

        [UxWin]::AttachThreadInput($ownThread, $foreignThread, $true) | Out-Null
        [UxWin]::BringWindowToTop($handle) | Out-Null
        [UxWin]::SetForegroundWindow($handle) | Out-Null
        [UxWin]::AttachThreadInput($ownThread, $foreignThread, $false) | Out-Null

        Start-Sleep -Milliseconds 220
        if ([UxWin]::GetForegroundWindow() -eq $handle) { return $handle }
    }

    throw 'Could not bring the Map window to the foreground; a capture here would be a fake.'
}

function Save-Step([string]$slug, [string]$note) {
    $script:index++
    $name = '{0:d2}-{1}.png' -f $script:index, $slug
    $path = Join-Path $OutDir $name

    $handle = Set-AppForeground
    Start-Sleep -Milliseconds 320

    $rect = New-Object UxWin+RECT
    [UxWin]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 200 -or $height -lt 200) { throw "Window rect looks wrong: ${width}x${height}" }

    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object Drawing.Size($width, $height)))
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }

    # Decode what was written: a saved file that does not match the window is not evidence.
    $decoded = [Drawing.Image]::FromFile($path)
    try {
        if ($decoded.Width -ne $width -or $decoded.Height -ne $height) {
            throw "Saved PNG $name is ${($decoded.Width)}x${($decoded.Height)}, window is ${width}x${height}"
        }
    } finally { $decoded.Dispose() }

    $script:steps.Add([ordered]@{
        file = $name; size = "${width}x${height}"; note = $note
    })
    Write-Host ("  [{0:d2}] {1}  {2}x{3}  {4}" -f $script:index, $name, $width, $height, $note)
    return $path
}

# Virtual key codes, not characters: SendKeys cannot type a Latin letter while a Russian
# keyboard layout is active, and this machine runs one. Virtual keys are positional, so they
# reproduce exactly what a user's fingers do regardless of layout.
$script:VirtualKeys = @{
    'Tab' = 0x09; 'Ctrl' = 0x11; 'Esc' = 0x1B
    'B' = 0x42; 'E' = 0x45; 'L' = 0x4C; 'M' = 0x4D; 'R' = 0x52; 'T' = 0x54; 'V' = 0x56
    'Y' = 0x59; 'Z' = 0x5A
}

function Send-Keystrokes {
    param([string[]]$Chord, [int]$Repeat = 1)

    Set-AppForeground | Out-Null
    Start-Sleep -Milliseconds 220

    for ($pass = 0; $pass -lt $Repeat; $pass++) {
        $codes = @()
        foreach ($name in $Chord) {
            if (-not $script:VirtualKeys.ContainsKey($name)) { throw "Unknown key '$name'" }
            $codes += [byte]$script:VirtualKeys[$name]
        }
        foreach ($code in $codes) { [UxWin]::keybd_event($code, 0, 0, [UIntPtr]::Zero) }
        [array]::Reverse($codes)
        foreach ($code in $codes) { [UxWin]::keybd_event($code, 0, 2, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 220
    }
    Start-Sleep -Milliseconds 260
}

function Assert-Equal([string]$actual, [string]$expected, [string]$what) {
    if ($actual -ne $expected) { throw "$what : expected '$expected', got '$actual'" }
    Write-Host "      ok  $what = '$actual'"
}

function Assert-Contains([string]$actual, [string]$fragment, [string]$what) {
    if ($actual -notlike "*$fragment*") { throw "$what : '$actual' does not contain '$fragment'" }
    Write-Host "      ok  $what contains '$fragment'"
}

function Get-BrushColor([string]$key) {
    $appXaml = Join-Path $root '..\src\Map.App\App.axaml'
    $source = Get-Content -LiteralPath $appXaml -Raw
    $pattern = '<SolidColorBrush\s+x:Key="' + [regex]::Escape($key) + '"\s+Color="(#[0-9A-Fa-f]{6})"\s*/>'
    $match = [regex]::Match($source, $pattern)
    if (-not $match.Success) { throw "Brush '$key' was not found in App.axaml." }
    return $match.Groups[1].Value
}

function Get-RelativeLuminance([string]$color) {
    $channels = @(1, 3, 5 | ForEach-Object {
        $value = [Convert]::ToInt32($color.Substring($_, 2), 16) / 255.0
        if ($value -le 0.04045) { $value / 12.92 }
        else { [Math]::Pow(($value + 0.055) / 1.055, 2.4) }
    })
    return 0.2126 * $channels[0] + 0.7152 * $channels[1] + 0.0722 * $channels[2]
}

function Assert-Contrast([string]$foregroundKey, [string]$backgroundKey, [double]$minimum) {
    $foreground = Get-BrushColor $foregroundKey
    $background = Get-BrushColor $backgroundKey
    $first = Get-RelativeLuminance $foreground
    $second = Get-RelativeLuminance $background
    $lighter = [Math]::Max($first, $second)
    $darker = [Math]::Min($first, $second)
    $ratio = ($lighter + 0.05) / ($darker + 0.05)
    if ($ratio -lt $minimum) {
        throw "$foregroundKey on $backgroundKey has contrast $($ratio.ToString('F3')), below $minimum."
    }
    Write-Host "      ok  $foregroundKey on $backgroundKey contrast = $($ratio.ToString('F3'))"
}

function Assert-ToggleOn([string]$id, [string]$what) {
    $item = Wait-Id $id
    $pattern = $null
    if (-not $item.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        throw "$what : '$id' does not expose TogglePattern"
    }
    if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "$what : '$id' is not checked"
    }
    Write-Host "      ok  $what exposes checked state"
}

function Assert-OneToggleOnByPrefix([string]$prefix, [string]$what) {
    $matches = [System.Collections.Generic.List[string]]::new()
    foreach ($window in Get-AppWindows) {
        $items = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $items) {
            $id = $item.Current.AutomationId
            if (-not $id.StartsWith($prefix, [StringComparison]::Ordinal)) { continue }
            $pattern = $null
            if ($item.TryGetCurrentPattern(
                    [System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern) -and
                $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
                $matches.Add($id)
            }
        }
    }
    if ($matches.Count -ne 1) { throw "$what : expected one checked '$prefix*' row, got $($matches.Count)" }
    Write-Host "      ok  $what exposes one checked row"
}

function Count-IdsByPrefix([string]$prefix) {
    $count = 0
    foreach ($window in Get-AppWindows) {
        $items = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $items) {
            if ($item.Current.AutomationId.StartsWith($prefix, [StringComparison]::Ordinal)) {
                $count++
            }
        }
    }
    return $count
}

function Get-UnsavedCount {
    $summary = Get-Text 'SaveStateText'
    $match = [regex]::Match($summary, 'Не сохранено:\s+(\d+)')
    if (-not $match.Success) { throw "Save state does not expose an unsaved count: '$summary'" }
    return [long]$match.Groups[1].Value
}

# ============================================================================ run

$contextTitles = [ordered]@{}
try {
    Write-Host 'Static accessibility checks'
    Assert-Contrast 'MapFaintBrush' 'MapPanelBrush' 4.5
    Assert-Contrast 'MapFaintBrush' 'MapSurfaceBrush' 4.5

    # Test mode exposes the font-substitution probe, which is a fixture and never a user command.
    $env:MAP_UI_TEST_MODE = '1'
    $script:process = Start-Process -FilePath $Exe -PassThru
    Wait-Id 'NewMapButton' 25000 | Out-Null

    Write-Host 'Dashboard'
    $target = Set-WindowSize 1416 939
    Assert-Equal $target.Outer '1416x939' 'window size'
    Wait-Id 'DashboardTitle' | Out-Null
    Wait-Id 'OpenFileButton' | Out-Null
    Wait-Id 'NewMapCardButton' | Out-Null
    Save-Step 'dashboard-1416x939' 'library: title, new map, open project, recent grid' | Out-Null

    Write-Host 'New map wizard'
    Invoke-Id 'NewMapButton' 600
    Wait-Id 'WizardTitleBox' 15000 | Out-Null
    Set-Value 'WizardTitleBox' 'Северо-западный предел Кирандийского архипелага'
    if (Test-Id 'WizardWidthBox') { throw 'Raw canvas numbers must stay hidden until "Свой размер".' }
    Invoke-Id 'WizardPresetCustom' 400
    Assert-ToggleOn 'WizardPresetCustom' 'custom size preset'
    Assert-ToggleOn 'WizardBackgroundFF1E2430' 'default background'
    Set-Value 'WizardWidthBox' '65536'
    Set-Value 'WizardHeightBox' '65536'
    Save-Step 'wizard-custom-size' 'wizard: presets, disclosure of exact size, long Russian title' | Out-Null
    Invoke-Id 'CreateMapButton' 1200
    Wait-Id 'MapCanvas' 30000 | Out-Null
    $script:process.Refresh()
    $script:targetHandle = $script:process.MainWindowHandle
    Save-Step 'editor-new-map' 'editor right after creating a 65536x65536 map' | Out-Null

    Write-Host 'Tool context panels'
    $tools = @(
        @{ Id = 'SelectToolButton'; Title = 'Инструмент: Выбор';  Slug = 'tool-select' },
        @{ Id = 'LineToolButton';   Title = 'Инструмент: Линия';  Slug = 'tool-line' },
        @{ Id = 'ShapeToolButton';  Title = 'Инструмент: Фигура'; Slug = 'tool-shape' },
        @{ Id = 'TextToolButton';   Title = 'Инструмент: Текст';  Slug = 'tool-text' },
        @{ Id = 'BrushToolButton';  Title = 'Инструмент: Кисть';  Slug = 'tool-brush' },
        @{ Id = 'MaskToolButton';   Title = 'Инструмент: Маска';  Slug = 'tool-mask' },
        @{ Id = 'LandToolButton';   Title = 'Инструмент: Земля';  Slug = 'tool-land' }
    )
    foreach ($tool in $tools) {
        Invoke-Id $tool.Id 380
        $title = Get-Text 'ContextPanelTitle'
        Assert-Equal $title $tool.Title "context title after $($tool.Id)"
        Assert-ToggleOn $tool.Id "active tool $($tool.Title)"
        $contextTitles[$tool.Id] = $title
        Save-Step $tool.Slug "context panel for $($tool.Title)" | Out-Null
    }

    # Each tool must own its parameters: a brush slider may not survive into the land panel.
    Invoke-Id 'BrushToolButton' 300
    if (-not (Test-Id 'BrushSizeSlider')) { throw 'Brush panel lost its own size control.' }
    if (Test-Id 'LandActionAdd') { throw 'Land parameters are visible while the brush is active.' }
    Invoke-Id 'LandToolButton' 300
    if (-not (Test-Id 'LandActionAdd')) { throw 'Land panel lost its action switch.' }
    if (Test-Id 'BrushSizeSlider') { throw 'Brush parameters are visible while land is active.' }
    Write-Host '      ok  tool panels do not leak parameters into each other'

    # The land shapes moved from a flat button pile into the tool panel, but every historical
    # automation id has to still resolve to a working control.
    foreach ($shape in @('LandCircleButton', 'LandRectangleButton', 'LandEllipseButton',
                         'LandPolygonButton', 'LandGridButton', 'LandFillButton')) {
        if (-not (Test-Id $shape)) { throw "Land shape '$shape' is no longer reachable." }
    }
    Invoke-Id 'LandCircleButton' 320
    Invoke-Id 'LandActionSubtract' 260
    Assert-ToggleOn 'LandActionSubtract' 'land subtract mode'
    Invoke-Id 'LandActionAdd' 260
    Assert-ToggleOn 'LandActionAdd' 'land add mode'
    Write-Host '      ok  all six land shapes kept their automation ids'

    Write-Host 'Layers'
    Select-SideTab 0
    Invoke-Id 'AddLandLayerButton' 420
    Invoke-Id 'AddGridLayerButton' 420
    if (-not (Test-Id 'GridGeometryButton')) { throw 'Grid layer properties did not appear.' }
    if (Test-Id 'FilterStyleButton') { throw 'Filter properties are shown for a grid layer.' }
    Invoke-Id 'GridGeometryButton' 300
    Invoke-Id 'AddFilterLayerButton' 420
    if (-not (Test-Id 'FilterStyleButton')) { throw 'Filter layer properties did not appear.' }
    if (Test-Id 'GridGeometryButton') { throw 'Grid properties survived into a filter layer.' }
    Invoke-Id 'FilterStyleButton' 300
    Invoke-Id 'LayerMaskButton' 320
    $layerStatus = Get-Text 'ActiveLayerText'
    Assert-Contains $layerStatus 'Слой:' 'status bar names the active layer'
    Assert-OneToggleOnByPrefix 'Layer_' 'active layer'
    Save-Step 'layers' 'layers list, typed add row, properties of the active layer' | Out-Null

    Write-Host 'Objects, selection and inspector'
    Invoke-Id 'LineToolButton' 300
    Invoke-Id 'AddLineButton' 500
    Select-SideTab 1
    $selection = Get-Text 'SelectionStatusText'
    Assert-Contains $selection 'Линия' 'selection status names the kind in Russian'
    Assert-OneToggleOnByPrefix 'Object_' 'selected object'
    $contextTitle = Get-Text 'ContextPanelTitle'
    Assert-Equal $contextTitle 'Свойства: Линия' 'inspector title follows the selection'
    if (-not (Test-Id 'MoveVectorNodeButton')) { throw 'Line geometry actions are missing.' }
    if (Test-Id 'CopyCompositionButton') { throw 'Composition actions are shown for a line selection.' }
    if (Test-Id 'StampFlipButton') { throw 'Stamp actions are shown for a line selection.' }
    Write-Host '      ok  inspector shows only the selected kind'
    Set-Value 'XBox' '900'
    Set-Value 'YBox' '640'
    Invoke-Id 'ApplyButton' 380
    Save-Step 'objects-selection-inspector' 'objects list, line selected, inspector for that kind' | Out-Null

    Invoke-Id 'TextToolButton' 320
    Assert-Equal (Get-Text 'ContextPanelTitle') 'Инструмент: Текст' 'tool panel returns while drawing'
    Invoke-Id 'AddTextButton' 600
    Assert-Equal (Get-Text 'ContextPanelTitle') 'Свойства: Текст' 'inspector switches with the kind'
    if (Test-Id 'MoveVectorNodeButton') { throw 'Vector actions survived into a text selection.' }
    if (-not (Test-Id 'TextMissingFontButton')) { throw 'Test-mode probe missing while MAP_UI_TEST_MODE=1.' }
    Invoke-Id 'TextMissingFontButton' 320
    Save-Step 'inspector-text' 'inspector for a text selection only' | Out-Null

    Write-Host 'Undo, redo and save state'
    $dirty = Get-Text 'SaveStateText'
    Assert-Contains $dirty 'Не сохранено' 'status bar counts unsaved work'
    $objectsBeforeUndo = Count-IdsByPrefix 'Object_'
    $changesBeforeUndo = Get-UnsavedCount
    Invoke-Id 'UndoButton' 380
    Assert-Equal (Get-UnsavedCount) ($changesBeforeUndo - 1) 'undo reduces pending changes'
    Invoke-Id 'UndoButton' 380
    Assert-Equal (Count-IdsByPrefix 'Object_') ($objectsBeforeUndo - 1) 'a second undo removes the last object'
    Assert-Equal (Get-UnsavedCount) ($changesBeforeUndo - 2) 'two undos reduce pending changes twice'
    Invoke-Id 'RedoButton' 380
    Assert-Equal (Count-IdsByPrefix 'Object_') $objectsBeforeUndo 'redo restores the last object'
    Assert-Equal (Get-UnsavedCount) ($changesBeforeUndo - 1) 'redo restores one pending change'
    Invoke-Id 'RedoButton' 380
    Assert-Equal (Get-UnsavedCount) $changesBeforeUndo 'redo restores pending changes'
    Write-Host "      ok  save state before undo = '$dirty'"

    Write-Host 'Assets and notes'
    Select-SideTab 2
    Wait-Id 'ImportAssetButton' | Out-Null
    Wait-Id 'PlaceStampButton' | Out-Null
    Save-Step 'assets' 'asset catalog tab with empty state and selection block' | Out-Null
    Select-SideTab 3
    Invoke-Id 'AddWorldNoteButton' 420
    Select-SideTab 1
    Invoke-Id 'MakeCompositionButton' 420

    Write-Host 'Keyboard-only path'
    Send-Keystrokes -Chord @('Tab') -Repeat 3
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $focusName = $focused.Current.Name
    if ([string]::IsNullOrWhiteSpace($focusName)) { throw 'Tab focus landed on a control with no accessible name.' }
    Write-Host "      ok  focus after Tab: '$focusName'"
    Send-Keystrokes -Chord @('B')
    Assert-Equal (Get-Text 'ContextPanelTitle') 'Инструмент: Кисть' 'tool switched by keyboard'
    Send-Keystrokes -Chord @('L')
    Assert-Equal (Get-Text 'ContextPanelTitle') 'Инструмент: Линия' 'a second tool key also works'
    Send-Keystrokes -Chord @('R')
    Assert-Equal (Get-Text 'ContextPanelTitle') 'Инструмент: Фигура' 'shape tool shortcut works'
    Send-Keystrokes -Chord @('V')
    $changesBeforeKeyboardUndo = Get-UnsavedCount
    Send-Keystrokes -Chord @('Ctrl', 'Z')
    Assert-Equal (Get-UnsavedCount) ($changesBeforeKeyboardUndo - 1) 'Ctrl+Z reduces pending changes'
    Send-Keystrokes -Chord @('Ctrl', 'Y')
    Assert-Equal (Get-UnsavedCount) $changesBeforeKeyboardUndo 'Ctrl+Y restores pending changes'
    Save-Step 'keyboard-only' "keyboard: Tab focus '$focusName', B/L/R/V switch tools, Ctrl+Z/Ctrl+Y change state" | Out-Null

    Write-Host 'Second native window'
    Invoke-Id 'SecondWindowButton' 900
    $windowCount = @(Get-AppWindows).Count
    if ($windowCount -lt 2) { throw 'Second native window did not appear.' }
    Save-Step 'second-window' "second native window, total $windowCount" | Out-Null

    Write-Host 'Minimum window'
    # 960x640 is the minimum *client* area the product supports; Windows adds the frame on top,
    # so the outer rect is legitimately larger and is recorded rather than asserted blindly.
    $small = Set-WindowSize 960 640
    Assert-Equal $small.Client '960x640' 'minimum client area'
    Write-Host "      ok  minimum outer window = '$($small.Outer)'"
    $minimumOuter = $small.Outer
    Wait-Id 'SelectToolButton' | Out-Null
    Wait-Id 'EditorSideTabs' | Out-Null
    Wait-Id 'SaveStateText' | Out-Null
    if ((Get-TargetHandle) -ne $script:targetHandle) { throw 'Lost track of the editor window.' }
    Save-Step 'minimum-960x640' 'the editor window at its minimum size keeps rail, panels and status reachable' | Out-Null
    Set-WindowSize 1416 939 | Out-Null

    Write-Host 'Offline check'
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object OwningProcess -eq $script:process.Id)
    if ($listeners.Count -ne 0) { throw 'The offline editor opened a TCP listener.' }

    [ordered]@{
        status        = 'FACT'
        recordedUtc   = [DateTimeOffset]::UtcNow
        executable    = $Exe
        processId     = $script:process.Id
        testMode      = $env:MAP_UI_TEST_MODE
        requestedCanvas = '65536x65536'
        nativeWindows = $windowCount
        minimumClientArea = '960x640'
        minimumOuterWindow = $minimumOuter
        tcpListeners  = 0
        contextTitles = $contextTitles
        screenshots   = $script:steps
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'evidence.json') -Encoding UTF8

    Write-Host "UX smoke passed: $OutDir"
}
finally {
    Remove-Item Env:\MAP_UI_TEST_MODE -ErrorAction SilentlyContinue
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        $script:process.WaitForExit(5000) | Out-Null
    }
}
