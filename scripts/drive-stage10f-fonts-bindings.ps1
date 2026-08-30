<#
.SYNOPSIS
Stage 10F native smoke for the local font catalog and editable keyboard bindings.

.DESCRIPTION
Starts the exact Release executable with isolated map, recovery and shortcut-settings paths. The
script uses UI Automation for controls and real keyboard input only where actual routing must be
proved. It executes the acceptance steps, inspects .mapproj/settings data, exports through
the production resolver and terminates only processes it started.
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
    $OutDir = Join-Path $workspace ("artifacts\stage10f-fonts-bindings\ui-smoke-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}
$Exe = [IO.Path]::GetFullPath($Exe)
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside the Map workspace: $OutDir"
}

$recoveryRoot = Join-Path $OutDir 'recovery'
$mapRoot = Join-Path $OutDir 'maps'
$savedMap = Join-Path $mapRoot 'stage10f-fonts-bindings.mapproj'
$settingsPath = Join-Path $OutDir 'isolated-shortcuts.json'
$exportPath = Join-Path $OutDir 'font-fallback-export.png'
New-Item -ItemType Directory -Force -Path $OutDir, $recoveryRoot, $mapRoot | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Stage10FNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Stage10FNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
"@
}

$script:Proc = $null
$script:Root = $null
$script:WindowHandle = [IntPtr]::Zero
$script:OwnedProcesses = New-Object Collections.Generic.List[object]
$script:Actions = New-Object Collections.Generic.List[string]
$script:Checks = [ordered]@{}
$script:Shots = New-Object Collections.Generic.List[string]
$script:Partial = New-Object Collections.Generic.List[string]
$script:Blocked = New-Object Collections.Generic.List[string]
$script:FontEvidence = [ordered]@{}
$script:ShortcutEvidence = [ordered]@{}

function Add-Action([string]$value) {
    $script:Actions.Add($value)
    Write-Host "      action  $value"
}

function Assert-True([bool]$condition, [string]$what, $actual = $null) {
    if (-not $condition) { throw "$what : condition was false; actual=$actual" }
    $script:Checks[$what] = if ($null -eq $actual) { $true } else { $actual }
    Write-Host "      ok  $what"
}

function Assert-Equal($actual, $expected, [string]$what) {
    if ($actual -ne $expected) { throw "$what : actual='$actual', expected='$expected'" }
    $script:Checks[$what] = $actual
    Write-Host "      ok  $what = $actual"
}

function Find-Uia(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    if ($null -eq $root) { $root = $script:Root }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    foreach ($attempt in 1..20) {
        try {
            $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $found) { return $found }
        }
        catch [System.Runtime.InteropServices.COMException] { }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Require-Uia(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    $element = Find-Uia $automationId $root
    if ($null -eq $element) { throw "Element '$automationId' was not found." }
    return $element
}

function Wait-Uia(
    [string]$automationId,
    [scriptblock]$until,
    [int]$timeoutMs = 20000,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $element = Find-Uia $automationId $root
        if ($null -ne $element) {
            $last = $element.Current.Name
            if (& $until $last) { return $element }
        }
        Start-Sleep -Milliseconds 180
    }
    throw "TIMEOUT waiting for '$automationId'. Last name: '$last'"
}

function Wait-State([scriptblock]$condition, [string]$what, [int]$timeoutMs = 15000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 180
    }
    throw "TIMEOUT waiting for state: $what"
}

function Invoke-UiaElement(
    [System.Windows.Automation.AutomationElement]$element,
    [string]$what) {
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
    Start-Sleep -Milliseconds 350
}

function Invoke-Uia(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    foreach ($attempt in 1..3) {
        try {
            Invoke-UiaElement (Require-Uia $automationId $root) $automationId
            return
        }
        catch [System.Windows.Automation.ElementNotAvailableException] { Start-Sleep -Milliseconds 300 }
        catch [System.ArgumentException] { Start-Sleep -Milliseconds 300 }
    }
    throw "Element '$automationId' could not be invoked after retries."
}

