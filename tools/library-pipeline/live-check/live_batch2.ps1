#requires -Version 5.1
<#
.SYNOPSIS
Checks one batch in an already running, isolated Map editor. Does not start Map.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\live_batch2.ps1 -SelfTest
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\live_batch2.ps1 -ProcessId 1234 -Batch 1 -Place 10
.NOTES
Catalog is an exact Sets[].Catalog slug filter, not a path or a UI title.
All output is below this script's directory. Input JSON is read explicitly as UTF-8.
Use a disposable map, an attached drive library, single placement and no concurrent edits.
The script clears catalog filters/grouping. Undo restores objects, not preferences or autosaves.
No SendInput, cursor movement, clipboard, foreground activation or GUI launch is used.
Zoom uses the existing canvas wheel path; no canvas zoom button/shortcut exists in the inspected source.
Live delivery is unverified: a missing viewport read-back is a failure, never a successful zoom.
#>
[CmdletBinding()]
param(
    [int]$ProcessId = 0,
    [ValidateRange(0, 2147483647)][int]$Batch = 0,
    [string]$Catalog = '',
    [ValidateRange(0, 2147483647)][int]$Place = 10,
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:HomeDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$script:ChecksDirectory = 'H:\MapLibrary\checks'
$script:ShotTool = 'C:\Projects\Map\tools\ai\live\mapwin.ps1'
$script:Events = New-Object 'System.Collections.Generic.List[object]'

function Get-SetId([string]$Key) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes('set-key:' + $Key.Trim())
        $hex = -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
        return 'set-' + $hex.Substring(0, 32)
    } finally { $sha.Dispose() }
}

# A set name such as "Кровати" exists in every catalog: its cover can be hundreds of tiles down, and
# the gallery's UIA peer does not scroll (13.11 battlemaps-classic 3-4). The set's own detail text
# is unique across the drive library, so the exact cover is on the first screen.
$script:CatalogDetails = @{}
function Get-SearchText($Set) {
    if (-not $script:CatalogDetails.ContainsKey($Set.Catalog)) {
        $map = @{}
        $path = Join-Path (Split-Path $script:ChecksDirectory) (Join-Path $Set.Catalog 'catalog.json')
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Add-Type -AssemblyName System.Web.Extensions
            $json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $json.MaxJsonLength = [int]::MaxValue
            $data = $json.DeserializeObject([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8))
            foreach ($entry in @($data['sets'])) {
                if ($entry -is [Collections.IDictionary] -and $entry['id'] -is [string] -and $entry['detail'] -is [string]) {
                    $map[$entry['id']] = $entry['detail'] }
            }
        }
        $script:CatalogDetails[$Set.Catalog] = $map
    }
    $detail = $script:CatalogDetails[$Set.Catalog][$Set.SetId]
    if ([string]::IsNullOrWhiteSpace($detail)) { return $Set.Name }
    return $detail
}

function Read-Batch([string]$Path, [int]$Number, [string]$Filter = '') {
    $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $data -or -not $data.PSObject.Properties['Batch'] -or
        $data.Batch -isnot [ValueType] -or [double]$data.Batch -ne $Number) { throw 'Batch number mismatch.' }
    if (-not $data.PSObject.Properties['Sets'] -or $data.Sets -isnot [Array] -or $data.Sets.Count -eq 0) {
        throw 'Sets must be a nonempty JSON array.'
    }
    $keys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $result = @()
    foreach ($set in $data.Sets) {
        foreach ($field in @('Key', 'Name', 'Catalog')) {
            if ($null -eq $set -or -not $set.PSObject.Properties[$field] -or
                $set.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($set.$field)) {
                throw "Invalid set field: $field"
            }
        }
        $id = Get-SetId $set.Key
        if (-not $keys.Add($set.Key.Trim()) -or -not $ids.Add($id)) { throw 'Duplicate set key or ID.' }
        if (-not $Filter -or [string]::Equals($Filter, $set.Catalog, [StringComparison]::Ordinal)) {
            $result += [pscustomobject]@{ Key = $set.Key; Name = $set.Name; Catalog = $set.Catalog
                SetId = $id; CoverId = 'GalleryCover_' + $id.Replace('-', '_'); LegacyCoverId = 'GalleryCover_' + $id }
        }
    }
    if ($result.Count -eq 0) { throw "Catalog filter matched zero sets: $Filter" }
    return $result
}

