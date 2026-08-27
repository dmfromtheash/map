<#
.SYNOPSIS
Stage 10C native smoke: create objects of several kinds on two object layers, prove single and
multi-selection through the objects tree, search and the type filter, create a named group, walk
into it and back over the breadcrumb, save and reopen, ungroup with undo/redo, show the honest
mixed lock state, and prove deletion with the automatic dissolve of a singleton group.

.DESCRIPTION
The run owns everything it touches: an isolated asset root, recovery root and map directory under
artifacts/stage10c-*, so the owner's real library and recovery are never opened. By default the
window is placed outside the visible desktop, is never activated, and is captured via
PrintWindow, so the owner's keyboard and desktop stay untouched. Real keyboard chords therefore
cannot be sent in this mode: canvas marquee, Esc, Ctrl+click and the move/scale/rotate keyboard
gestures stay PARTIAL here and are proven by the targeted unit tests plus routing review. Every
wait is bounded and only processes this script started are terminated.
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
    $OutDir = Join-Path $workspace ("artifacts\stage10c-multi-selection\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$catalogRoot = Join-Path $OutDir 'catalog'
$recoveryRoot = Join-Path $OutDir 'recovery'
$mapRoot = Join-Path $OutDir 'maps'
New-Item -ItemType Directory -Force -Path $OutDir, $catalogRoot, $recoveryRoot, $mapRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Stage10CNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage10CNative
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

# ------------------------------------------------------------------ UIA helpers

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

function Find-UiaNameLike([string]$fragment) {
    foreach ($attempt in 1..5) {
        try {
            $all = $script:Root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($element in $all) {
                if ($element.Current.Name -like "*$fragment*") { return $element }
            }
            return $null
        }
        catch [System.Runtime.InteropServices.COMException] {
            Start-Sleep -Milliseconds 100
        }
    }
    return $null
}

function Find-UiaNamesLike([string]$fragment) {
    foreach ($attempt in 1..5) {
        try {
            $all = $script:Root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $found = @()
            foreach ($element in $all) {
                if ($element.Current.Name -like "*$fragment*") { $found += $element }
            }
            return $found
        }
        catch [System.Runtime.InteropServices.COMException] {
            Start-Sleep -Milliseconds 100
        }
    }
    return @()
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

# Waits for the UI to settle after ONE action: the input is sent exactly once and this only
# bounds how long its visible result may take to appear.
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
    else {
        throw "Element '$what' exposes no Invoke, Toggle or SelectionItem pattern."
    }
    Start-Sleep -Milliseconds 300
}

function Invoke-Uia([string]$automationId) {
    # A panel swap can detach the element between find and invoke; a stale invoke throws an
    # ArgumentException. Re-finding by the stable id and invoking again targets the live control.
    foreach ($attempt in 1..3) {
        try {
            Invoke-UiaElement (Require-Uia $automationId) $automationId
            return
        }
        catch [System.ArgumentException] {
            Write-Host "      retry $attempt for '$automationId': $($_.Exception.Message)"
            Start-Sleep -Milliseconds 400
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            Write-Host "      retry $attempt for '$automationId': element not available"
            Start-Sleep -Milliseconds 400
        }
    }
    throw "Element '$automationId' could not be invoked after retries."
}

<#
Clicks one list row and verifies the click actually landed by watching the selection status.
The tree rebuilds its rows after every edit, so a just-detached row can swallow one click; the
verified retry targets the freshly built row instead.
#>
function Select-RowChecked([string]$fragment, [string]$expectedStatusFragment) {
    foreach ($attempt in 1..5) {
        try { Invoke-RowByName $fragment }
        catch [System.ArgumentException] { }
        Start-Sleep -Milliseconds 350
        if ((Get-UiaName 'SelectionStatusText') -like "*$expectedStatusFragment*") { return }
    }
    throw "Selecting row '$fragment' never produced selection '$expectedStatusFragment'."
}

<#
Finds one LIST ROW by a fragment of its accessible name. The status bar repeats the selection
wording in a plain TextBlock, so a bare name search would hit it first; a row is therefore
required to be an actual button (our rows are ToggleButtons) before it counts.
#>
function Find-RowLike([string]$fragment) {
    $buttonType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($attempt in 1..5) {
        try {
            $all = $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonType)
            foreach ($element in $all) {
                if ($element.Current.Name -like "*$fragment*") { return $element }
            }
            return $null
        }
        catch [System.Runtime.InteropServices.COMException] {
            Start-Sleep -Milliseconds 100
        }
    }
    return $null
}