function Get-UiaName(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    $element = Find-Uia $automationId $root
    if ($null -eq $element) { return '' }
    return $element.Current.Name
}

function Get-UiaValue(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    $element = Require-Uia $automationId $root
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Set-UiaValue(
    [string]$automationId,
    [string]$value,
    [System.Windows.Automation.AutomationElement]$root = $null) {
    $element = Require-Uia $automationId $root
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 350
}

function Test-ActiveTool([string]$hintFragment) {
    return (Get-UiaName 'ToolHintText') -like "*$hintFragment*"
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

function Find-AutomationIdLike([string]$prefix) {
    $type = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($element in $script:Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $type)) {
        if ($element.Current.AutomationId -like "$prefix*") { return $element }
    }
    return $null
}

function Find-ProcessWindow([string]$title) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:Proc.Id)
    $condition = New-Object System.Windows.Automation.AndCondition(
        $nameCondition, $typeCondition, $processCondition)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Window([string]$title, [int]$timeoutMs = 15000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $window = Find-ProcessWindow $title
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 180
    }
    throw "Window '$title' did not appear."
}

function Close-UiaWindow([System.Windows.Automation.AutomationElement]$window) {
    $pattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $pattern.Close()
    Start-Sleep -Milliseconds 350
}

function Protect-AppWindow {
    if (-not $Background -or $script:WindowHandle -eq [IntPtr]::Zero) { return }
    [void][Stage10FNative]::ShowWindow($script:WindowHandle, 4)
    [void][Stage10FNative]::SetWindowPos(
        $script:WindowHandle, [IntPtr]::Zero, -32000, 0, 1700, 1080, 0x54)
    Start-Sleep -Milliseconds 300
}

function Start-TestApp {
    $env:MAP_UI_TEST_MODE = '1'
    $env:MAP_UI_TEST_RECOVERY_ROOT = $recoveryRoot
    $env:MAP_UI_TEST_MAP_PATH = $savedMap
    $env:MAP_UI_TEST_SHORTCUT_SETTINGS_PATH = $settingsPath
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    $windowRect = New-Object Stage10FNative+RECT
    $windowWidth = 0
    $windowHeight = 0
    foreach ($attempt in 1..80) {
        Start-Sleep -Milliseconds 250
        $script:Proc.Refresh()
        $candidateHandle = $script:Proc.MainWindowHandle
        if ($candidateHandle -eq [IntPtr]::Zero) { continue }
        if (-not [Stage10FNative]::GetWindowRect($candidateHandle, [ref]$windowRect)) { continue }
        $windowWidth = $windowRect.Right - $windowRect.Left
        $windowHeight = $windowRect.Bottom - $windowRect.Top
        if ($windowWidth -ge 960 -and $windowHeight -ge 640) { break }
    }
    if ($script:Proc.MainWindowHandle -eq [IntPtr]::Zero -or
        $windowWidth -lt 960 -or $windowHeight -lt 640) {
        throw "Full Map window did not appear; last bounds were ${windowWidth}x${windowHeight}."
    }
    $script:WindowHandle = $script:Proc.MainWindowHandle
    $dpi = [Stage10FNative]::GetDpiForWindow($script:WindowHandle)
    Protect-AppWindow
    $script:Root = [System.Windows.Automation.AutomationElement]::FromHandle($script:WindowHandle)
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($script:Proc.Id)"
    $script:OwnedProcesses.Add([ordered]@{
        pid = $script:Proc.Id
        path = $script:Proc.Path
        commandLine = $cim.CommandLine
        dpi = $dpi
        scalePercent = [Math]::Round(($dpi / 96.0) * 100)
        initialWindowBounds = "${windowWidth}x${windowHeight}"
    })
}

function Stop-TestApp {
    if ($null -ne $script:Proc -and -not $script:Proc.HasExited) {
        $ownedId = $script:Proc.Id
        $script:Proc.Kill()
        if (-not $script:Proc.WaitForExit(10000)) { throw "Owned Map process $ownedId did not terminate." }
    }
    $script:Proc = $null
    $script:Root = $null
    $script:WindowHandle = [IntPtr]::Zero
}