# These pure selectors/parsers are also exercised offline.
function Select-SearchCandidate([object[]]$Candidates) {
    return $Candidates | Where-Object {
        $_.Visible -and $_.Enabled -and $_.HasValue -and -not $_.ReadOnly -and -not $_.Password
    } | Sort-Object @{ Expression = { if ($_.Id -ceq 'AssetSearchBox') { 0 } else { 1 } } },
        @{ Expression = { if ($_.Type -eq 'ControlType.Edit') { 0 } else { 1 } } } | Select-Object -First 1
}
function Convert-ObjectCount([string]$Text) {
    # N0 may use spaces, NBSP, narrow NBSP or commas as grouping separators.
    $match = [regex]::Match($Text, '^\D*(\d(?:[\d\s,\u00a0\u202f]*\d)?)\D*$')
    if (-not $match.Success) { throw "Cannot parse object count: $Text" }
    return [long]([regex]::Replace($match.Groups[1].Value, '\D', ''))
}
function Convert-ZoomPercent([string]$Text) {
    $match = [regex]::Match($Text, '(\d+(?:[.,]\d+)?)\s*%')
    if (-not $match.Success) { throw "Cannot parse viewport zoom: $Text" }
    $number = [double]::Parse($match.Groups[1].Value.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
    if ($number -le 0) { throw 'Viewport zoom must be positive.' }
    return $number
}
function Get-ZoomDelta([double]$Current, [double]$Target) {
    if ($Current -le 0 -or $Target -le 0) { throw 'Invalid zoom percentage.' }
    $delta = [int][math]::Round(120 * [math]::Log($Target / $Current) / [math]::Log(1.15))
    return [int][math]::Max(-1200, [math]::Min(1200, $delta))
}
function Write-Event([string]$Action, $Details) {
    $script:Events.Add([pscustomobject]@{ Utc = [DateTime]::UtcNow.ToString('o'); Action = $Action; Details = $Details })
}
function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Add-LocalType([string]$Path) {
    # Windows PowerShell's C# compiler must also keep its temporary files here.
    $temporary = Join-Path $script:HomeDirectory ('.compile-' + [guid]::NewGuid().ToString('N'))
    $oldTemp = $env:TEMP; $oldTmp = $env:TMP
    try {
        New-Item -ItemType Directory -Path $temporary | Out-Null
        $env:TEMP = $temporary; $env:TMP = $temporary
        Add-Type -Path $Path
    } finally {
        $env:TEMP = $oldTemp; $env:TMP = $oldTmp
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
}

function Invoke-SelfTest {
    $script:Assertions = 0
    function Assert($Condition, [string]$Label) {
        if (-not $Condition) { throw "SelfTest failed: $Label" }
        $script:Assertions++
    }
    function Expect-Rejection([scriptblock]$Action, [string]$Label) {
        $rejected = $false
        try { & $Action | Out-Null } catch { $rejected = $true }
        Assert $rejected $Label
    }
    Add-LocalType (Join-Path $script:HomeDirectory 'LiveBatchNative.cs')
    Assert ((Get-SetId 'abc') -ceq 'set-85265af995dd2b29d0eedf046725942f') 'ASCII SHA256 vector'
    Assert ((Get-SetId ' abc ') -ceq (Get-SetId 'abc')) 'Trim matches AssetIdentity'
    $unicode = -join ([char[]](0x433, 0x43e, 0x440, 0x44b))
    Assert ((Get-SetId $unicode) -ceq 'set-7a501a41bc63b49717607e3aca97a320') 'UTF8 SHA256 vector'
    Assert ((Get-SetId 'battlemaps-classic|OBJ:Angular Entrances') -ceq 'set-ebe2896720b3ed25bbdbfa9cd7ee25c2') 'real key vector'
    Assert ((Convert-ObjectCount ('Objects 12' + [char]0x202f + '345')) -eq 12345) 'N0 grouping'
    Assert ((Convert-ObjectCount 'Objects 1,234') -eq 1234) 'comma grouping'
    Assert ((Convert-ObjectCount 'Objects 0') -eq 0) 'zero count'
    Expect-Rejection { Convert-ObjectCount 'unavailable' } 'missing count'
    Assert ((Convert-ZoomPercent 'Zoom 400 %') -eq 400) 'zoom percent'
    Assert ((Convert-ZoomPercent 'Zoom 12,5%') -eq 12.5) 'decimal percent'
    Expect-Rejection { Convert-ZoomPercent 'Zoom 0%' } 'zero zoom'
    $delta = Get-ZoomDelta 100 400
    Assert ([math]::Abs(100 * [math]::Pow(1.15, $delta / 120.0) - 400) -lt 1) '400 percent calculation'
    Assert ((Get-ZoomDelta 400 100) -eq -$delta) 'inverse wheel calculation'
    Assert (([LiveBatchNative]::Pack(-120, -300).ToInt64() -band 65535) -eq 65416) 'signed X packing'
    Assert (([LiveBatchNative]::Pack(-120, -300).ToInt64() -shr 16) -eq -300) 'signed Y packing'
    Assert (([LiveBatchNative]::WheelParam(-120).ToInt64() -shr 16) -eq -120) 'negative wheel packing'
    $rows = @(
        [pscustomobject]@{ Id='AssetSearchBox'; Type='ControlType.Text'; Visible=$true; Enabled=$true; HasValue=$false; ReadOnly=$false; Password=$false },
        [pscustomobject]@{ Id='AssetSearchBox'; Type='ControlType.Edit'; Visible=$false; Enabled=$true; HasValue=$true; ReadOnly=$false; Password=$false },
        [pscustomobject]@{ Id='AssetSearchBox'; Type='ControlType.Edit'; Visible=$true; Enabled=$true; HasValue=$true; ReadOnly=$true; Password=$false },
        [pscustomobject]@{ Id='PART_Input'; Type='ControlType.Edit'; Visible=$true; Enabled=$true; HasValue=$true; ReadOnly=$false; Password=$false }
    )
    Assert ((Select-SearchCandidate $rows).Id -ceq 'PART_Input') 'editable descendant beats duplicate IDs'
    Assert ($null -eq (Select-SearchCandidate $rows[0..2])) 'no writable visible ValuePattern'
    $exact = [pscustomobject]@{ Id='AssetSearchBox'; Type='ControlType.Edit'; Visible=$true; Enabled=$true; HasValue=$true; ReadOnly=$false; Password=$false }
    Assert ((Select-SearchCandidate (@($rows[3]) + @($exact))).Id -ceq 'AssetSearchBox') 'exact editable ID preferred'
    $exact.Password = $true
    Assert ($null -eq (Select-SearchCandidate @($exact))) 'password control rejected'
    # Exercise the real catalog-opening loop against a delayed window model; no UIA is loaded.
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$tokens, [ref]$parseErrors)
    Assert (@($parseErrors).Count -eq 0) 'Windows PowerShell syntax'
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Open-Catalog'
    }, $true)
    Invoke-Expression $definition.Extent.Text
    $script:MockCatalogVisible = $true; $script:MockCatalogActions = 0; $script:MockCatalogCanOpen = $true
    function Get-CatalogWindow { if ($script:MockCatalogVisible) { return [pscustomobject]@{ Visible=$true } }; return $null }
    function Get-EditorWindow { return 'offline-editor' }
    function Find-One($Root, $Id) { return $Id }
    function Invoke-Element($Element) { $script:MockCatalogActions++ }
    function Wait-Catalog($Open) {
        if ($script:MockCatalogCanOpen -and $script:MockCatalogActions -ge 2) { $script:MockCatalogVisible=$true }
        return $script:MockCatalogVisible
    }
    Open-Catalog
    Assert ($script:MockCatalogActions -eq 0) 'already open catalog is not toggled'
    $script:MockCatalogVisible = $false
    Open-Catalog
    Assert ($script:MockCatalogActions -eq 2 -and $script:MockCatalogVisible) 'catalog retries until window is visible'
    Open-Catalog
    Assert ($script:MockCatalogActions -eq 2) 'visible catalog stays open'
    $script:MockCatalogVisible=$false; $script:MockCatalogActions=0; $script:MockCatalogCanOpen=$false
    Expect-Rejection { Open-Catalog } 'catalog failure is reported'
    Assert ($script:MockCatalogActions -eq 3) 'catalog retries are bounded'
    $temporary = Join-Path $script:HomeDirectory ('.selftest-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $good = '{"Batch":7,"Sets":[{"Key":"abc","Name":"\u0433\u043e\u0440\u044b","Catalog":"test"}]}'
        [IO.File]::WriteAllText($temporary, $good, (New-Object Text.UTF8Encoding($false)))
        $sets = @(Read-Batch $temporary 7 'test')
        Assert ($sets.Count -eq 1 -and $sets[0].Name -ceq $unicode) 'BOMless UTF8 JSON and Cyrillic'
        Assert ($sets[0].CoverId -ceq 'GalleryCover_set_85265af995dd2b29d0eedf046725942f') 'sanitized cover automation ID'
        Assert ($sets[0].LegacyCoverId -ceq 'GalleryCover_set-85265af995dd2b29d0eedf046725942f') 'legacy cover automation ID'
        $literalUtf8 = $good.Replace('\u0433\u043e\u0440\u044b', $unicode)
        [IO.File]::WriteAllText($temporary, $literalUtf8, (New-Object Text.UTF8Encoding($false)))
        Assert ((@(Read-Batch $temporary 7))[0].Name -ceq $unicode) 'literal UTF8 bytes without BOM'
        Expect-Rejection { Read-Batch $temporary 8 } 'wrong batch'
        Expect-Rejection { Read-Batch $temporary 7 'other' } 'empty catalog selection'
        foreach ($invalid in @(
            '{', '{"Batch":7,"Sets":[]}', '{"Batch":7,"Sets":{"Key":"abc","Name":"a","Catalog":"test"}}',
            '{"Batch":7,"Sets":[{"Key":" ","Name":"a","Catalog":"test"}]}',
            '{"Batch":7,"Sets":[{"Key":"abc","Name":"a","Catalog":"test"},{"Key":" abc ","Name":"b","Catalog":"test"}]}'
        )) {
            [IO.File]::WriteAllText($temporary, $invalid)
            Expect-Rejection { Read-Batch $temporary 7 } 'invalid JSON/schema/duplicate'
        }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    $files = @(Get-ChildItem -LiteralPath $script:ChecksDirectory -Filter 'batch-*.json' -File | Sort-Object Name)
    if ($Batch -gt 0) { $files = @($files | Where-Object { $_.BaseName -ceq ('batch-{0:D4}' -f $Batch) }) }
    Assert ($files.Count -gt 0) 'real batch reports exist'
    $reports = @()
    foreach ($file in $files) {
        if ($file.BaseName -notmatch '^batch-(\d+)$') { continue }
        $number = [int]$Matches[1]
        $sets = @(Read-Batch $file.FullName $number $Catalog)
        foreach ($set in $sets) { Assert ($set.CoverId -cmatch '^GalleryCover_set_[0-9a-f]{32}$') 'real cover ID format' }
        $reports += [pscustomobject]@{ Batch=$number; Sets=$sets.Count; PlannedPlacements=[math]::Min($Place, $sets.Count) }
    }
    $result = [ordered]@{ Mode='SelfTest'; Passed=$true; Assertions=$script:Assertions; Reports=$reports
        NativeCodeCompiled=$true; UiaAccessed=$false; MapStarted=$false; LiveVerified=$false }
    Write-Json (Join-Path $script:HomeDirectory 'selftest-summary.json') $result
    $result | ConvertTo-Json -Depth 5
}

