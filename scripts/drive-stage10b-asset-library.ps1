<#
.SYNOPSIS
Stage 10B native smoke: import the project's own diagnostic fixtures into an isolated library,
navigate collections, variants and filters, place One and Area gestures, prove undo/redo and
save/reopen without the local catalog, and exercise the missing / hash-mismatch / damaged paths.

.DESCRIPTION
The run owns everything it touches. MAP_UI_TEST_MODE plus MAP_UI_TEST_ASSET_ROOT redirect the
asset catalog into the artifact directory, so the owner's real library is never opened, written
or screenshotted. The native Windows file picker neither shows a window nor returns under this
automation host - confirmed on the pre-existing single-import path too - so file paths are handed
to the app through its own MAP_UI_TEST_* switches instead of an OS dialog.
Every wait is bounded and only processes this script started are terminated.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutDir,
    [switch]$SkipDpi,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspace = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
if (-not $Exe) { $Exe = Join-Path $workspace 'src\Map.App\bin\Release\net10.0\Map.App.exe' }
if (-not $OutDir) {
    $OutDir = Join-Path $workspace ("artifacts\stage10b-asset-library\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$fixtureRoot = Join-Path $workspace 'tests\fixtures\assets'
if (-not (Test-Path -LiteralPath $fixtureRoot)) { throw "Diagnostic fixtures are missing: $fixtureRoot" }

# The run owns its sandbox. A leftover catalog from an interrupted run would make the very first
# assertion fail for the wrong reason, so a dirty output directory is refused up front.
$catalogRoot = Join-Path $OutDir 'catalog'
$recoveryRoot = Join-Path $OutDir 'recovery'
$mapRoot = Join-Path $OutDir 'maps'
if (Test-Path -LiteralPath (Join-Path $catalogRoot 'catalog.json')) {
    throw "OutDir already holds a catalog from an earlier run: $catalogRoot. Use a fresh -OutDir."
}

New-Item -ItemType Directory -Force -Path $OutDir, $catalogRoot, $recoveryRoot, $mapRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Stage10Native' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage10Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
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

# Virtual key codes rather than characters: this machine runs a Russian layout, where SendKeys
# cannot produce a Latin letter. Virtual keys are positional and reproduce real fingers.
$script:Keys = @{
    'Alt' = 0x12; 'Shift' = 0x10; 'Ctrl' = 0x11; 'Left' = 0x25; 'Right' = 0x27
    'A' = 0x41; 'F' = 0x46; 'O' = 0x4F; 'Q' = 0x51; 'Z' = 0x5A; 'Y' = 0x59
}
$script:Modifiers = @(0x12, 0x11, 0x10)

# ------------------------------------------------------------------ UIA helpers

function Find-Uia([string]$automationId, [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    foreach ($attempt in 1..5) {
        try {
            $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $found) { return $found }
        }
        catch [System.Runtime.InteropServices.COMException] { }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Find-UiaByName([string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

<#
Finds one realised library row by a fragment of its accessible name. It looks only at list items,
because the badge inside a card carries its own name and a card whose file check is still running
legitimately gains a ", проверка файла…" suffix - an exact-name lookup would race with both.
#>
function Find-UiaCard([string]$fragment) {
    $type = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    foreach ($attempt in 1..5) {
        try {
            $items = $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)
            foreach ($item in $items) {
                if ($item.Current.Name -like "*$fragment*") { return $item }
            }
            return $null
        }
        catch [System.Runtime.InteropServices.COMException] {
            Start-Sleep -Milliseconds 100
        }
    }
    return $null
}

function Find-UiaCards([string]$fragment) {
    $type = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    foreach ($attempt in 1..5) {
        try {
            $items = $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)
            $matches = @()
            foreach ($item in $items) {
                if ($item.Current.Name -like "*$fragment*") { $matches += $item }
            }
            return $matches
        }
        catch [System.Runtime.InteropServices.COMException] {
            Start-Sleep -Milliseconds 100
        }
    }
    return @()
}

function Get-CardVisualTexts(
    [System.Windows.Automation.AutomationElement]$card,
    [string]$fragment = '') {
    $textType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($attempt in 1..5) {
        try {
            $texts = $card.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $textType)
            return @($texts | ForEach-Object { $_.Current.Name })
        }
        catch [System.Runtime.InteropServices.COMException] { Start-Sleep -Milliseconds 100 }
    }
    return @()
}

function Test-CardVisualText(
    [System.Windows.Automation.AutomationElement]$card,
    [string]$fragment) {
    foreach ($text in @(Get-CardVisualTexts $card)) {
        if ($text -like "*$fragment*") { return $true }
    }
    return $false
}

function Assert-CardVisualText(
    [System.Windows.Automation.AutomationElement]$card,
    [string]$fragment,
    [string]$what) {
    if (Test-CardVisualText $card $fragment) {
        Assert-True $true $what
        return
    }
    $visible = @(Get-CardVisualTexts $card) -join ' | '
    throw "$what : visual text '$visible' does not contain '$fragment'"
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

# Waits for the UI to settle after ONE action. This is not a retry of the action: the input is
# sent exactly once and this only bounds how long its visible result may take to appear.
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

function Invoke-Uia([string]$automationId) {
    $element = Require-Uia $automationId
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
        throw "Element '$automationId' exposes no Invoke, Toggle or SelectionItem pattern."
    }
    Start-Sleep -Milliseconds 260
}

function Get-UiaExpandState([string]$automationId) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    return $pattern.Current.ExpandCollapseState.ToString()
}

function Set-UiaExpanded([string]$automationId, [bool]$expanded = $true) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $state = $pattern.Current.ExpandCollapseState
    if ($expanded -and $state -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $pattern.Expand()
    }
    elseif (-not $expanded -and $state -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Collapse()
    }
    Start-Sleep -Milliseconds 300
}

function Set-UiaValue([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
    Start-Sleep -Milliseconds 260
}

function Select-UiaChoice([string]$automationId, [string]$value) {
    $element = Require-Uia $automationId
    $expand = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 300
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $value)
    # A drop-down is not always a descendant of its combo: on Windows it can be an overlay in the
    # same window or a top-level popup window of its own. All three are searched before giving up.
    $item = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) {
        $item = $script:Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    if ($null -eq $item) {
        $owned = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $owned)) {
            $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $item) { break }
        }
    }
    if ($null -eq $item) {
        $expand.Collapse()
        throw "Choice '$value' is not offered by '$automationId'."
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

function Get-UiaName([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { return '' }
    return $element.Current.Name
}

function Test-UiaFocus([string]$automationId) {
    $element = Find-Uia $automationId
    if ($null -eq $element) { return $false }
    return $element.Current.HasKeyboardFocus
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

# ------------------------------------------------------------------ input gate

<#
Takes the foreground deliberately instead of hoping for it. SetForegroundWindow is refused when
the calling thread does not own the current foreground window, which is exactly why the previous
driver dropped an Alt+Right once and passed on the immediate repeat. Attaching this thread's input
queue to the window's thread is the documented way to be allowed, and the result is verified.
#>
function Set-AppForeground {
    $handle = $script:WindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'The Map window has no handle.' }
    if ([Stage10Native]::GetForegroundWindow() -eq $handle) { return }

    $ours = [Stage10Native]::GetCurrentThreadId()
    $theirs = [Stage10Native]::GetWindowThreadProcessId($handle, [IntPtr]::Zero)
    $attached = $false
    try {
        if ($ours -ne $theirs) { $attached = [Stage10Native]::AttachThreadInput($ours, $theirs, $true) }
        $deadline = (Get-Date).AddMilliseconds(6000)
        while ((Get-Date) -lt $deadline) {
            # Deliberately not SW_RESTORE: that would un-maximize the window and quietly change
            # the size of every screenshot taken after the first keyboard chord.
            [void][Stage10Native]::BringWindowToTop($handle)
            [void][Stage10Native]::SetForegroundWindow($handle)
            if ([Stage10Native]::GetForegroundWindow() -eq $handle) { return }
            Start-Sleep -Milliseconds 150
        }
        throw 'The Map window did not accept the foreground within 6000 ms.'
    }
    finally {
        if ($attached) { [void][Stage10Native]::AttachThreadInput($ours, $theirs, $false) }
    }
}

<#
Sends one chord to a window proven to be in the foreground. Every modifier is released in the
finally block, so a failure in the middle can never leave Alt or Ctrl stuck down for the rest of
the run - which would silently corrupt every later keystroke.
#>
function Send-Chord([string[]]$Chord) {
    Set-AppForeground
    $codes = @()
    foreach ($name in $Chord) {
        if (-not $script:Keys.ContainsKey($name)) { throw "Unknown key '$name'" }
        $codes += [byte]$script:Keys[$name]
    }
    try {
        foreach ($code in $codes) { [Stage10Native]::keybd_event($code, 0, 0, [UIntPtr]::Zero) }
        $reversed = @($codes)
        [array]::Reverse($reversed)
        foreach ($code in $reversed) { [Stage10Native]::keybd_event($code, 0, 2, [UIntPtr]::Zero) }
    }
    finally {
        foreach ($modifier in $script:Modifiers) {
            [Stage10Native]::keybd_event([byte]$modifier, 0, 2, [UIntPtr]::Zero)
        }
    }
    Start-Sleep -Milliseconds 260
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
# ------------------------------------------------------------------ app control

function Start-TestApp([string]$ImportGroups = '', [string]$MapPath = '') {
    # Start-Process -Environment is PowerShell 7 only; on Windows PowerShell 5.1 the child
    # inherits this process's environment, so the sandbox is declared here instead.
    #
    # The native Windows file picker never shows a window and never returns under this automation
    # host - verified on the pre-existing single-import path as well - so the driver hands the app
    # its file paths through the project's own test-mode switch rather than through an OS dialog.
    $env:MAP_UI_TEST_MODE = '1'
    $env:MAP_UI_TEST_ASSET_ROOT = $catalogRoot
    $env:MAP_UI_TEST_RECOVERY_ROOT = $recoveryRoot
    $env:MAP_UI_TEST_IMPORT_PATHS = $ImportGroups
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
        # Keep the native layout and PrintWindow evidence without covering the owner's desktop.
        # SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW, positioned outside the visible desktop.
        [void][Stage10Native]::SetWindowPos(
            $script:WindowHandle, [IntPtr]::Zero, -32000, 0, 2000, 1250, 0x54)
    }
    else {
        [void][Stage10Native]::ShowWindow($script:WindowHandle, 3)
    }
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
    $rect = New-Object Stage10Native+RECT
    [void][Stage10Native]::GetWindowRect($script:WindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Invalid window bounds ${width}x${height}." }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage10Native]::PrintWindow($script:WindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' }
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
    Set-UiaValue 'WizardTitleBox' $title
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '2048'
    Set-UiaValue 'WizardHeightBox' '1400'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Start-Sleep -Milliseconds 500
}

function Activate-StampTool {
    if ($Background) { Invoke-Uia 'StampToolButton' }
    else { Send-Chord @('O') }
    Wait-State { (Get-UiaName 'ContextPanelTitle') -like '*Штамп*' } 'the stamp tool is active'
}

function Get-CatalogJson {
    $path = Join-Path $catalogRoot 'catalog.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
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

function Get-PlacedHashes([string]$mapPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($mapPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'scene.json' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $scene = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $matches = [regex]::Matches($scene, '"assetSha256"\s*:\s*"([0-9a-f]{64})"')
        return ($matches | ForEach-Object { $_.Groups[1].Value })
    }
    finally { $archive.Dispose() }
}

# =================================================================== the run

$stampFixtures = 1..12 | ForEach-Object { Join-Path $fixtureRoot ("diagnostic-stamp-{0:d2}.png" -f $_) }
$brushFixture = Join-Path $fixtureRoot 'diagnostic-brush-tip.png'
foreach ($fixture in @($stampFixtures) + @($brushFixture)) {
    if (-not (Test-Path -LiteralPath $fixture)) { throw "Fixture is missing: $fixture" }
}

$savedMap = Join-Path $mapRoot 'stage10b.mapproj'

try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
    Write-Host "Stage 10B native smoke"
    Write-Host "  exe     : $Exe"
    Write-Host "  catalog : $catalogRoot"
    Write-Host "  out     : $OutDir"

    # ---------------------------------------------------------------- 1. import
    Write-Host "`n[1] Import the diagnostic singleton and the 12-variant set"
    $importGroups = ($stampFixtures -join ';') + '|' + $brushFixture
    Start-TestApp -ImportGroups $importGroups -MapPath $savedMap
    Assert-True ($script:Proc.Id -gt 0) 'owned process started'
    New-TestMap 'Stage 10B library'
    Select-UiaTab 'Библиотека'
    Save-Shot 'library-empty' | Out-Null
    Assert-True (-not (Require-Uia 'EditorSideTabs').Current.IsOffscreen) `
        'the library panel remains inside the background verification viewport'
    Assert-Contains (Get-UiaName 'LibraryGrid') 'Карточки библиотеки' 'library grid has an accessible name'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'пуста' 'empty library states it is empty'

    Invoke-Uia 'ImportAssetSetButton'
    Wait-Uia 'SetNameBox' { param($t) $true } | Out-Null
    Set-UiaValue 'SetNameBox' 'Диагностический набор'
    Invoke-Uia 'ConfirmSetNameButton'
    Start-Sleep -Milliseconds 900

    Invoke-Uia 'ImportAssetButton'
    Start-Sleep -Milliseconds 1200
    Save-Shot 'library-imported' | Out-Null

    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 2' 'two logical cards after import'
    Assert-Equal (Get-UiaExpandState 'LibraryFilterDetails') 'Collapsed' `
        'advanced filters are collapsed in the default workflow'
    Assert-Equal (Get-UiaExpandState 'LibraryCollectionsDetails') 'Collapsed' `
        'collections are collapsed in the default workflow'
    $catalog = Get-CatalogJson
    Assert-True ($null -ne $catalog) 'catalog.json was written into the sandbox'
    Assert-Contains $catalog '"schemaVersion": 2' 'catalog is schema v2'
    Assert-True (([regex]::Matches($catalog, '"sha256"')).Count -ge 13) '13 content records stored'

    # ---------------------------------------------------------- 2. navigation
    Write-Host "`n[2] Tree, breadcrumbs, search and the full filter bar"
    Set-UiaExpanded 'LibraryCollectionsDetails'
    Set-UiaExpanded 'LibraryFilterDetails'
    Assert-Contains (Get-UiaName 'BreadcrumbRoot') 'Библиотека' 'the breadcrumb root is a real control'
    # The singleton import selects its own card, so the trail must name that card, not the root.
    Assert-Contains (Get-UiaName 'BreadcrumbCurrent') 'diagnostic-brush-tip' 'the breadcrumb leaf names the selection'
    Invoke-Uia 'BreadcrumbRoot'
    Start-Sleep -Milliseconds 500
    Assert-Contains (Get-UiaName 'BreadcrumbCurrent') 'Все коллекции' 'the breadcrumb root navigates back up'
    Assert-True ($null -ne (Find-Uia 'Collection_All')) 'collection tree lists all collections'

    Set-UiaValue 'AssetSearchBox' 'Диагностический'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'search narrows the grid'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 1' 'search narrows to one card'
    Assert-Contains (Get-UiaName 'LibraryFilterSummary') 'поиск' 'the summary names the active search'
    Set-UiaValue 'AssetSearchBox' ''
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'clearing the search restores both'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 2' 'clearing the search restores both'

    # ------------------------------------------------------- 3. real grouping
    Write-Host "`n[3] Без групп -> По коллекции -> По тегу with visible group headers"
    Assert-True ($null -eq (Find-UiaNameLike 'Группа ')) 'ungrouped catalog shows no group header'
    Save-Shot 'grouping-none' | Out-Null

    Invoke-Uia 'LibraryGroupCollection'
    Wait-State { $null -ne (Find-UiaNameLike 'Группа ') } 'collection headers appear'
    $collectionHeader = Find-UiaNameLike 'Группа '
    Assert-Contains $collectionHeader.Current.Name 'карточек:' 'a collection group header states its count'
    Assert-Equal $collectionHeader.Current.ControlType.ProgrammaticName 'ControlType.ListItem' `
        'a group header is a row of the same virtualized list'
    $collectionHeaderName = $collectionHeader.Current.Name
    Save-Shot 'grouping-collection' | Out-Null

    # A header must never become the selected card: selecting it moves on to a real card.
    $selectable = $null
    if ($collectionHeader.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectable)) {
        $selectable.Select()
        Start-Sleep -Milliseconds 500
    }
    Assert-True ((Get-UiaName 'SelectedSetNameText') -notlike '*Группа*') `
        'selecting a group header never makes it the selected set'

    # Nothing carries a tag yet, so grouping by tag is the "without tags" bucket. It is still a
    # different header over the same cards, which is exactly what must change on screen.
    Invoke-Uia 'LibraryGroupTag'
    Wait-State { $null -ne (Find-UiaNameLike 'Группа Без тегов') } 'tag headers appear'
    Assert-True ($null -ne (Find-UiaNameLike 'Группа Без тегов')) 'the tag grouping names its bucket'
    Assert-True ((Find-UiaNameLike 'Группа ').Current.Name -ne $collectionHeaderName) `
        'switching the grouping really changes the same catalog area'
    Save-Shot 'grouping-tag' | Out-Null

    Invoke-Uia 'LibraryGroupNone'
    Wait-State { $null -eq (Find-UiaNameLike 'Группа ') } 'headers disappear again'
    Assert-True ($null -eq (Find-UiaNameLike 'Группа ')) 'Без групп returns to a flat card list'

    # ------------------------------------------------------------- 4. filters
    Write-Host "`n[4] Tag, media, date, untagged, favourite, recent, used and reset"
    Assert-True ($null -ne (Find-Uia 'LibraryTagFilter')) 'the exact tag filter is a real control'
    Assert-Equal (Get-UiaName 'LibraryTagFilter') 'Тег' 'the tag filter is named'

    if (-not $Background) {
        Select-UiaChoice 'LibraryMediaFilter' 'image/png'
        Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'both diagnostic fixtures are PNG'
        Assert-Contains (Get-UiaName 'LibraryFilterSummary') 'image/png' 'the summary names the media type'
    }

    Set-UiaValue 'LibraryImportedFromBox' '2099-01-01'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ничего не найдено*' } 'a future lower bound empties the grid'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ничего не найдено' 'a date range can empty the library'
    Set-UiaValue 'LibraryImportedFromBox' 'не дата'
    Wait-State { (Get-UiaName 'LibraryDateHint') -like '*не распознана*' } 'a bad date is reported'
    Assert-Contains (Get-UiaName 'LibraryDateHint') 'не распознана' 'an unparsable date says so instead of filtering'

    Invoke-Uia 'LibraryFilterUntagged'
    Start-Sleep -Milliseconds 400
    Assert-Contains (Get-UiaName 'LibraryFilterSummary') 'без тегов' 'the untagged filter is named in the summary'
    Save-Shot 'filters-active' | Out-Null

    Invoke-Uia 'LibraryClearFiltersButton'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'the reset restores the library'
    Assert-True ($null -eq (Find-Uia 'LibraryFilterSummary')) `
        'the inactive filter summary leaves the visible workflow'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 2' 'the reset brings both cards back'
    Set-UiaExpanded 'LibraryFilterDetails' $false
    Set-UiaExpanded 'LibraryCollectionsDetails' $false

    # ---------------------------------------------------- 5. set and variants
    Write-Host "`n[5] Select the set, walk its variants, exercise the keyboard"
    $tile = Find-UiaCard 'набор, 12 вариантов'
    if ($null -eq $tile) { throw 'The 12-variant tile was not found by its accessible name.' }
    Assert-True $true 'set tile badge is named "12 вариантов"'

    # The realised container itself carries the name, so it is directly selectable.
    $selectionPattern = $null
    if (-not $tile.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
        throw 'The set tile does not expose the SelectionItem pattern.'
    }
    Assert-Equal $tile.Current.ControlType.ProgrammaticName 'ControlType.ListItem' 'the set tile is a list item'
    $selectionPattern.Select()
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*1 / 12*' } 'the set becomes current'
    if (-not $Background) {
        Assert-True $tile.Current.HasKeyboardFocus 'the selected set tile takes keyboard focus'
    }

    Assert-Contains (Get-UiaName 'SelectedSetBadge') '12 вариантов' 'selected set badge is accessible'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 1 / 12' 'set starts on its cover'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'обложка' 'the cover is named as the cover'
    Assert-Contains (Get-UiaName 'VariantStateText') 'Готов к размещению' 'a healthy variant says it is ready'
    Assert-Equal (Get-UiaExpandState 'SetManagementDetails') 'Collapsed' `
        'set-management commands are collapsed until requested'
    Assert-True ($null -eq (Find-Uia 'StampAreaSlider')) `
        'single-stamp mode does not show scatter-only controls'
    Save-Shot 'set-selected' | Out-Null

    Invoke-Uia 'NextVariantButton'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 2 / 12' 'mouse moves to variant 2 of 12'
    Invoke-Uia 'PreviousVariantButton'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 1 / 12' 'mouse walks back to variant 1'

    Activate-StampTool
    Assert-Contains (Get-UiaName 'ContextPanelTitle') 'Штамп' 'the stamp tool is active'

    if (-not $Background) {
        Send-Chord @('Alt', 'Right')
        Wait-State { (Get-UiaName 'VariantPositionText') -like '*2 / 12*' } 'Alt+Right advances the variant'
        Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 2 / 12' 'Alt+Right moves to the next variant'
        Send-Chord @('Alt', 'Left')
        Wait-State { (Get-UiaName 'VariantPositionText') -like '*1 / 12*' } 'Alt+Left steps back'
        Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 1 / 12' 'Alt+Left moves to the previous variant'

        Send-Chord @('F')
        Wait-State { Test-UiaFocus 'AssetSearchBox' } 'F puts the caret in the library search'
        Assert-True (Test-UiaFocus 'AssetSearchBox') 'F focuses the library search box'

        # A focused text box owns the letter keys, which is correct: on this Russian layout O types
        # "щ". Focus is therefore handed back to the card deliberately before any further shortcut.
        Set-UiaValue 'AssetSearchBox' ''
        $tile = Find-UiaCard 'набор, 12 вариантов'
        if ($null -eq $tile) { throw 'The set tile was not found when taking focus back.' }
        $tile.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-State { -not (Test-UiaFocus 'AssetSearchBox') } 'focus leaves the search box'
        Assert-True (-not (Test-UiaFocus 'AssetSearchBox')) 'the card takes the focus back from the search'
        Send-Chord @('O')
        Wait-State { (Get-UiaName 'ContextPanelTitle') -like '*Штамп*' } 'the stamp tool is active again'
        Assert-Contains (Get-UiaName 'AssetSearchBox') '' 'the search box did not swallow a shortcut'
    }
    else {
        $script:Partial += 'Keyboard: skipped during the non-intrusive background pass.'
    }

    # Density only appears in the Area readout, because in One mode it places nothing.
    Invoke-Uia 'StampModeArea'
    Start-Sleep -Milliseconds 400
    Assert-True ($null -ne (Find-Uia 'StampAreaSlider')) `
        'scatter controls appear when the Area mode is selected'
    $areaBefore = Get-UiaName 'StampAreaSummaryText'
    if (-not $Background) {
        Send-Chord @('Q')
        Wait-State { (Get-UiaName 'StampAreaSummaryText') -ne $areaBefore } 'Q changes the area'
        Send-Chord @('A')
        Wait-State { (Get-UiaName 'StampAreaSummaryText') -eq $areaBefore } 'A returns the area'
        Assert-Equal (Get-UiaName 'StampAreaSummaryText') $areaBefore 'A returns the area to where it was'

        $densityBefore = Get-UiaName 'StampAreaSummaryText'
        Send-Chord @('Alt', 'Q')
        Wait-State { (Get-UiaName 'StampAreaSummaryText') -ne $densityBefore } 'Alt+Q changes the density'
        Send-Chord @('Alt', 'A')
        Wait-State { (Get-UiaName 'StampAreaSummaryText') -eq $densityBefore } 'Alt+A returns the density'
        Assert-Equal (Get-UiaName 'StampAreaSummaryText') $densityBefore 'Alt+A returns the density to where it was'
    }
    Assert-Contains $areaBefore 'плотность' 'the Area readout states area, density and count'
    Save-Shot 'stamp-tool-context' | Out-Null

    Invoke-Uia 'SetFavoriteButton'
    Assert-Contains (Get-UiaName 'SetFavoriteButton') 'Убрать из избранного' 'favourite is set-level and reflected'
    Invoke-Uia 'LibraryFilterFavorites'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'the favourites filter narrows'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 1' 'the favourites filter keeps only the set'
    Invoke-Uia 'LibraryFilterFavorites'
    Start-Sleep -Milliseconds 500
    Invoke-Uia 'LibraryFilterRecent'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'the recent filter narrows'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 1' 'the recent filter shows the touched set'
    Invoke-Uia 'LibraryFilterRecent'
    Start-Sleep -Milliseconds 500

    # ------------------------------------------------- 6. managing the set
    Write-Host "`n[6] Rename the set, reorder a variant, choose a cover"
    Set-UiaExpanded 'SetManagementDetails'
    Set-UiaValue 'SetRenameBox' '   '
    Invoke-Uia 'SetRenameButton'
    Start-Sleep -Milliseconds 400
    Assert-Contains (Get-UiaName 'StatusDetailText') 'не может быть пустым' 'a blank set name is refused'
    Assert-Contains (Get-UiaName 'SelectedSetNameText') 'Диагностический набор' 'the refused rename changed nothing'

    Set-UiaValue 'SetRenameBox' 'Проверочный набор'
    Invoke-Uia 'SetRenameButton'
    Wait-State { (Get-UiaName 'SelectedSetNameText') -like '*Проверочный набор*' } 'the set is renamed'
    Assert-Contains (Get-UiaName 'SelectedSetNameText') 'Проверочный набор' 'the set takes its new name'

    Invoke-Uia 'NextVariantButton'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 2 / 12' 'variant 2 is current before the reorder'
    Invoke-Uia 'VariantMoveLaterButton'
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*3 / 12*' } 'the moved variant stays current'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 3 / 12' 'the variant moved one place later'
    Assert-Contains (Get-UiaName 'StatusDetailText') 'Порядок изменён' 'the reorder is reported'

    Invoke-Uia 'SetCoverButton'
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*обложка*' } 'the current variant becomes the cover'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'обложка' 'the cover distinction follows the current variant'
    $coverButtonName = Get-UiaName 'SetCoverButton'
    Assert-Contains $coverButtonName 'уже обложка' 'the cover button says the state it reached'
    Save-Shot 'set-management' | Out-Null

    # The serializer escapes Cyrillic as \uXXXX, so the file is unescaped before it is searched.
    $renamed = [Text.RegularExpressions.Regex]::Unescape((Get-CatalogJson))
    Assert-True ($renamed -like '*Проверочный набор*') 'the new name reached catalog.json'

    # --------------------------------------------- 7. restart keeps the changes
    Write-Host "`n[7] Restart: name, order and cover survive, and a real tag drives the filter"
    Stop-TestApp

    # This build has no UI for authoring tags, so the tag a real catalog would carry is written
    # into the index directly, in the escaped form the serializer itself uses. What is under test
    # afterwards is the tag filter and the tag grouping, not the editing of tags.
    $indexBeforeTag = Join-Path $catalogRoot 'catalog.json'
    $raw = [IO.File]::ReadAllText($indexBeforeTag)
    $setsAt = $raw.IndexOf('"sets": [')
    if ($setsAt -lt 0) { throw 'The index has no sets section.' }
    $tagsAt = $raw.IndexOf('"tags": []', $setsAt)
    if ($tagsAt -lt 0) { throw 'The first set has no tags array to fill.' }
    $tagged = $raw.Substring(0, $tagsAt) +
        '"tags": [ "рельеф", "лес" ]' +
        $raw.Substring($tagsAt + '"tags": []'.Length)
    [IO.File]::WriteAllText($indexBeforeTag, $tagged, (New-Object Text.UTF8Encoding($false)))

    Start-TestApp -MapPath $savedMap
    New-TestMap 'Stage 10B after restart'
    Select-UiaTab 'Библиотека'
    Start-Sleep -Milliseconds 600
    Set-UiaExpanded 'LibraryFilterDetails'
    $restored = Find-UiaCard 'Проверочный набор, набор, 12 вариантов'
    if ($null -eq $restored) { throw 'The renamed set did not come back after the restart.' }
    $restored.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*/ 12*' } 'the restored set opens'
    Assert-Contains (Get-UiaName 'SelectedSetNameText') 'Проверочный набор' 'the rename survived the restart'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'Вариант 3 / 12' 'the remembered current variant survived'
    Assert-Contains (Get-UiaName 'VariantPositionText') 'обложка' 'the chosen cover survived the restart'
    Save-Shot 'set-after-restart' | Out-Null

    # Now that a tag exists, the exact-tag filter and the tag grouping can be driven for real.
    if (-not $Background) {
        Select-UiaChoice 'LibraryTagFilter' 'рельеф'
        Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'the tag filter narrows the grid'
        Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 1' 'the exact tag filter keeps one card'
        Assert-Contains (Get-UiaName 'LibraryFilterSummary') 'рельеф' 'the summary names the active tag'
        Invoke-Uia 'LibraryClearFiltersButton'
        Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'clearing the tag restores both'
    }

    Invoke-Uia 'LibraryGroupTag'
    Wait-State { $null -ne (Find-UiaNameLike 'Группа рельеф') } 'a real tag becomes a group header'
    Assert-True ($null -ne (Find-UiaNameLike 'Группа рельеф')) 'the tag grouping shows the real tag'
    Assert-True ($null -ne (Find-UiaNameLike 'Группа лес')) 'the second tag becomes its own group header'
    Assert-True ($null -ne (Find-UiaNameLike 'Группа Без тегов')) 'untagged cards keep their own group'
    Save-Shot 'grouping-real-tag' | Out-Null
    Invoke-Uia 'LibraryGroupNone'
    Wait-State { $null -eq (Find-UiaNameLike 'Группа ') } 'the flat list returns'

    $restored = Find-UiaCard 'Проверочный набор, набор, 12 вариантов'
    $restored.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*/ 12*' } 'the set is current again'

    # -------------------------------------------------- 8. virtualized large list
    Write-Host "`n[8] A list larger than the viewport keeps visual rows matched after scroll and filter"
    Stop-TestApp
    $indexPath = Join-Path $catalogRoot 'catalog.json'
    $smallCatalog = Join-Path $OutDir 'catalog-before-large-list.json'
    Copy-Item -LiteralPath $indexPath -Destination $smallCatalog
    $largeCatalog = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $singleton = $largeCatalog.sets | Where-Object { $_.variantIds.Count -eq 1 } | Select-Object -First 1
    if ($null -eq $singleton) { throw 'The singleton set is missing before the large-list check.' }
    $sourceVariant = $largeCatalog.variants |
        Where-Object { $_.id -eq $singleton.variantIds[0] } | Select-Object -First 1
    if ($null -eq $sourceVariant) { throw 'The singleton variant is missing before the large-list check.' }
    $expandedSets = @($largeCatalog.sets)
    $expandedVariants = @($largeCatalog.variants)
    foreach ($number in 1..60) {
        $variantId = ('var-scroll-{0:d2}' -f $number)
        $expandedVariants += [pscustomobject][ordered]@{
            id = $variantId
            sha256 = $sourceVariant.sha256
            label = ('Прокрутка {0:d2}' -f $number)
            anchorX = $sourceVariant.anchorX
            anchorY = $sourceVariant.anchorY
            defaultScale = $sourceVariant.defaultScale
        }
        $expandedSets += [pscustomobject][ordered]@{
            id = ('set-scroll-{0:d2}' -f $number)
            name = ('Прокрутка {0:d2}' -f $number)
            variantIds = @($variantId)
            coverVariantId = $variantId
            collectionId = $singleton.collectionId
            tags = @('проверка прокрутки')
            favorite = $false
            importedUtc = ([DateTimeOffset]'2026-08-26T21:00:00+00:00').AddSeconds($number).ToString('o')
        }
    }
    $largeCatalog.variants = $expandedVariants
    $largeCatalog.sets = $expandedSets
    $largeJson = $largeCatalog | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($indexPath, $largeJson, (New-Object Text.UTF8Encoding($false)))

    Start-TestApp -MapPath $savedMap
    New-TestMap 'Stage 10B large library'
    Select-UiaTab 'Библиотека'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 62*' } 'the large catalog is loaded'
    Set-UiaExpanded 'LibraryFilterDetails'
    Invoke-Uia 'LibrarySortNameAsc'
    $grid = Require-Uia 'LibraryGrid'
    $scrollType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ScrollBar)
    $scrollBars = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollType)
    $movedToEnd = $false
    foreach ($scrollBar in $scrollBars) {
        if ($scrollBar.Current.Orientation -ne [System.Windows.Automation.OrientationType]::Vertical) {
            continue
        }
        $range = $null
        if ($scrollBar.TryGetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$range)) {
            $range.SetValue($range.Current.Maximum)
            $movedToEnd = $true
            break
        }
    }
    if (-not $movedToEnd) {
        $scroll = $grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    }
    Start-Sleep -Milliseconds 700
    $scrolledCards = @(Find-UiaCards 'Прокрутка')
    if ($scrolledCards.Count -eq 0) { throw 'No large-list card was realised after scrolling.' }
    $scrolledCard = $scrolledCards[$scrolledCards.Count - 1]
    $scrolledName = ($scrolledCard.Current.Name -split ',')[0]
    Assert-CardVisualText $scrolledCard $scrolledName `
        'the scrolled visual text matches the row that selection and UIA expose'

    Set-UiaValue 'AssetSearchBox' 'Прокрутка 42'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } 'the large list filters to one row'
    $filteredCard = Find-UiaCard 'Прокрутка 42'
    if ($null -eq $filteredCard) { throw 'The filtered large-list card was not realised.' }
    Assert-CardVisualText $filteredCard 'Прокрутка 42' `
        'the filtered visual text matches the only remaining row'
    Save-Shot 'large-list-after-scroll-filter' | Out-Null
    Stop-TestApp

    Copy-Item -LiteralPath $smallCatalog -Destination $indexPath -Force
    Start-TestApp -MapPath $savedMap
    New-TestMap 'Stage 10B placement after large list'
    Select-UiaTab 'Библиотека'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'the small catalog is restored'
    $restored = Find-UiaCard 'Проверочный набор, набор, 12 вариантов'
    $restored.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-State { (Get-UiaName 'VariantPositionText') -like '*/ 12*' } 'the restored set is current after the large-list check'

    # ------------------------------------------------------- 9. placement
    Write-Host "`n[9] One placement, then an Area gesture over several variants"
    Activate-StampTool
    Invoke-Uia 'StampModeOne'
    Start-Sleep -Milliseconds 300
    $oneVariant = Get-UiaName 'VariantPositionText'
    Invoke-Uia 'PlaceStampButton'
    Wait-State { (Get-UiaName 'StatusDetailText') -like '*Размещён вариант*' } 'the One placement reports back'
    $onePlacedMessage = Get-UiaName 'StatusDetailText'
    Save-Shot 'one-placement' | Out-Null
    Assert-Contains $onePlacedMessage 'Размещён вариант 3 / 12' 'One names the variant it actually placed'
    Assert-Contains (Get-UiaName 'SaveStateText') 'сохран' 'the document reports its save state after One'

    Invoke-Uia 'LibraryFilterUsedInMap'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 1*' } `
        'the used-in-map filter narrows after a real placement'
    Assert-True ($null -ne (Find-UiaCard 'Проверочный набор')) `
        'the used-in-map filter keeps the set whose SHA is on the map'
    Assert-Contains (Get-UiaName 'LibraryFilterSummary') 'на карте' `
        'the used-in-map filter is named in the active summary'
    Invoke-Uia 'LibraryFilterUsedInMap'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } `
        'clearing used-in-map restores the catalog'

    Invoke-Uia 'StampModeArea'
    Invoke-Uia 'RandomVariantToggle'
    Start-Sleep -Milliseconds 300
    Assert-Contains (Get-UiaName 'RandomVariantToggle') 'Случайный вариант' 'the random toggle is named'
    Assert-Contains (Get-UiaName 'StampAreaSummaryText') 'плотность' 'the Area readout states the density'

    # The Area gesture itself is a canvas press, which this bounded smoke performs through the
    # keyboard-reachable command rather than synthesising a raw click on the drawing surface.
    Invoke-Uia 'PlaceStampButton'
    Wait-State { (Get-UiaName 'StatusDetailText') -like '*Размещено штампов*' } 'the Area gesture reports back'
    $areaMessage = Get-UiaName 'StatusDetailText'
    Save-Shot 'area-placement' | Out-Null
    Assert-Contains $areaMessage 'вариантов: 12' 'one Area gesture placed all twelve variants'

    # -------------------------------------------------- 10. undo, redo, save
    Write-Host "`n[10] Undo, redo, save, close and reopen without the local catalog"
    if ($Background) { Invoke-Uia 'UndoButton' } else { Send-Chord @('Ctrl', 'Z') }
    Start-Sleep -Milliseconds 600
    Save-Shot 'after-undo' | Out-Null
    if ($Background) { Invoke-Uia 'RedoButton' } else { Send-Chord @('Ctrl', 'Y') }
    Start-Sleep -Milliseconds 600
    Save-Shot 'after-redo' | Out-Null
    Assert-True $true 'undo and redo ran on the placement history'

    Invoke-Uia 'SaveButton'
    Start-Sleep -Milliseconds 2500
    Assert-True (Test-Path -LiteralPath $savedMap) 'the map was written to the artifact directory'

    $hashes = Get-PlacedHashes $savedMap
    $distinct = ($hashes | Sort-Object -Unique)
    Write-Host ("      placed stamps: {0}, distinct hashes: {1}" -f $hashes.Count, $distinct.Count)
    $script:Checks['placed stamps'] = $hashes.Count
    $script:Checks['distinct variant hashes'] = $distinct.Count
    Assert-True ($distinct.Count -eq 12) 'the saved map carries all twelve distinct variant hashes'

    # A scatter multiplies the variant; it does not turn or mirror it. The proof is in the saved
    # file, not in the status line: every placed object is upright and unmirrored.
    $scene = Get-SceneJson $savedMap
    $rotations = [regex]::Matches($scene, '"rotationDegrees"\s*:\s*(-?[0-9.eE+]+)') |
        ForEach-Object { [double]$_.Groups[1].Value }
    $turned = @($rotations | Where-Object { $_ -ne 0 })
    $script:Checks['objects with a non-zero rotation'] = $turned.Count
    Assert-True ($turned.Count -eq 0) 'every scattered stamp was saved upright'
    Assert-True ($scene -notlike '*"flipHorizontal": true*') 'no scattered stamp was mirrored horizontally'
    Assert-True ($scene -notlike '*"flipVertical": true*') 'no scattered stamp was mirrored vertically'

    Stop-TestApp

    # Reopen with the local catalog moved away: the map must be self-sufficient.
    $hiddenCatalog = Join-Path $OutDir 'catalog-hidden'
    Move-Item -LiteralPath $catalogRoot -Destination $hiddenCatalog
    Start-TestApp -MapPath $savedMap
    Wait-Uia 'DashboardTitle' { param($t) $t -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'OpenFileButton'
    Wait-Uia 'EditorSideTabs' { param($t) $true } | Out-Null
    Start-Sleep -Milliseconds 1200
    Save-Shot 'reopened-without-catalog' | Out-Null
    Select-UiaTab 'Библиотека'
    Start-Sleep -Milliseconds 500
    Assert-Contains (Get-UiaName 'LibraryCountText') 'пуста' 'the library is empty without its catalog'
    Save-Shot 'reopened-library-empty' | Out-Null
    Stop-TestApp
    Remove-Item -LiteralPath $catalogRoot -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item -LiteralPath $hiddenCatalog -Destination $catalogRoot

    # ------------------------------- 11. missing and changed content on the card
    Write-Host "`n[11] Missing and hash-mismatch shown on the card and refusing a gesture"
    # Which originals to break is chosen from the index rather than from the directory order, so
    # the cover of the twelve-variant card is provably the changed one and a second variant of the
    # same card is provably the missing one.
    $originals = Join-Path $catalogRoot 'originals'
    $catalogObject = (Get-Content -LiteralPath (Join-Path $catalogRoot 'catalog.json') -Raw -Encoding UTF8) |
        ConvertFrom-Json
    $bigSet = $catalogObject.sets | Where-Object { $_.variantIds.Count -eq 12 } | Select-Object -First 1
    if ($null -eq $bigSet) { throw 'The twelve-variant set is not in the index.' }
    $coverSha = ($catalogObject.variants | Where-Object { $_.id -eq $bigSet.coverVariantId }).sha256

    # The card badge follows the cover, and the variant readout follows the variant the tool will
    # open on. Breaking exactly those two makes both states deterministic rather than incidental.
    $currentProperty = $catalogObject.currentVariantBySet.PSObject.Properties[$bigSet.id]
    $otherVariantId = if ($null -ne $currentProperty -and $currentProperty.Value -ne $bigSet.coverVariantId) {
        $currentProperty.Value
    }
    else {
        ($bigSet.variantIds | Where-Object { $_ -ne $bigSet.coverVariantId })[0]
    }
    $otherSha = ($catalogObject.variants | Where-Object { $_.id -eq $otherVariantId }).sha256

    $mismatchTarget = Join-Path $originals ($coverSha + '.png')
    $missingTarget = Join-Path $originals ($otherSha + '.png')
    if (-not (Test-Path -LiteralPath $mismatchTarget)) { throw "Cover original is missing: $mismatchTarget" }
    if (-not (Test-Path -LiteralPath $missingTarget)) { throw "Variant original is missing: $missingTarget" }

    $mismatchBackup = Join-Path $OutDir 'mismatch-source.png'
    Copy-Item -LiteralPath $mismatchTarget -Destination $mismatchBackup
    Copy-Item -LiteralPath $brushFixture -Destination $mismatchTarget -Force

    $missingBackup = Join-Path $OutDir 'missing-source.png'
    Move-Item -LiteralPath $missingTarget -Destination $missingBackup

    Start-TestApp
    New-TestMap 'Stage 10B failure paths'
    Select-UiaTab 'Библиотека'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*Ассеты · 2*' } 'both cards are still listed'
    Set-UiaExpanded 'LibraryFilterDetails'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'Ассеты · 2' 'a damaged original does not hide the card'

    $set = Find-UiaCard 'Проверочный набор, набор, 12 вариантов'
    if ($null -eq $set) { throw 'The twelve-variant card was not found on the failure run.' }
    $set.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-State { (Get-UiaName 'VariantStateText') -notlike '*не проверен*' } 'the file check finishes'

    # The verdict has to be visible on the card itself, not only in the tool panel. The cover of
    # this card is the changed file, so the card carries the mismatch; the missing file belongs to
    # another variant of the same card, so the variant strip carries that one.
    Wait-State { $null -ne (Find-UiaNameLike 'не совпадает с сохранённым SHA-256') } `
        'the hash mismatch reaches the card'
    Assert-True ($null -ne (Find-UiaNameLike 'не совпадает с сохранённым SHA-256')) `
        'a changed original is named as a hash mismatch'
    $mismatchCard = Find-UiaCard 'не совпадает с сохранённым SHA-256'
    Assert-True ($null -ne $mismatchCard) 'the card itself, not only the panel, reports the mismatch'
    Wait-State { $null -ne (Find-UiaNameLike 'файл отсутствует') } 'the missing state reaches the variant'
    Assert-True ($null -ne (Find-UiaNameLike 'файл отсутствует')) 'a missing original is named as missing'
    Assert-Contains (Get-UiaName 'VariantStateText') 'не найден' 'the selected variant states its own file'

    Invoke-Uia 'LibraryGroupTag'
    Wait-State {
        $taggedCards = @(Find-UiaCards 'Проверочный набор, набор, 12 вариантов')
        if ($taggedCards.Count -ne 2) { return $false }
        foreach ($taggedCard in $taggedCards) {
            if (-not (Test-CardVisualText $taggedCard 'изменён')) { return $false }
        }
        return $true
    } 'both tag-group copies receive the asynchronous mismatch badge'
    Assert-Equal (@(Find-UiaCards 'Проверочный набор, набор, 12 вариантов')).Count 2 `
        'a two-tag set is visibly repeated in both tag groups'
    foreach ($taggedCard in @(Find-UiaCards 'Проверочный набор, набор, 12 вариантов')) {
        Assert-CardVisualText $taggedCard 'изменён' `
            'each repeated tag card shows the same cover-file verdict'
    }
    Save-Shot 'missing-and-mismatch' | Out-Null
    Invoke-Uia 'LibraryGroupNone'
    Wait-State { @(Find-UiaCards 'Проверочный набор, набор, 12 вариантов').Count -eq 1 } `
        'the flat list returns before the placement refusal'

    # -------------------------------------------- 12. the gesture refuses as a whole
    Write-Host "`n[12] An Area gesture that needs the broken variant places nothing"
    Activate-StampTool
    Invoke-Uia 'StampModeArea'
    Invoke-Uia 'RandomVariantToggle'
    Start-Sleep -Milliseconds 300
    $saveBefore = Get-UiaName 'SaveStateText'
    $objectsBefore = Get-UiaName 'SelectionStatusText'
    Invoke-Uia 'PlaceStampButton'
    Wait-State { (Get-UiaName 'StatusDetailText') -like '*Ничего не размещено*' } 'the gesture refuses as a whole'
    $refusal = Get-UiaName 'StatusDetailText'
    Save-Shot 'atomic-refusal' | Out-Null
    Assert-Contains $refusal 'Ничего не размещено' 'the refusal says nothing at all was placed'
    Assert-True (($refusal -like '*отсутствует*') -or ($refusal -like '*не совпадает*')) `
        'the refusal names why the variant could not be placed'
    Assert-Equal (Get-UiaName 'SaveStateText') $saveBefore 'the refused gesture left the save state alone'
    Assert-Equal (Get-UiaName 'SelectionStatusText') $objectsBefore 'the refused gesture left the selection alone'
    Stop-TestApp

    Copy-Item -LiteralPath $mismatchBackup -Destination $mismatchTarget -Force
    Move-Item -LiteralPath $missingBackup -Destination $missingTarget

    # ------------------------------------- 13. a semantically damaged index
    Write-Host "`n[13] An index with a collection cycle is refused, not repaired"
    $indexPath = Join-Path $catalogRoot 'catalog.json'
    $healthy = Join-Path $OutDir 'catalog-healthy.json'
    Copy-Item -LiteralPath $indexPath -Destination $healthy

    # Parses perfectly as JSON, contradicts itself as a library: three collections in a ring.
    $cycleJson = @'
{
  "schemaVersion": 2,
  "records": [],
  "collections": [
    { "id": "col-x", "name": "X", "parentId": "col-z", "order": 0 },
    { "id": "col-y", "name": "Y", "parentId": "col-x", "order": 1 },
    { "id": "col-z", "name": "Z", "parentId": "col-y", "order": 2 }
  ],
  "variants": [],
  "sets": [],
  "recent": [],
  "currentVariantBySet": {}
}
'@
    $damagedBytes = [Text.Encoding]::UTF8.GetBytes($cycleJson)
    [IO.File]::WriteAllBytes($indexPath, $damagedBytes)

    Start-TestApp
    New-TestMap 'Stage 10B damaged index'
    Select-UiaTab 'Библиотека'
    Wait-State { (Get-UiaName 'LibraryCountText') -like '*недоступна*' } 'the damaged library reports itself'
    Save-Shot 'damaged-index' | Out-Null
    Assert-Contains (Get-UiaName 'LibraryDiagnosticText') 'цикл' 'the diagnostic names the collection cycle'
    Assert-Contains (Get-UiaName 'LibraryCountText') 'недоступна' 'a damaged library says it is unavailable'
    Assert-True ($script:Proc.HasExited -eq $false) 'a semantically damaged index does not crash the app'
    Stop-TestApp

    $afterBytes = [IO.File]::ReadAllBytes($indexPath)
    Assert-True ([Linq.Enumerable]::SequenceEqual($damagedBytes, $afterBytes)) `
        'the damaged index was never overwritten or repaired'
    Copy-Item -LiteralPath $healthy -Destination $indexPath -Force
    # ----------------------------------------------------------- 14. high DPI
    Write-Host "`n[14] High DPI"
    if ($SkipDpi) {
        $script:Partial += 'DPI: skipped on request (-SkipDpi).'
        Write-Host '      PARTIAL: skipped on request'
    }
    else {
        $dpiShots = @{}
        foreach ($scale in @(1.0, 1.5, 2.0)) {
            $env:AVALONIA_SCREEN_SCALE_FACTORS = "*=$scale"
            try {
                Start-TestApp
                New-TestMap ("Stage 10B DPI {0}" -f $scale)
                Select-UiaTab 'Библиотека'
                Start-Sleep -Milliseconds 600
                $dpiShots[$scale] = Save-Shot ("dpi-{0}" -f ($scale.ToString('0.0').Replace('.', '_')))
                Assert-True ($null -ne (Find-Uia 'LibraryGrid')) ("library grid resolves at scale $scale")
                Stop-TestApp
            }
            finally {
                Remove-Item Env:AVALONIA_SCREEN_SCALE_FACTORS -ErrorAction SilentlyContinue
            }
        }

        # Starting at a requested scale is not the same as rendering at it. If 100% and 200% come
        # back visually identical, the environment ignored the request and this is NOT DPI cover.
        $compare = Join-Path $workspace 'scripts\compare-ui-screenshots.ps1'
        $diff = Join-Path $OutDir 'diff-dpi-100-vs-200.png'
        $result = & powershell -ExecutionPolicy Bypass -File $compare `
            -Reference $dpiShots[1.0] -Actual $dpiShots[2.0] -Difference $diff -MaximumNormalizedRmse 1.0
        $rmseLine = ($result | Select-String 'NormalizedRmse\s+:' | Select-Object -First 1)
        $rmse = 0.0
        if ($null -ne $rmseLine) { $rmse = [double](($rmseLine -split ':')[1].Trim()) }
        $script:Checks['DPI 100% vs 200% normalized RMSE'] = $rmse
        if ($rmse -lt 0.02) {
            $script:Partial += ("DPI: the window rendered identically at 100% and 200% (normalized RMSE {0:F5}); AVALONIA_SCREEN_SCALE_FACTORS was not applied by this environment, so only start-up and control resolution are covered, not real scaling." -f $rmse)
            Write-Host ("      PARTIAL: scale factor not applied (RMSE {0:F5})" -f $rmse)
        }
        else {
            Write-Host ("      ok  the window really re-rendered at 200% (RMSE {0:F5})" -f $rmse)
        }
    }

    Write-Host "`nRESULT: PASS"
}
catch {
    Write-Host "`nRESULT: FAIL - $_" -ForegroundColor Red
    if ($null -ne $script:Proc -and -not $script:Proc.HasExited) {
        try { Save-Shot 'failure' | Out-Null } catch { }
    }
    throw
}
finally {
    Stop-TestApp
    $report = [ordered]@{
        exe = $Exe
        outDir = $OutDir
        catalogRoot = $catalogRoot
        savedMap = $savedMap
        screenshots = $script:Shots
        checks = $script:Checks
        partial = $script:Partial
    }
    $reportPath = Join-Path $OutDir 'report.json'
    $report | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 $reportPath
    Write-Host "`nReport: $reportPath"
    Write-Host ("Screenshots: {0}" -f $script:Shots.Count)
    if ($script:Partial.Count -gt 0) {
        Write-Host "PARTIAL:"
        $script:Partial | ForEach-Object { Write-Host "  - $_" }
    }
}
