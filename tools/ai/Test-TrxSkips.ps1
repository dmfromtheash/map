# DEC-116: every skipped TRX result must carry a message starting exactly with defect:.
# No source baseline exceptions apply to runtime results. Missing/empty/malformed input fails.
# Exit 0 = clean, 1 = bad skips, 2 = invalid input/tool failure. ASCII / PowerShell 5.1.
param(
    [Parameter(Position = 0)] [string[]]$Path = @(),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
try {
    if (-not ('MapTestGuards.Guard' -as [type])) {
        $compile = @{ Path = (Join-Path $PSScriptRoot 'Test-GuardSupport.cs') }
        if ($PSVersionTable.PSEdition -ne 'Core') { $compile.ReferencedAssemblies = @('System.dll', 'System.Core.dll', 'System.Xml.dll') }
        Add-Type @compile
    }
    if ($SelfTest) {
        [MapTestGuards.Guard]::SelfTest((Join-Path $PSScriptRoot 'test-guard-cases.xml'))
        exit 0
    }
    if ($Path.Count -eq 0) { throw 'Supply -Path with one or more .trx files or result directories' }
    $files = @{}
    foreach ($item in $Path) {
        if (-not (Test-Path -LiteralPath $item)) { throw "Missing TRX path: $item" }
        $entry = Get-Item -LiteralPath $item
        if ($entry.PSIsContainer) {
            $found = @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Filter *.trx -File)
            if ($found.Count -eq 0) { throw "No TRX files in: $item" }
            foreach ($file in $found) { $files[$file.FullName] = $true }
        }
        else {
            if ($entry.Extension -ine '.trx') { throw "Not a TRX file: $item" }
            $files[$entry.FullName] = $true
        }
    }
    $count = 0
    foreach ($file in ($files.Keys | Sort-Object)) {
        $findings = @([MapTestGuards.Guard]::ScanTrx([IO.File]::ReadAllText($file), $file))
        foreach ($finding in $findings) { "FAIL $finding" }
        $count += $findings.Count
    }
    if ($count -gt 0) { "Test-TrxSkips: FAIL ($count skips without defect reasons)"; exit 1 }
    "Test-TrxSkips: OK ($($files.Count) reports)"
    exit 0
}
catch { Write-Error -ErrorAction Continue "Test-TrxSkips: ERROR: $_"; exit 2 }