function Send-PhysicalKey(
    [byte]$virtualKey,
    [string]$focusAutomationId = 'MapCanvas',
    [System.Windows.Automation.AutomationElement]$root = $null,
    [byte[]]$modifiers = @()) {
    if ($null -eq $root) { $root = $script:Root }
    $handle = [IntPtr]$root.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { $handle = $script:WindowHandle }
    [void][Stage10FNative]::ShowWindow($handle, 4)
    [void][Stage10FNative]::SetForegroundWindow($handle)
    (Require-Uia $focusAutomationId $root).SetFocus()
    Start-Sleep -Milliseconds 150
    Send-PhysicalKeyToFocused $virtualKey $root $modifiers
}

function Send-PhysicalKeyToFocused(
    [byte]$virtualKey,
    [System.Windows.Automation.AutomationElement]$window = $null,
    [byte[]]$modifiers = @(),
    [int]$settleMilliseconds = 500) {
    if ($null -eq $window) { $window = $script:Root }
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { $handle = $script:WindowHandle }
    [void][Stage10FNative]::ShowWindow($handle, 4)
    [void][Stage10FNative]::SetForegroundWindow($handle)
    foreach ($modifier in $modifiers) {
        [Stage10FNative]::keybd_event($modifier, 0, 0, [UIntPtr]::Zero)
    }
    [Stage10FNative]::keybd_event($virtualKey, 0, 0, [UIntPtr]::Zero)
    [Stage10FNative]::keybd_event($virtualKey, 0, 2, [UIntPtr]::Zero)
    for ($index = $modifiers.Count - 1; $index -ge 0; $index--) {
        [Stage10FNative]::keybd_event($modifiers[$index], 0, 2, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds $settleMilliseconds
}

function Get-FocusedAutomationId {
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -eq $focused) { return '' }
    return $focused.Current.AutomationId
}

function Focus-WithPhysicalTab(
    [string]$targetAutomationId,
    [System.Windows.Automation.AutomationElement]$window,
    [string]$startAutomationId = 'ShortcutSearchBox',
    [int]$maximumTabs = 160) {
    (Require-Uia $startAutomationId $window).SetFocus()
    Start-Sleep -Milliseconds 250
    $trace = New-Object Collections.Generic.List[string]
    foreach ($attempt in 0..$maximumTabs) {
        $focusedId = Get-FocusedAutomationId
        if ($focusedId -and ($trace.Count -eq 0 -or $trace[$trace.Count - 1] -ne $focusedId)) {
            $trace.Add($focusedId)
        }
        if ($focusedId -eq $targetAutomationId) {
            Assert-True (Require-Uia $targetAutomationId $window).Current.HasKeyboardFocus `
                "physical Tab reached $targetAutomationId with keyboard focus"
            return @($trace)
        }
        Send-PhysicalKeyToFocused 0x09 $window @() 100 # VK_TAB
    }
    throw "Physical Tab did not reach '$targetAutomationId'. Trace: $($trace -join ' -> ')"
}

function Invoke-FocusedButtonByKeyboard(
    [string]$automationId,
    [System.Windows.Automation.AutomationElement]$window) {
    Assert-Equal (Get-FocusedAutomationId) $automationId "keyboard target is $automationId"
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][Stage10FNative]::ShowWindow($handle, 4)
    [void][Stage10FNative]::SetForegroundWindow($handle)
    [System.Windows.Forms.SendKeys]::SendWait(' ')
    Start-Sleep -Milliseconds 500
}

function Save-Shot(
    [string]$fileName,
    [System.Windows.Automation.AutomationElement]$window = $null) {
    $handle = if ($null -eq $window) { $script:WindowHandle } else { [IntPtr]$window.Current.NativeWindowHandle }
    if ($handle -eq [IntPtr]::Zero) { throw "No native handle for screenshot $fileName" }
    $rect = New-Object Stage10FNative+RECT
    [void][Stage10FNative]::GetWindowRect($handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 500 -or $height -lt 300) { throw "Invalid window bounds ${width}x${height}." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [Stage10FNative]::PrintWindow($handle, $hdc, 2)) { throw 'PrintWindow failed.' }
    }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $path = Join-Path $OutDir $fileName
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $script:Shots.Add($path)
}

function New-TestMap {
    Wait-Uia 'DashboardTitle' { param($text) $text -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'NewMapButton'
    Wait-Uia 'WizardTitleBox' { param($text) $true } | Out-Null
    Set-UiaValue 'WizardTitleBox' 'Stage 10F fonts and bindings'
    Invoke-Uia 'WizardPresetCustom'
    Set-UiaValue 'WizardWidthBox' '1200'
    Set-UiaValue 'WizardHeightBox' '800'
    Invoke-Uia 'CreateMapButton'
    Wait-Uia 'EditorSideTabs' { param($text) $true } | Out-Null
    Protect-AppWindow
}

function Save-Map {
    Invoke-Uia 'SaveButton'
    Wait-State { Test-Path -LiteralPath $savedMap } 'map archive exists' 20000
    Wait-State { (Get-UiaName 'SaveStateText') -eq 'Сохранено' } 'editor reports saved' 20000
}

function Get-TextStyle {
    foreach ($attempt in 1..20) {
        try {
            $archive = [IO.Compression.ZipFile]::OpenRead($savedMap)
            try {
                $entry = $archive.Entries | Where-Object FullName -eq 'scene.json'
                $reader = New-Object IO.StreamReader($entry.Open())
                try { $scene = $reader.ReadToEnd() | ConvertFrom-Json }
                finally { $reader.Dispose() }
            }
            finally { $archive.Dispose() }
            foreach ($layer in @($scene.layers)) {
                foreach ($object in @($layer.objects)) {
                    if ($null -ne $object.text) { return $object.text }
                }
            }
            throw 'No text object in scene.json.'
        }
        catch [IO.IOException] { Start-Sleep -Milliseconds 200 }
    }
    throw "Could not read text style from $savedMap"
}

function Get-FontItems {
    $list = Require-Uia 'TextFontList'
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Select-FontItem([string]$family) {
    foreach ($attempt in 1..20) {
        foreach ($item in Get-FontItems) {
            if ($item.Current.Name -eq $family) {
                $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 600
                return
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Font family '$family' was not visible in the native list."
}

function Select-CurrentTextObject {
    Select-UiaTab 'Объекты'
    $row = Find-AutomationIdLike 'ObjectRow_'
    if ($null -eq $row) { throw 'Text object row was not found.' }
    Invoke-UiaElement $row 'text object'
    Wait-Uia 'TextFontStateText' { param($text) $text -like '*Запрошено в документе*' } | Out-Null
}

function Open-SavedMapAndSelectText {
    Wait-Uia 'DashboardTitle' { param($text) $text -eq 'Ваши карты' } | Out-Null
    Invoke-Uia 'OpenFileButton'
    Wait-Uia 'EditorSideTabs' { param($text) $true } | Out-Null
    Select-CurrentTextObject
}

function Open-ShortcutSettings {
    Invoke-Uia 'ShortcutSettingsButton'
    $dialog = Wait-Window 'Сочетания клавиш'
    Wait-Uia 'ShortcutSearchBox' { param($text) $true } 10000 $dialog | Out-Null
    return $dialog
}

function Select-TextShortcut([System.Windows.Automation.AutomationElement]$dialog) {
    Invoke-Uia 'ShortcutCommand_text' $dialog
    Wait-Uia 'ShortcutGestureInput' { param($text) $true } 10000 $dialog | Out-Null
}

function Get-WindowText([System.Windows.Automation.AutomationElement]$window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    return (@($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) |
        ForEach-Object { $_.Current.Name }) -join "`n"
}

try {
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Executable not found: $Exe" }
    $expectedExe = [IO.Path]::GetFullPath((Join-Path $workspace 'src\Map.App\bin\Release\net10.0\Map.App.exe'))
    Assert-Equal $Exe $expectedExe 'exact Release EXE path'
    $exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Exe).Hash.ToLowerInvariant()
    $branch = (& git -C $workspace branch --show-current).Trim()
    $head = (& git -C $workspace rev-parse HEAD).Trim()

    Write-Host "Stage 10F native smoke (background: $Background)"
    Write-Host "  exe      : $Exe"
    Write-Host "  out      : $OutDir"
    Write-Host "  settings : $settingsPath"

    Write-Host "`n[1] Start exact Release executable and create isolated map"
    Start-TestApp
    Assert-Equal $script:Proc.Path $Exe 'running process executable identity'
    Assert-True ($script:OwnedProcesses[0].commandLine -like "*$Exe*") 'owned process command line contains exact executable' $script:OwnedProcesses[0].commandLine
    New-TestMap
    Add-Action 'created isolated 1200x800 map'

    Write-Host "`n[2] Create and select one text object"
    Invoke-Uia 'TextToolButton'
    Invoke-Uia 'AddTextButton'
    Wait-Uia 'TextFontStateText' { param($text) $text -like '*Запрошено в документе*' } | Out-Null
    Assert-True (Test-ActiveTool 'Щёлкните объект') 'creating text returns to Select with the new object selected'
    Add-Action 'created one selected text object through standard controls and observed the documented return to Select'

    Write-Host "`n[3] Open and inspect the real local font catalog"
    $search = Require-Uia 'TextFontSearchBox'
    $list = Require-Uia 'TextFontList'
    Assert-Equal $search.Current.ControlType.ProgrammaticName 'ControlType.Edit' 'font search UIA role'
    Assert-Equal $list.Current.ControlType.ProgrammaticName 'ControlType.List' 'font catalog UIA role'
    $families = @(Get-FontItems | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    Assert-True ($families.Count -gt 1) 'native catalog exposes multiple local families' $families.Count
    Save-Shot '01-font-catalog.png'
    Add-Action 'opened real Windows font families through Skia-backed catalog'

    Write-Host "`n[4] Search case-insensitively and select an available family"
    $chosen = $families | Where-Object { $_ -notmatch '^Arial$' } | Select-Object -First 1
    if (-not $chosen) { throw 'No second local font family was available.' }
    $query = $chosen.Substring(0, [Math]::Min(4, $chosen.Length)).ToLowerInvariant()
    Set-UiaValue 'TextFontSearchBox' $query
    Select-FontItem $chosen
    Wait-State { (Get-UiaName 'TextFontStateText') -like "*$chosen*" } 'selected font appears in requested state'
    $script:FontEvidence['selected'] = $chosen
    $script:FontEvidence['searchQuery'] = $query
    Add-Action "searched '$query' and selected '$chosen'"

    Write-Host "`n[5] Cycle to the next local family through the editable ] binding"
    Set-UiaValue 'TextFontSearchBox' ''
    $beforeCycle = Get-UiaName 'TextFontStateText'
    Send-PhysicalKey 0xDD # VK_OEM_6 / physical right-bracket key, independent of text layout
    Wait-State { (Get-UiaName 'TextFontStateText') -ne $beforeCycle } 'font changes through ] binding'
    $afterCycle = Get-UiaName 'TextFontStateText'
    $script:FontEvidence['afterCycleVisibleState'] = $afterCycle
    Add-Action 'cycled next font through the real keyboard router'

    Write-Host "`n[6] Undo and redo the font command exactly"
    Invoke-Uia 'UndoButton'
    Select-CurrentTextObject
    Wait-State { (Get-UiaName 'TextFontStateText') -like "*$chosen*" } 'font undo restores chosen family'
    Invoke-Uia 'RedoButton'
    Select-CurrentTextObject
    Wait-State { (Get-UiaName 'TextFontStateText') -eq $afterCycle } 'font redo restores cycled family'
    Add-Action 'verified one undo and one redo by reselecting the unchanged text object and reading its exact font state'

    Write-Host "`n[7] Save, restart and reopen the same requested font"
    Save-Map
    $savedStyle = Get-TextStyle
    $savedRequested = [string]$savedStyle.requestedFontFamily
    Assert-True ($savedRequested.Length -gt 0) 'requested font persisted to scene.json' $savedRequested
    Stop-TestApp
    Start-TestApp
    Open-SavedMapAndSelectText
    Assert-True ((Get-UiaName 'TextFontStateText') -like "*$savedRequested*") 'reopened UI shows saved requested font'
    Add-Action "saved and reopened requested family '$savedRequested'"

    Write-Host "`n[8] Prove missing requested font, separate fallback and export resolver"
    Invoke-Uia 'TextMissingFontButton'
    Wait-State { (Get-UiaName 'TextFontStateText') -like '*локальная замена*' } 'fallback is shown separately'
    $fallbackState = Get-UiaName 'TextFontStateText'
    Assert-True ($fallbackState -like '*Map Stage 7 Missing Font*') 'missing requested family remains visible'
    Assert-True ($fallbackState -like '*Фактически показано*') 'actual fallback is separately labelled'
    Save-Map
    $missingStyle = Get-TextStyle
    Assert-Equal ([string]$missingStyle.requestedFontFamily) 'Map Stage 7 Missing Font' 'missing request persisted exactly'
    Assert-Equal ([string]$missingStyle.fallbackFontFamily) 'Arial' 'fallback family persisted separately'
    $exportTool = Join-Path $workspace 'tools\Map.GridGoldenProposer\Map.GridGoldenProposer.csproj'
    $exportOutput = & dotnet run --project $exportTool --configuration Release --no-build -- `
        --export $savedMap $exportPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Production export resolver failed: $exportOutput" }
    Assert-True (Test-Path -LiteralPath $exportPath) 'export with missing requested font exists' $exportPath
    Save-Shot '02-missing-font-fallback.png'
    $script:FontEvidence['fallbackVisibleState'] = $fallbackState
    $script:FontEvidence['mapRequested'] = [string]$missingStyle.requestedFontFamily
    $script:FontEvidence['mapFallback'] = [string]$missingStyle.fallbackFontFamily
    $script:FontEvidence['export'] = $exportPath
    Add-Action 'verified separate missing request/fallback in UI, map data and production export'

    Write-Host "`n[9] Open the transactional shortcut editor"
    $settingsDialog = Open-ShortcutSettings
    Select-TextShortcut $settingsDialog
    Assert-Equal (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) 'T' 'Text default shown in editor'
    $tabTrace = @(Focus-WithPhysicalTab 'SaveShortcutSettingsButton' $settingsDialog)
    Save-Shot '03-keyboard-tab-focus.png' $settingsDialog
    $script:ShortcutEvidence['physicalTabTrace'] = $tabTrace
    Add-Action 'opened searchable/grouped shortcut editor and selected Text command'

    Write-Host "`n[10] Create a conflict and prove save is blocked"
    Send-PhysicalKey 0x56 'ShortcutGestureInput' $settingsDialog # V
    Wait-State { (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) -eq 'V' } 'V captured'
    Wait-State { (Get-UiaName 'ShortcutConflictText' $settingsDialog) -like '*Конфликт V*' } 'visible conflict message'
    $saveSettings = Require-Uia 'SaveShortcutSettingsButton' $settingsDialog
    Assert-True (-not $saveSettings.Current.IsEnabled) 'conflicting settings cannot be saved'
    Save-Shot '04-shortcut-conflict.png' $settingsDialog
    Add-Action 'created overlapping V conflict and observed disabled Save'

    Write-Host "`n[10A] Cancel the conflicting draft from the keyboard and prove focus return"
    [void](Focus-WithPhysicalTab 'CancelShortcutSettingsButton' $settingsDialog 'ShortcutGestureInput')
    Invoke-FocusedButtonByKeyboard 'CancelShortcutSettingsButton' $settingsDialog
    Wait-State { $null -eq (Find-ProcessWindow 'Сочетания клавиш') } `
        'shortcut dialog closes through keyboard-only Cancel'
    Wait-State { (Get-FocusedAutomationId) -eq 'ShortcutSettingsButton' } `
        'focus returns to the shortcut settings trigger'
    $settingsDialog = Open-ShortcutSettings
    Select-TextShortcut $settingsDialog
    Assert-Equal (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) 'T' `
        'keyboard Cancel leaves the live Text binding unchanged'
    Add-Action 'traversed with physical Tab, cancelled from the keyboard and restored trigger focus'

    Write-Host "`n[11] Rebind Text to free Alt+T and save isolated settings"
    Send-PhysicalKey 0x54 'ShortcutGestureInput' $settingsDialog @([byte]0x12) # Alt+T
    Wait-State { (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) -eq 'Alt+T' } 'Alt+T captured'
    Assert-True (Require-Uia 'SaveShortcutSettingsButton' $settingsDialog).Current.IsEnabled 'conflict-free save is enabled'
    [void](Focus-WithPhysicalTab 'SaveShortcutSettingsButton' $settingsDialog 'ShortcutGestureInput')
    Invoke-FocusedButtonByKeyboard 'SaveShortcutSettingsButton' $settingsDialog
    Wait-State { Test-Path -LiteralPath $settingsPath } 'isolated settings file exists'
    $settingsJson = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal ([string]$settingsJson.bindings.text) 'Alt+T' 'settings JSON stores Text override'
    $script:ShortcutEvidence['settingsPath'] = $settingsPath
    $script:ShortcutEvidence['rebound'] = [string]$settingsJson.bindings.text
    Add-Action 'saved Text=Alt+T outside .mapproj in isolated JSON'

    Write-Host "`n[12] Prove old T no longer executes and text fields are not intercepted"
    Invoke-Uia 'SelectToolButton'
    Send-PhysicalKey 0x54 # T
    Assert-True (Test-ActiveTool 'Щёлкните объект') 'old T leaves Select active'
    Select-UiaTab 'Объекты'
    Send-PhysicalKey 0x54 'ObjectSearchBox' $script:Root @([byte]0x12) # Alt+T
    Assert-True (Test-ActiveTool 'Щёлкните объект') 'Alt+T does not escape a text input field'
    Add-Action 'proved old binding is inactive and text input focus suppresses global routing'

    Write-Host "`n[13] Prove new Alt+T executes in the real editor"
    Send-PhysicalKey 0x54 'MapCanvas' $script:Root @([byte]0x12) # Alt+T
    Wait-State { Test-ActiveTool 'Добавьте подпись' } 'Alt+T activates Text tool'
    Assert-True (Test-ActiveTool 'Добавьте подпись') 'new Alt+T is the active real command'
    Add-Action 'activated Text through the new binding on canvas focus'

    Write-Host "`n[14] Prove help and tooltip read the same updated profile"
    $textTool = Require-Uia 'TextToolButton'
    Wait-State { (Require-Uia 'TextToolButton').Current.HelpText -like '*Alt+T*' } 'Text tooltip help contains Alt+T'
    Send-PhysicalKey 0x4B # K
    $help = Wait-Window 'Горячие клавиши Map'
    $helpText = Get-WindowText $help
    Assert-True ($helpText -like '*Alt+T*Инструмент «Текст»*') 'shortcut help contains Text Alt+T'
    Save-Shot '05-rebound-help.png' $help
    Close-UiaWindow $help
    $script:ShortcutEvidence['tooltip'] = $textTool.Current.HelpText
    Add-Action 'verified updated Alt+T in both UIA HelpText and generated help dialog'

    Write-Host "`n[15] Restart and prove settings persistence and routing again"
    Stop-TestApp
    Start-TestApp
    Open-SavedMapAndSelectText
    Invoke-Uia 'SelectToolButton'
    Send-PhysicalKey 0x54 # T
    Assert-True (Test-ActiveTool 'Щёлкните объект') 'old T remains inactive after restart'
    Send-PhysicalKey 0x54 'MapCanvas' $script:Root @([byte]0x12) # Alt+T
    Assert-True (Test-ActiveTool 'Добавьте подпись') 'Alt+T remains active after restart'
    Save-Shot '06-persisted-restart.png'
    Add-Action 'restarted exact executable and proved persisted routing'

    Write-Host "`n[16] Reset one command, reset all and confirm compatible defaults"
    $settingsDialog = Open-ShortcutSettings
    Select-TextShortcut $settingsDialog
    [void](Focus-WithPhysicalTab 'ResetShortcutButton' $settingsDialog 'ShortcutGestureInput')
    Invoke-FocusedButtonByKeyboard 'ResetShortcutButton' $settingsDialog
    Assert-Equal (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) 'T' 'single-command reset restores T'
    Send-PhysicalKey 0x54 'ShortcutGestureInput' $settingsDialog @([byte]0x12) # Alt+T
    Assert-Equal (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) 'Alt+T' 'temporary rebind before reset-all'
    [void](Focus-WithPhysicalTab 'ResetAllShortcutsButton' $settingsDialog 'ShortcutGestureInput')
    Invoke-FocusedButtonByKeyboard 'ResetAllShortcutsButton' $settingsDialog
    Assert-Equal (Get-UiaValue 'ShortcutGestureInput' $settingsDialog) 'T' 'reset-all restores T'
    [void](Focus-WithPhysicalTab 'SaveShortcutSettingsButton' $settingsDialog 'ResetAllShortcutsButton')
    Invoke-FocusedButtonByKeyboard 'SaveShortcutSettingsButton' $settingsDialog
    $resetJson = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($null -eq $resetJson.bindings.text) 'default Text binding is absent from overrides'
    Invoke-Uia 'SelectToolButton'
    Send-PhysicalKey 0x54 'MapCanvas' $script:Root @([byte]0x12) # Alt+T
    Assert-True (Test-ActiveTool 'Щёлкните объект') 'Alt+T inactive after reset'
    Send-PhysicalKey 0x54 # T
    Assert-True (Test-ActiveTool 'Добавьте подпись') 'default T active after reset'
    $script:ShortcutEvidence['resetText'] = 'T'
    Add-Action 'verified reset-one, reset-all, compact default JSON and restored T routing'

    Stop-TestApp
    $ownedStillRunning = @($script:OwnedProcesses | Where-Object {
        $null -ne (Get-Process -Id $_.pid -ErrorAction SilentlyContinue)
    }).Count
    Assert-True ($ownedStillRunning -eq 0) 'all and only owned process instances exited' $ownedStillRunning

    $rect = New-Object Stage10FNative+RECT
    $windowEvidence = [ordered]@{
        native = 'Avalonia Win32 window; no WebView'
        input = 'UI Automation controls plus physical Win32 key events only for actual shortcut routing/capture'
        scale = 'captured per owned process before termination in process evidence'
    }
    $report = [ordered]@{
        result = 'PASS'
        executable = [ordered]@{ path=$Exe; sha256=$exeHash }
        processes = $script:OwnedProcesses
        git = [ordered]@{ branch=$branch; head=$head; workspace=$workspace }
        window = $windowEvidence
        actions = $script:Actions
        checks = $script:Checks
        fonts = $script:FontEvidence
        shortcuts = $script:ShortcutEvidence
        paths = [ordered]@{
            mapProject=$savedMap
            isolatedSettings=$settingsPath
            export=$exportPath
            screenshots=$script:Shots
        }
        partial = $script:Partial
        blocked = $script:Blocked
        finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $report | ConvertTo-Json -Depth 10 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host "`nPASS  $($script:Checks.Count) checks"
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
        processes = $script:OwnedProcesses
        actions = $script:Actions
        checks = $script:Checks
        screenshots = $script:Shots
        partial = $script:Partial
        blocked = $script:Blocked
    }
    $report | ConvertTo-Json -Depth 10 | Out-File -Encoding utf8 (Join-Path $OutDir 'report.json')
    Write-Host "FAIL: $($_.Exception.Message)"
    throw
}
finally {
    Stop-TestApp
    Remove-Item Env:MAP_UI_TEST_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_RECOVERY_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_MAP_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:MAP_UI_TEST_SHORTCUT_SETTINGS_PATH -ErrorAction SilentlyContinue
}
