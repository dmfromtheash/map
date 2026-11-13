#requires -Version 5.1
# Run in a fresh Windows PowerShell process for each mapwin capture (its Add-Type is not repeatable).
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][long]$WindowHandle,
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$ShotTool
)
$ErrorActionPreference = 'Stop'
$homeDirectory = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
$fullPath = [IO.Path]::GetFullPath($Path)
if (-not $fullPath.StartsWith($homeDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture output must stay in this script directory.' }
$temporary = Join-Path $PSScriptRoot ('.capture-' + [guid]::NewGuid().ToString('N'))
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $env:TEMP=$temporary; $env:TMP=$temporary
    Add-Type -Path (Join-Path $PSScriptRoot 'LiveBatchNative.cs')
    [LiveBatchNative]::UsePhysicalCoordinates()
    [LiveBatchNative]::ValidateWindow([IntPtr]$WindowHandle, $ProcessId)
    # mapwin.ps1 uses unqualified Get-Process Map.App | Select-Object -First 1.
    # Shadow ONLY that lookup in this child process; the supplied tool remains untouched.
    function Get-Process {
        param([string]$Name)
        if ($Name -cne 'Map.App') { throw 'Unexpected process lookup in mapwin.ps1.' }
        $process = Microsoft.PowerShell.Management\Get-Process -Id $ProcessId -ErrorAction Stop
        if ($process.ProcessName -cne 'Map.App') { throw 'Target is not Map.App.' }
        return $process
    }
    $output = @(& $ShotTool -Action shot -Title $Title -Path $fullPath)
    # mapwin exposes the precise handle lookup after loading its helper type.
    $actual = [MapFindTitled]::Find([uint32]$ProcessId, $Title)
    if ($actual.ToInt64() -ne $WindowHandle) {
        if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Force }
        throw 'Title prefix resolved to a different window; screenshot rejected.'
    }
    if (($output -join ' ') -notmatch '^True \d+x\d+$') { throw "PrintWindow failed: $($output -join ' ')" }
    if ((Get-Item -LiteralPath $fullPath).Length -lt 100) { throw 'Capture file is empty or truncated.' }
    $output
} finally {
    $env:TEMP=$oldTemp; $env:TMP=$oldTmp
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