function Invoke-RowByName([string]$fragment) {
    $element = Find-RowLike $fragment
    if ($null -eq $element) { throw "Row named like '$fragment' was not found." }
    Invoke-UiaElement $element $fragment
}

function Set-UiaValue([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
    Start-Sleep -Milliseconds 300
}

function Get-UiaName([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { return '' }
    return $element.Current.Name
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
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 320
}

function Assert-Equal([string]$actual, [string]$expected, [string]$what) {
    if ($actual -ne $expected) { throw "$what : expected '$expected', got '$actual'" }
    Write-Host "      ok  $what = '$actual'"
    $script:Checks[$what] = $actual
}

function Assert-Contains([string]$actual, [string]$fragment, [string]$what) {
    if ($actual -notlike "*$fragment*") { throw "$what : '$actual' does not contain '$fragment'" }
    Write-Host "      ok  $what contains '$fragment'"
    $script:Checks[$what] = $actual
}

function Assert-True([bool]$condition, [string]$what) {
    if (-not $condition) { throw "$what : condition was false" }
    Write-Host "      ok  $what"
    $script:Checks[$what] = 'true'
}

function Note-Partial([string]$what) {
    Write-Host "      PARTIAL  $what"
    $script:Partial += $what
}

# ------------------------------------------------------------------ app control

function Start-TestApp([string]$MapPath = '') {
    # Start-Process -Environment is PowerShell 7 only; on Windows PowerShell 5.1 the child
    # inherits this process's environment, so the sandbox is declared here.
    $env:MAP_UI_TEST_MODE = '1'
    $env:MAP_UI_TEST_ASSET_ROOT = $catalogRoot
    $env:MAP_UI_TEST_RECOVERY_ROOT = $recoveryRoot
    $env:MAP_UI_TEST_IMPORT_PATHS = ''
    $env:MAP_UI_TEST_MAP_PATH = $MapPath
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $script:Proc.Refresh()
        if ($script:Proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Map window did not appear.' }
    $script:WindowHandle = $script:Proc.MainWindowHandle
    if ($Background) {
        Protect-AppWindow
    }
    else {
        [void][Stage10CNative]::ShowWindow($script:WindowHandle, 3)
    }

    Start-Sleep -Milliseconds 700
    $script:Root = [System.Windows.Automation.AutomationElement]::FromHandle($script:WindowHandle)
}

<#
A freshly started process - and every modal dialog it later opens - takes the foreground. While
the window sits outside the visible desktop, the owner's keystrokes would silently land in it
(observed in this very stage: a stray letter appeared in the library search). Minimizing hands
the activation straight back to whatever the owner had; the window is then restored WITHOUT
activation, parked outside the desktop for PrintWindow evidence, and natively disabled so it
refuses keyboard and mouse input altogether. UI Automation patterns keep working on a disabled
window, so the driver loses nothing. Called again after any flow that opened a modal dialog,
because closing a dialog re-enables and re-activates its owner.
#>
function Protect-AppWindow {
    if (-not $Background -or $script:WindowHandle -eq [IntPtr]::Zero) { return }
    [void][Stage10CNative]::ShowWindow($script:WindowHandle, 6)  # SW_MINIMIZE: give activation back
    Start-Sleep -Milliseconds 150
    [void][Stage10CNative]::ShowWindow($script:WindowHandle, 4)  # SW_SHOWNOACTIVATE
    # SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW, positioned outside the visible desktop.
    [void][Stage10CNative]::SetWindowPos(
        $script:WindowHandle, [IntPtr]::Zero, -32000, 0, 2000, 1250, 0x54)
    [void][Stage10CNative]::EnableWindow($script:WindowHandle, $false)
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
    $rect = New-Object Stage10CNative+RECT
    [void][Stage10CNative]::GetWindowRect($script:WindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Invalid window bounds ${width}x${height}." }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage10CNative]::PrintWindow($script:WindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' }
    }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $decoded = [System.Drawing.Image]::FromFile($path)
    try {
        if ($decoded.Width -ne $width -or $decoded.Height -ne $height) {
            throw "Screenshot dimensions differ from the window: $path"
        }
    }
    finally { $decoded.Dispose() }
    Write-Host ("  [{0:d2}] {1}  {2}x{3}" -f $script:Index, $label, $width, $height)
    $script:Shots += $path
    return $path
}

function New-TestMap([string]$title) {
    Wait-Uia 'DashboardTitle' { param($t) $t -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'NewMapButton'
    Wait-Uia 'WizardTitleBox' { param($t) $true } | Out-Null

    # The wizard is its own native window and it opens ACTIVATED; hand the activation back and
    # refuse raw input on it too, so no stray keystroke can land while it is open.
    if ($Background) {
        $owned = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $owned)) {
            $handle = [IntPtr]$window.Current.NativeWindowHandle
            if ($handle -ne [IntPtr]::Zero -and $handle -ne $script:WindowHandle) {
                [void][Stage10CNative]::ShowWindow($handle, 6)
                Start-Sleep -Milliseconds 120
                [void][Stage10CNative]::ShowWindow($handle, 4)
                [void][Stage10CNative]::EnableWindow($handle, $false)
            }
        }
    }

    Set-UiaValue 'WizardTitleBox' $title
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '2048'
    Set-UiaValue 'WizardHeightBox' '1400'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Выбор*' } | Out-Null

    # Closing the modal re-enabled and re-activated the main window: protect it again.
    Protect-AppWindow
    Start-Sleep -Milliseconds 500
}

function Get-SceneJson([string]$mapPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($mapPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'scene.json' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

# =================================================================== the run

$savedMap = Join-Path $mapRoot 'stage10c.mapproj'

try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
    Write-Host "Stage 10C native smoke (background: $Background)"
    Write-Host "  exe : $Exe"
    Write-Host "  out : $OutDir"

    # -------------------------------------------- 1. a map and objects of several kinds
    Write-Host "`n[1] Create a map and objects of several kinds on two object layers"
    Start-TestApp -MapPath $savedMap
    Assert-True ($script:Proc.Id -gt 0) 'owned process started'
    New-TestMap 'Stage 10C группы'

    # Creating an object returns the editor to the Select tool and swaps the context panel, so
    # every add is bracketed by explicit panel-state waits: click a tool only when its panel is
    # named, click its add button only while that panel is up, and treat the switch to
    # "Свойства" as the proof the object really landed. Without the waits a click can hit a
    # button that was just detached by the swap.
    function Add-ObjectVia([string]$toolButton, [string]$toolTitle, [string]$addButton) {
        Invoke-Uia $toolButton
        Wait-Uia 'ContextPanelTitle' { param($t) $t -like "*$toolTitle*" } | Out-Null
        Invoke-Uia $addButton
        Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Свойства*' } | Out-Null
    }

    # One legacy rectangle from the idle Select panel (only reachable while nothing is selected),
    # then a text and a line from their tool panels.
    Invoke-Uia 'AddRectangleButton'
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Свойства*' } | Out-Null
    Add-ObjectVia 'TextToolButton' 'Инструмент: Текст' 'AddTextButton'
    Add-ObjectVia 'LineToolButton' 'Инструмент: Линия' 'AddLineButton'

    # Three identically named vector rectangles - they will become the group.
    foreach ($i in 1..3) {
        Add-ObjectVia 'ShapeToolButton' 'Инструмент: Фигура' 'AddVectorRectangleButton'
    }

    # A second object layer with two vector ellipses on it.
    Select-UiaTab 'Слои'
    Invoke-Uia 'AddObjectLayerButton'
    foreach ($i in 1..2) {
        Add-ObjectVia 'ShapeToolButton' 'Инструмент: Фигура' 'AddShapeEllipseButton'
    }
    Invoke-Uia 'SelectToolButton'
    Wait-Uia 'ContextPanelTitle' { param($t) $t -like '*Свойства*' -or $t -like '*Выбор*' } | Out-Null
    Start-Sleep -Milliseconds 400
    Save-Shot 'objects-created' | Out-Null

    # -------------------------------------------- 2. single and multi-selection in the tree
    Write-Host "`n[2] Single selection through the tree, then search + filter + select all"
    Select-UiaTab 'Объекты'
    Start-Sleep -Milliseconds 400
    Assert-True ($null -ne (Find-Uia 'ObjectList')) 'the objects list exposes a stable automation id'

    Select-RowChecked 'Текст · Текст' 'Текст'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Текст*' } 'the text object is selected'
    Assert-Contains (Get-UiaName 'SelectionCountText') '1 объект' 'the tree names a single selection'
    Save-Shot 'single-selection' | Out-Null

    # ------------------------------------------------------ 3. search and the type filter
    Write-Host "`n[3] Search and the type filter drive the tree and Ctrl+A"
    Invoke-Uia 'ObjectTypeFilterText'
    Start-Sleep -Milliseconds 300
    Assert-True ($null -ne (Find-RowLike 'Текст · Текст')) 'the Text filter keeps the text row'
    Assert-True ($null -eq (Find-RowLike 'Линия · Линия')) 'the Text filter hides the line row'

    Invoke-Uia 'ObjectTypeFilterLines'
    Start-Sleep -Milliseconds 300
    Assert-True ($null -ne (Find-RowLike 'Линия · Линия')) 'the Lines filter shows the line row'
    Save-Shot 'type-filter' | Out-Null

    # ------------------------------------------------------ 4. select exactly three objects
    Write-Host "`n[4] Select all by search and filter: exactly the three rectangles"
    Invoke-Uia 'ObjectTypeFilterShapes'
    Set-UiaValue 'ObjectSearchBox' 'Фигура Rectangle'
    Start-Sleep -Milliseconds 400
    Invoke-Uia 'SelectAllButton'
    Wait-State { (Get-UiaName 'SelectionCountText') -like '*3 объекта*' } 'three shapes are selected'
    Assert-Contains (Get-UiaName 'SelectionCountText') '3 объекта' 'the selection counter reports three'
    Assert-Contains (Get-UiaName 'SelectionStatusText') '3 объекта' 'the status bar reports the same three'
    Save-Shot 'three-selected' | Out-Null

    # ---------------------------------------------------------------- 5. create the group
    Write-Host "`n[5] Create the named group from the selection"
    Assert-Contains (Get-UiaName 'MultiSelectionCountText') '3 объекта' `
        'the properties panel shows the count before grouping'
    Set-UiaValue 'GroupNameBox' 'Тестовая группа'
    Invoke-Uia 'GroupCreateButton'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Тестовая группа*' } 'the new group is selected'
    Assert-Contains (Get-UiaName 'SelectionStatusText') 'Группа «Тестовая группа»' 'the status names the group'
    Assert-Contains (Get-UiaName 'MultiSelectionCountText') 'Тестовая группа' 'the panel names the group'

    # ------------------------------------- 6. one top-level row, children not duplicated
    Write-Host "`n[6] The tree shows one group row and no duplicated children"
    Set-UiaValue 'ObjectSearchBox' ''
    Invoke-Uia 'ObjectTypeFilterAll'
    Start-Sleep -Milliseconds 400
    $groupRow = Find-RowLike 'Группа «Тестовая группа»'
    Assert-True ($null -ne $groupRow) 'the group is one top-level row'
    Assert-Contains $groupRow.Current.Name '3 объекта' 'the group row states its child count'
    Assert-True ($null -eq (Find-RowLike 'Фигура Rectangle · Фигура')) `
        'grouped shapes are not duplicated as top-level rows'
    Save-Shot 'group-row' | Out-Null

    # -------------------------------------------------- 7. undo/redo around group create
    Write-Host "`n[7] Undo and redo restore the same named group"
    Invoke-Uia 'UndoButton'
    Wait-State { $null -eq (Find-RowLike 'Группа «Тестовая группа»') } 'undo removes the group row'
    Assert-True ($null -ne (Find-RowLike 'Фигура Rectangle · Фигура')) `
        'undo returns the rectangles as plain rows'
    Invoke-Uia 'RedoButton'
    Wait-State { $null -ne (Find-RowLike 'Группа «Тестовая группа»') } 'redo restores the group row'
    Save-Shot 'group-after-redo' | Out-Null

    # -------------------------------------------------- 8. one multi command over the group
    Write-Host "`n[8] A whole-group command is one undoable entry"
    Select-RowChecked 'Группа «Тестовая группа»' 'Тестовая группа'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Тестовая группа*' } 'the group row selects the group'
    Assert-Contains (Get-UiaName 'MultiVisibilityButton') 'Показан' 'the shared visibility reads as shown'
    Invoke-Uia 'MultiVisibilityButton'
    Wait-State { (Get-UiaName 'MultiVisibilityButton') -like '*Скрыт*' } 'one command hides the whole group'
    Invoke-Uia 'UndoButton'
    Start-Sleep -Milliseconds 500
    Select-RowChecked 'Группа «Тестовая группа»' 'Тестовая группа'
    Wait-State { (Get-UiaName 'MultiVisibilityButton') -like '*Показан*' } `
        'one undo shows the whole group again'
    Note-Partial 'keyboard move/scale/rotate of the group is not exercised in background mode; proven by targeted unit tests'
    Save-Shot 'group-visibility-undo' | Out-Null

    # ------------------------------------------------------------ 9. enter over breadcrumb
    Write-Host "`n[9] Enter the group, select a child, return over the breadcrumb"
    Invoke-Uia 'EnterGroupButton'
    Wait-State { $null -ne (Find-Uia 'GroupBreadcrumb') } 'the breadcrumb appears'
    Assert-Contains (Get-UiaName 'GroupBreadcrumb') 'Тестовая группа' 'the breadcrumb names the group'
    Select-RowChecked 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Wait-State { (Get-UiaName 'SelectionCountText') -like '*1 объект*' } 'a child is selected alone'
    Assert-Contains (Get-UiaName 'SelectionStatusText') 'Фигура Rectangle' 'the child is named in the status'
    Save-Shot 'inside-group' | Out-Null

    Invoke-Uia 'GroupBackButton'
    Wait-State { $null -eq (Find-Uia 'GroupBreadcrumb') } 'the breadcrumb closes'
    Assert-Contains (Get-UiaName 'SelectionStatusText') 'Тестовая группа' `
        'leaving the group selects the whole group again'
    Save-Shot 'back-from-group' | Out-Null

    # ---------------------------------------------------------------- 10. save and reopen
    Write-Host "`n[10] Save, reopen, and verify GroupId/GroupName in the .mapproj itself"
    Invoke-Uia 'SaveButton'
    Wait-State { Test-Path -LiteralPath $savedMap } 'the map file is written' 15000
    Start-Sleep -Milliseconds 1500

    $scene = Get-SceneJson $savedMap
    $groupIds = [regex]::Matches($scene, '"groupId"\s*:\s*"([0-9a-f-]{36})"') |
        ForEach-Object { $_.Groups[1].Value }
    $groupNames = [regex]::Matches($scene, '"groupName"\s*:\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }
    $script:Checks['saved groupId entries'] = $groupIds.Count
    $script:Checks['saved groupName entries'] = $groupNames.Count
    Assert-True ($groupIds.Count -eq 3) 'exactly the three grouped objects carry a groupId'
    Assert-True ((@($groupIds | Sort-Object -Unique)).Count -eq 1) 'all three share ONE groupId'
    Assert-True ($groupNames.Count -eq 3) 'every member carries the group name'
    Assert-True ((@($groupNames | Sort-Object -Unique)).Count -eq 1) 'all three share ONE group name'
    Assert-True ($scene -like '*"x":*') 'the scene keeps explicit geometry'

    Stop-TestApp
    Start-TestApp -MapPath $savedMap
    Wait-Uia 'DashboardTitle' { param($t) $t -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'OpenFileButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Start-Sleep -Milliseconds 800
    Select-UiaTab 'Объекты'
    Start-Sleep -Milliseconds 400
    $reopenedRow = Find-RowLike 'Группа «Тестовая группа»'
    Assert-True ($null -ne $reopenedRow) 'the reopened map still shows the group row'
    Assert-Contains $reopenedRow.Current.Name '3 объекта' 'the reopened group keeps its child count'
    Save-Shot 'reopened-group' | Out-Null

    # -------------------------------------------------------------------- 11. ungroup
    Write-Host "`n[11] Ungroup with undo and redo"
    Select-RowChecked 'Группа «Тестовая группа»' 'Тестовая группа'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Тестовая группа*' } 'the group is selected again'
    Invoke-Uia 'UngroupButton'
    Wait-State { $null -eq (Find-RowLike 'Группа «Тестовая группа»') } 'ungroup removes the group row'
    Assert-True ($null -ne (Find-RowLike 'Фигура Rectangle · Фигура')) `
        'former members return as plain rows'
    Invoke-Uia 'UndoButton'
    Wait-State { $null -ne (Find-RowLike 'Группа «Тестовая группа»') } 'undo restores the group'
    Invoke-Uia 'RedoButton'
    Wait-State { $null -eq (Find-RowLike 'Группа «Тестовая группа»') } 'redo ungroups again'
    Invoke-Uia 'UndoButton'
    Wait-State { $null -ne (Find-RowLike 'Группа «Тестовая группа»') } 'the group is back for the next phase'
    Save-Shot 'ungroup-undo-redo' | Out-Null

    # ------------------------------------------------------ 12. the honest mixed lock state
    Write-Host "`n[12] A locked child stays reachable and the mixed state is honest"
    Select-RowChecked 'Группа «Тестовая группа»' 'Тестовая группа'
    Start-Sleep -Milliseconds 300
    Invoke-Uia 'EnterGroupButton'
    Wait-State { $null -ne (Find-Uia 'GroupBreadcrumb') } 'the group is open'
    Select-RowChecked 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Wait-State { (Get-UiaName 'ObjectLockButton') -like '*Свободен*' } 'the child is unlocked at first'
    Invoke-Uia 'ObjectLockButton'
    Wait-State { (Get-UiaName 'ObjectLockButton') -like '*Заблокирован*' } 'the child is now locked'

    # The locked child must stay reachable through the tree - that is the door to unlock it.
    Select-RowChecked 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Фигура Rectangle*' } `
        'the locked child is still selectable through the tree'

    Invoke-Uia 'GroupBackButton'
    Wait-State { (Get-UiaName 'SelectionStatusText') -like '*Тестовая группа*' } 'the whole group is selected'
    Assert-Contains (Get-UiaName 'MultiLockButton') 'Разные значения' `
        'the mixed lock state reads as mixed, never as a fake single value'
    Save-Shot 'mixed-lock-state' | Out-Null
    Note-Partial 'the atomic keyboard-nudge refusal over a locked member is proven by targeted unit tests; background mode sends no keyboard input'

    # Unlock the child again so the deletion phase starts clean.
    Invoke-Uia 'EnterGroupButton'
    Wait-State { $null -ne (Find-Uia 'GroupBreadcrumb') } 'the group is open again'
    Select-RowChecked 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Wait-State { (Get-UiaName 'ObjectLockButton') -like '*Заблокирован*' } 'the locked child is current'
    Invoke-Uia 'ObjectLockButton'
    Wait-State { (Get-UiaName 'ObjectLockButton') -like '*Свободен*' } 'the child is unlocked again'

    # -------------------------------------- 13. deletion and the singleton auto-dissolve
    Write-Host "`n[13] Deleting down to one member dissolves the group in the same entry"
    Invoke-Uia 'DeleteObjectButton'
    Start-Sleep -Milliseconds 400
    Invoke-Uia 'GroupBackButton'
    Start-Sleep -Milliseconds 300
    $twoLeft = Find-RowLike 'Группа «Тестовая группа»'
    Assert-True ($null -ne $twoLeft) 'after one deletion the group still exists'
    Assert-Contains $twoLeft.Current.Name '2 объекта' 'the group row counts two remaining members'

    Invoke-Uia 'EnterGroupButton'
    Wait-State { $null -ne (Find-Uia 'GroupBreadcrumb') } 'the two-member group is open'
    Select-RowChecked 'Фигура Rectangle · Фигура' 'Фигура Rectangle'
    Start-Sleep -Milliseconds 300
    Invoke-Uia 'DeleteObjectButton'
    Wait-State { $null -eq (Find-RowLike 'Группа «Тестовая группа»') } `
        'deleting the second-to-last member dissolves the group'
    Assert-True ($null -ne (Find-RowLike 'Фигура Rectangle · Фигура')) `
        'the remainder returns as a plain ungrouped row'
    Save-Shot 'singleton-dissolved' | Out-Null

    Invoke-Uia 'UndoButton'
    Wait-State { $null -ne (Find-RowLike 'Группа «Тестовая группа»') } `
        'one undo restores both the member and the group identity'
    Assert-Contains (Find-RowLike 'Группа «Тестовая группа»').Current.Name '2 объекта' `
        'the restored group counts two members again'
    Save-Shot 'singleton-undo' | Out-Null

    # -------------------------------------------- 14. delete across layers as one entry
    Write-Host "`n[14] Select all across both layers, delete, and undo"
    Set-UiaValue 'ObjectSearchBox' ''
    Invoke-Uia 'ObjectTypeFilterAll'
    Start-Sleep -Milliseconds 300
    Invoke-Uia 'SelectAllButton'
    Wait-State { (Get-UiaName 'SelectionCountText') -notlike '*Ничего*' } 'select all found the objects'
    $countBefore = Get-UiaName 'SelectionCountText'
    $script:Checks['select-all across layers'] = $countBefore
    Save-Shot 'select-all' | Out-Null
    Invoke-Uia 'MultiDeleteButton'
    Wait-State { ($null -eq (Find-RowLike 'Текст · Текст')) -and ($null -eq (Find-RowLike 'Фигура Ellipse · Фигура')) } `
        'every visible unlocked object is deleted at once'
    Invoke-Uia 'UndoButton'
    Wait-State { ($null -ne (Find-RowLike 'Текст · Текст')) -and ($null -ne (Find-RowLike 'Фигура Ellipse · Фигура')) } `
        'one undo returns the whole scene'
    Save-Shot 'delete-across-layers-undone' | Out-Null

    Note-Partial 'canvas marquee, Ctrl+click and Esc are pointer/keyboard gestures; in background mode they are proven by targeted unit tests and the renderer test for the marquee chrome'

    # ------------------------------------------------------------------ 15. clean shutdown
    Write-Host "`n[15] Clean shutdown"
    Stop-TestApp
    $left = @(Get-Process -Name 'Map.App' -ErrorAction SilentlyContinue)
    Assert-True ($left.Count -eq 0) 'no Map.App process is left behind'

    # ------------------------------------------------------------------ report
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
    Write-Host ("`nPASS  {0} screenshots, {1} checks, {2} partial notes" -f `
        $script:Shots.Count, $script:Checks.Count, $script:Partial.Count)
    Write-Host "Report: $(Join-Path $OutDir 'report.json')"
}
catch {
    # Failure diagnostics: what the window showed at the moment the run stopped.
    try {
        if ($script:WindowHandle -ne [IntPtr]::Zero) {
            Write-Host ("  at failure ContextPanelTitle : '{0}'" -f (Get-UiaName 'ContextPanelTitle'))
            Write-Host ("  at failure SelectionStatus   : '{0}'" -f (Get-UiaName 'SelectionStatusText'))
            Write-Host ("  at failure StatusDetail      : '{0}'" -f (Get-UiaName 'StatusDetailText'))
            Save-Shot 'failure-state' | Out-Null
        }
    }
    catch { Write-Host "  (failure diagnostics unavailable: $($_.Exception.Message))" }

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
    Write-Host $_.ScriptStackTrace
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