if ($SelfTest) { Invoke-SelfTest; return }
if ($ProcessId -le 0 -or $Batch -le 0) { throw 'Live mode requires -ProcessId <pid> -Batch <positive number>.' }
$inputPath = Join-Path $script:ChecksDirectory ('batch-{0:D4}.json' -f $Batch)
# Since the per-catalog builder, reports live in checks\<catalog>\batch-NNNN.json.
if ($Catalog) {
    $perCatalog = Join-Path (Join-Path $script:ChecksDirectory $Catalog) ('batch-{0:D4}.json' -f $Batch)
    if (Test-Path -LiteralPath $perCatalog) { $inputPath = $perCatalog }
}
$sets = @(Read-Batch $inputPath $Batch $Catalog)
$inputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
$runDirectory = Join-Path $script:HomeDirectory ('live\batch-{0:D4}-{1}-{2}' -f $Batch,
    [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$summary = [ordered]@{ Mode='Live'; Passed=$false; ProcessId=$ProcessId; Batch=$Batch; Catalog=$Catalog
    Input=$inputPath; InputSha256=$inputHash; StartedUtc=[DateTime]::UtcNow.ToString('o'); FinishedUtc=$null
    Sets=$sets.Count; Found=0; RequestedPlacements=[math]::Min($Place, $sets.Count); Placed=0; Undone=0
    ObjectsBefore=$null; ObjectsAfter=$null; ObjectsRestored=$false; ZoomBefore=$null; Zoom400=$null
    ZoomRestored=$null; ZoomWheelDelta=0; ZoomMethod='WM_MOUSEWHEEL / UIA canvas coordinates / status read-back'
    Screenshots=@(); Results=@(); Errors=@(); CleanupErrors=@(); Events=@(); OutputDirectory=$runDirectory }
$script:Placements = New-Object 'System.Collections.Generic.List[object]'
$script:PendingClick = $null
$script:ZoomNetDelta = 0
$script:ZoomAnchor = $null
$script:EditorHandle = [IntPtr]::Zero
$script:UiReady = $false

function Get-WindowRoots {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}
function Test-Visible($Element) {
    $c = $Element.Current; $r = $c.BoundingRectangle
    return -not $c.IsOffscreen -and -not $r.IsEmpty -and $r.Width -gt 0 -and $r.Height -gt 0
}
function Find-Elements($Root, [string]$Id) {
    # A window that is briefly hidden (13.11 batch 4) is "not found", never a null-method crash.
    if ($null -eq $Root) { return @() }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Get-CatalogWindow {
    # The catalog is an owned window: UI Automation shows it under the editor window, not as a root.
    $candidates = @(Get-WindowRoots) + @(Find-Elements (Get-EditorWindow) 'AssetCatalogWindow')
    foreach ($window in $candidates) {
        if ($window.Current.AutomationId -ceq 'AssetCatalogWindow' -and (Test-Visible $window) -and
            [LiveBatchNative]::IsWindowVisible([IntPtr]$window.Current.NativeWindowHandle)) { return $window }
    }
    return $null
}
function Get-EditorWindow {
    return [System.Windows.Automation.AutomationElement]::FromHandle($script:EditorHandle)
}
function Find-One($Root, [string]$Id, [switch]$AllowHidden) {
    foreach ($element in (Find-Elements $Root $Id)) {
        if ($element.Current.IsEnabled -and ($AllowHidden -or (Test-Visible $element))) { return $element }
    }
    return $null
}
function Invoke-Element($Element) {
    if ($null -eq $Element -or -not $Element.Current.IsEnabled) { throw 'UI action target is missing or disabled.' }
    $p = $null
    $id = $Element.Current.AutomationId
    foreach ($name in @('Invoke', 'SelectionItem', 'Toggle', 'ExpandCollapse')) {
        $pattern = switch ($name) {
            'Invoke' { [System.Windows.Automation.InvokePattern]::Pattern }
            'SelectionItem' { [System.Windows.Automation.SelectionItemPattern]::Pattern }
            'Toggle' { [System.Windows.Automation.TogglePattern]::Pattern }
            'ExpandCollapse' { [System.Windows.Automation.ExpandCollapsePattern]::Pattern }
        }
        if ($Element.TryGetCurrentPattern($pattern, [ref]$p)) {
            switch ($name) { 'Invoke' { $p.Invoke() }; 'SelectionItem' { $p.Select() }; 'Toggle' { $p.Toggle() }; 'ExpandCollapse' { $p.Expand() } }
            Write-Event 'UiAction' @{ Id=$id; Pattern=$name }
            return
        }
    }
    throw "No action pattern: $id"
}
function Wait-Catalog([bool]$Open, [int]$Milliseconds = 4000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $window = Get-CatalogWindow
        if (($null -ne $window) -eq $Open) { return $true }
        Start-Sleep -Milliseconds 200
    } while ($watch.ElapsedMilliseconds -lt $Milliseconds)
    return $false
}
function Open-Catalog {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if ($null -ne (Get-CatalogWindow)) { return }
        Invoke-Element (Find-One (Get-EditorWindow) 'AssetsLibraryButton')
        if (Wait-Catalog $true) { return }
    }
    throw 'Asset catalog did not become visible after three checked attempts.'
}
function Close-Catalog {
    $window = Get-CatalogWindow
    if ($null -eq $window) { return }
    $p = $null
    if (-not $window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) {
        throw 'AssetCatalogWindow has no WindowPattern.'
    }
    $p.Close()
    if (-not (Wait-Catalog $false)) { throw 'Catalog close/hide was not confirmed.' }
}
function Set-CatalogSearch([AllowEmptyString()][string]$Text) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            Open-Catalog
            $candidates = @(Get-SearchCandidates)
            Write-Event 'SearchCandidates' @($candidates | Select-Object Id,Type,Visible,Enabled,HasValue,ReadOnly,Password)
            $chosen = Select-SearchCandidate $candidates
            if ($null -eq $chosen) { throw 'No visible writable ValuePattern inside the catalog search control.' }
            $chosen.Pattern.SetValue($Text)
            # Requery after every mutation; do not keep peers across catalog hide/show or search rebuilds.
            Start-Sleep -Milliseconds 250
            $fresh = Select-SearchCandidate @(Get-SearchCandidates)
            if ($null -eq $fresh) { throw 'Search peer disappeared during read-back.' }
            $value = $fresh.Pattern.Current.Value
            if ($value -cne $Text) { throw 'Search ValuePattern read-back mismatch.' }
            Write-Event 'SearchSet' @{ Text=$Text; Id=$chosen.Id; Type=$chosen.Type; ReadBack=$value }
            Start-Sleep -Milliseconds 500
            return
        } catch {
            Write-Event 'SearchRetry' $_.Exception.Message
            if ($attempt -eq 2) { throw }
            Start-Sleep -Milliseconds 350
        }
    }
}
function Get-SearchCandidates {
    foreach ($hit in (Find-Elements (Get-CatalogWindow) 'AssetSearchBox')) {
        $elements = @($hit) + @($hit.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition))
        foreach ($element in $elements) {
            $p = $null; $c = $element.Current
            $hasValue = $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)
            [pscustomobject]@{ Element=$element; Pattern=$p; Id=$c.AutomationId
                Type=$c.ControlType.ProgrammaticName; Visible=(Test-Visible $element); Enabled=$c.IsEnabled
                HasValue=$hasValue; ReadOnly=($hasValue -and $p.Current.IsReadOnly); Password=$c.IsPassword }
        }
    }
}
function Invoke-CatalogMenu([string]$Button, [string]$Item) {
    Invoke-Element (Find-One (Get-CatalogWindow) $Button)
    Start-Sleep -Milliseconds 200
    # Popup peers may be separate process-owned top-level windows.
    foreach ($root in (Get-WindowRoots)) {
        $element = Find-One $root $Item
        if ($null -ne $element) { Invoke-Element $element; Start-Sleep -Milliseconds 250; return }
    }
    throw "Catalog menu item not found: $Item"
}
function Get-GalleryScroll {
    $grid = Find-One (Get-CatalogWindow) 'LibraryGrid'
    if ($null -eq $grid) { throw 'LibraryGrid missing from the catalog.' }
    $p = $null
    $elements = @($grid) + @($grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
    foreach ($element in $elements) {
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$p)) { return $p }
    }
    return $null
}
function Find-Cover([string]$Id) {
    $ids = @($Id, ($Id -creplace '^GalleryCover_set_', 'GalleryCover_set-')) | Select-Object -Unique
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($candidateId in $ids) {
            $element = Find-One (Get-CatalogWindow) $candidateId
            if ($null -ne $element) { return $element }
        }
        Start-Sleep -Milliseconds 250
    } while ($watch.ElapsedMilliseconds -lt 5000)
    $scroll = Get-GalleryScroll
    if ($null -eq $scroll -or -not $scroll.Current.VerticallyScrollable) { return $null }
    $scroll.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 350
    for ($page = 0; $page -lt 100; $page++) {
        foreach ($candidateId in $ids) {
            $element = Find-One (Get-CatalogWindow) $candidateId
            if ($null -ne $element) { return $element }
        }
        $scroll = Get-GalleryScroll
        if ($null -eq $scroll -or $scroll.Current.VerticalScrollPercent -ge 99.9) { break }
        $before = $scroll.Current.VerticalScrollPercent
        $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 300
        if ([math]::Abs($scroll.Current.VerticalScrollPercent - $before) -lt 0.001) { break }
    }
    foreach ($candidateId in $ids) {
        $element = Find-One (Get-CatalogWindow) $candidateId
        if ($null -ne $element) { return $element }
    }
    return $null
}
function Read-Status([string]$Id) {
    $element = Find-One (Get-EditorWindow) $Id -AllowHidden
    if ($null -eq $element) { throw "Status element missing: $Id" }
    return $element.Current.Name
}
function Read-Count { return Convert-ObjectCount (Read-Status 'MapObjectCountText') }
function Read-Zoom { return Convert-ZoomPercent (Read-Status 'ViewportStatusText') }
function Wait-Count([long]$Before, [bool]$Change, [int]$Milliseconds = 5000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $count = Read-Count
        if (($count -ne $Before) -eq $Change) { return $count }
        Start-Sleep -Milliseconds 200
    } while ($watch.ElapsedMilliseconds -lt $Milliseconds)
    throw "Object count read-back timed out (before=$Before, current=$count, change=$Change)."
}
function Get-CanvasPoint([int]$Index, [int]$Total) {
    $canvas = Find-One (Get-EditorWindow) 'MapCanvas'
    if ($null -eq $canvas) { throw 'Visible MapCanvas missing.' }
    $r = $canvas.Current.BoundingRectangle
    $dpiScale = [LiveBatchNative]::Dpi($script:EditorHandle) / 96.0
    $left = $r.Left + [math]::Max(120 * $dpiScale, $r.Width * 0.15)
    $top = $r.Top + 40 * $dpiScale
    $width = $r.Right - 30 * $dpiScale - $left
    $height = $r.Bottom - 40 * $dpiScale - $top
    if ($width -lt 100 * $dpiScale -or $height -lt 100 * $dpiScale) { throw 'Canvas safe placement area is too small.' }
    if ($Index -eq 0) { $x = $left + $width / 2; $y = $top + $height / 2 }
    else {
        $columns = [int][math]::Ceiling([math]::Sqrt($Total))
        $rows = [int][math]::Ceiling($Total / [double]$columns)
        $x = $left + $width * (($Index % $columns) + 0.5) / $columns
        $y = $top + $height * ([math]::Floor($Index / $columns) + 0.5) / $rows
    }
    return [pscustomobject]@{ X=[int][math]::Round($x); Y=[int][math]::Round($y) }
}
function Save-Capture([string]$Kind, $Window) {
    $path = Join-Path $runDirectory ($Kind + '.png')
    $title = $Window.Current.Name
    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:HomeDirectory 'shot_target.ps1') `
        -ProcessId $ProcessId -WindowHandle $handle.ToInt64() -Title $title -Path $path -ShotTool $script:ShotTool 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $path)) { throw "Capture failed: $Kind / $($output -join ' ')" }
    $summary.Screenshots += [pscustomobject]@{ Kind=$Kind; Path=$path; Bytes=(Get-Item -LiteralPath $path).Length
        WindowHandle=$handle.ToInt64(); Title=$title; ToolOutput=($output -join ' ')
        PrintWindowSucceeded=$true; VisualContentVerified=$false }
}
function Send-Wheel([int]$Delta, $Point) {
    [LiveBatchNative]::Wheel($script:EditorHandle, $Point.X, $Point.Y, $Delta)
    $script:ZoomNetDelta += $Delta
    Write-Event 'WheelSent' @{ Delta=$Delta; Net=$script:ZoomNetDelta; Point=$Point }
}
function Zoom-To400 {
    $summary.ZoomBefore = Read-Zoom
    $script:ZoomAnchor = $script:Placements[0].Point
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $current = Read-Zoom
        if ([math]::Abs($current - 400) -le 4) { $summary.Zoom400 = $current; return }
        $delta = Get-ZoomDelta $current 400
        if ($delta -eq 0) { throw 'Zoom correction rounded to zero.' }
        Send-Wheel $delta $script:ZoomAnchor
        $watch = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 200
            $after = Read-Zoom
        } while ($after -eq $current -and $watch.ElapsedMilliseconds -lt 3000)
        if ($after -eq $current) { throw 'WM_MOUSEWHEEL did not change ViewportStatusText; zoom delivery is unverified.' }
        Write-Event 'ZoomReadBack' @{ Before=$current; After=$after }
    }
    throw 'Viewport did not reach 400 percent within tolerance (4 percentage points).'
}

try {
    $process = Microsoft.PowerShell.Management\Get-Process -Id $ProcessId
    if ($process.ProcessName -cne 'Map.App') { throw 'The selected process is not Map.App.' }
    if (-not (Test-Path -LiteralPath $script:ShotTool -PathType Leaf)) { throw 'mapwin.ps1 is unavailable.' }
    Add-LocalType (Join-Path $script:HomeDirectory 'LiveBatchNative.cs')
    [LiveBatchNative]::UsePhysicalCoordinates()
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    foreach ($window in (Get-WindowRoots)) {
        if ((Test-Visible $window) -and $null -ne (Find-One $window 'MapCanvas')) {
            if ($script:EditorHandle -ne [IntPtr]::Zero) { throw 'Multiple editors in the selected process; close extra editors.' }
            $script:EditorHandle = [IntPtr]$window.Current.NativeWindowHandle
        }
    }
    if ($script:EditorHandle -eq [IntPtr]::Zero) { throw 'An already open, visible editor is required.' }
    [LiveBatchNative]::ValidateWindow($script:EditorHandle, $ProcessId)
    $script:UiReady = $true
    $summary.ObjectsBefore = Read-Count
    Open-Catalog
    $clear = Find-One (Get-CatalogWindow) 'LibraryClearFiltersButton'
    if ($null -ne $clear) { Invoke-Element $clear; Start-Sleep -Milliseconds 350 }
    Invoke-Element (Find-One (Get-CatalogWindow) 'Collection_All')
    # Menu items of the catalog expose no UIA action pattern in Avalonia: grouping/view stay as they are
    # (each set is found by its own search, so neither is needed for the check).
    try { Invoke-CatalogMenu 'LibraryGroupMenu' 'LibraryGroupNone' } catch { Write-Event 'Note' "grouping left as is: $($_.Exception.Message)" }
    try { Invoke-CatalogMenu 'LibraryViewMenu' 'LibraryCollapseVariantsItem' } catch { Write-Event 'Note' "view left as is: $($_.Exception.Message)" }
    foreach ($set in $sets) {
        $item = [pscustomobject]@{ Key=$set.Key; Name=$set.Name; SetId=$set.SetId; CoverId=$set.CoverId
            Found=$false; Placed=$false; Before=$null; After=$null; Error=$null }
        $summary.Results += $item
        # One set's search failure is that set's result; it must not end the rest of the hundred.
        $cover = $null
        try {
            if ($null -eq (Get-CatalogWindow)) { Open-Catalog }
            $search = Get-SearchText $set
            Write-Event 'SearchText' @{ SetId=$set.SetId; ByDetail=($search -cne $set.Name) }
            Set-CatalogSearch $search
            $cover = Find-Cover $set.CoverId
        } catch { $item.Error = "Search failed: $($_.Exception.Message)"; continue }
        if ($null -eq $cover) { $item.Error='Exact cover ID not found after search/scroll.'; continue }
        $item.Found = $true; $summary.Found++
        Write-Event 'CoverFound' @{ Id=$cover.Current.AutomationId; Name=$cover.Current.Name }
        if ($summary.Placed -ge $summary.RequestedPlacements) { continue }
        Invoke-Element $cover
        # Organisation mode can keep the catalog open. Closing it arms the chosen stamp in production.
        Close-Catalog
        $one = Find-One (Get-EditorWindow) 'StampModeOne' -AllowHidden
        if ($null -ne $one) { Invoke-Element $one; Start-Sleep -Milliseconds 150 }
        $before = Read-Count
        if ($before -ne ($summary.ObjectsBefore + $summary.Placed)) { throw 'Unexpected object count before placement; concurrent edits or non-single mode.' }
        $point = Get-CanvasPoint $summary.Placed $summary.RequestedPlacements
        $script:PendingClick = [pscustomobject]@{ Before=$before; After=$null; Point=$point; SetId=$set.SetId }
        [LiveBatchNative]::ValidateWindow($script:EditorHandle, $ProcessId)
        [LiveBatchNative]::Click($script:EditorHandle, $point.X, $point.Y)
        $after = Wait-Count $before $true
        $script:PendingClick.After = $after
        $script:Placements.Add($script:PendingClick); $script:PendingClick = $null
        $summary.Placed++; $item.Placed=$true; $item.Before=$before; $item.After=$after
        if ($after -ne ($before + 1)) { throw "Expected one object from one placement; count changed from $before to $after." }
        Start-Sleep -Milliseconds 250
    }
    Open-Catalog
    # Keep a batch-specific search in the evidence, rather than an unrelated unfiltered library.
    Set-CatalogSearch (Get-SearchText $sets[0])
    if ($null -eq (Find-Cover $sets[0].CoverId)) { throw 'First batch cover missing before catalog capture.' }
    Save-Capture 'catalog' (Get-CatalogWindow)
    Close-Catalog
    Invoke-Element (Find-One (Get-EditorWindow) 'SelectToolButton')
    Start-Sleep -Milliseconds 700
    Save-Capture 'canvas' (Get-EditorWindow)
    if ($summary.Placed -gt 0) {
        Zoom-To400
        Start-Sleep -Milliseconds 1000
        Save-Capture 'canvas-400' (Get-EditorWindow)
    }
} catch {
    $summary.Errors += $_.Exception.Message
    Write-Event 'Failure' $_.Exception.Message
} finally {
    if ($script:UiReady) {
        try { Close-Catalog } catch { $summary.CleanupErrors += $_.Exception.Message }
        if ($script:ZoomNetDelta -ne 0) {
            try {
                $inverse = -$script:ZoomNetDelta
                Send-Wheel $inverse $script:ZoomAnchor
                Start-Sleep -Milliseconds 600
                $summary.ZoomRestored = Read-Zoom
                if ([math]::Abs($summary.ZoomRestored - $summary.ZoomBefore) -gt 1) { throw 'Zoom restoration read-back mismatch.' }
            } catch { $summary.CleanupErrors += $_.Exception.Message }
        } elseif ($null -ne $summary.ZoomBefore) {
            try { $summary.ZoomRestored = Read-Zoom } catch { $summary.CleanupErrors += $_.Exception.Message }
        }
        try {
            # A click followed by a read failure is uncertain: inspect once before any Undo.
            if ($null -ne $script:PendingClick) {
                Start-Sleep -Milliseconds 600
                $observed = Read-Count
                if ($observed -gt $script:PendingClick.Before) {
                    $script:PendingClick.After=$observed; $script:Placements.Add($script:PendingClick)
                    Write-Event 'RecoveredPendingClick' $script:PendingClick
                } elseif ($observed -ne $script:PendingClick.Before) { throw 'Uncertain placement count; automatic Undo stopped.' }
            }
            Invoke-Element (Find-One (Get-EditorWindow) 'SelectToolButton')
            for ($index = $script:Placements.Count - 1; $index -ge 0; $index--) {
                $placement = $script:Placements[$index]
                if ((Read-Count) -ne $placement.After) { throw 'Object count changed before Undo; automatic Undo stopped.' }
                Invoke-Element (Find-One (Get-EditorWindow) 'UndoButton')
                $count = Wait-Count $placement.After $true
                if ($count -ne $placement.Before) { throw 'Undo did not restore the preceding count; automatic Undo stopped.' }
                $summary.Undone++
            }
            $summary.ObjectsAfter = Read-Count
            $summary.ObjectsRestored = $summary.ObjectsAfter -eq $summary.ObjectsBefore
        } catch { $summary.CleanupErrors += $_.Exception.Message }
    }
    $summary.ZoomWheelDelta = $script:ZoomNetDelta
    $zoomOk = $summary.RequestedPlacements -eq 0 -or ($null -ne $summary.Zoom400 -and
        $null -ne $summary.ZoomRestored -and [math]::Abs($summary.ZoomRestored - $summary.ZoomBefore) -le 1)
    $summary.Passed = $summary.Errors.Count -eq 0 -and $summary.CleanupErrors.Count -eq 0 -and
        $summary.Found -eq $summary.Sets -and $summary.Placed -eq $summary.RequestedPlacements -and
        $summary.Undone -eq $summary.Placed -and $summary.ObjectsRestored -and $zoomOk
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary.Events = @($script:Events.ToArray())
    Write-Json (Join-Path $runDirectory 'summary.json') $summary
}
$summary | ConvertTo-Json -Depth 12
if (-not $summary.Passed) { exit 1 }
