[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Reference,

    [Parameter(Mandatory)]
    [string]$Actual,

    [string]$Difference = 'null:',

    [ValidateRange(0.0, 1.0)]
    [double]$MaximumNormalizedRmse = 0.0
)

$ErrorActionPreference = 'Stop'

function Resolve-ImagePath {
    param([string]$Path, [string]$Label)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
        throw "$Label is not a file: $Path"
    }

    return $resolved.Path
}

$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) {
    $aliasPath = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\magick.exe'
    if (Test-Path -LiteralPath $aliasPath -PathType Leaf) {
        $magick = Get-Item -LiteralPath $aliasPath
    }
}

if (-not $magick) {
    throw 'ImageMagick is not installed or magick.exe is not available.'
}

$referencePath = Resolve-ImagePath -Path $Reference -Label 'Reference'
$actualPath = Resolve-ImagePath -Path $Actual -Label 'Actual'

$referenceSize = (& $magick.Source identify -format '%w x %h' $referencePath 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "ImageMagick could not identify reference: $referenceSize" }

$actualSize = (& $magick.Source identify -format '%w x %h' $actualPath 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "ImageMagick could not identify actual: $actualSize" }

if ($referenceSize -ne $actualSize) {
    throw "Image dimensions differ: reference=$referenceSize, actual=$actualSize"
}

$differenceTarget = $Difference
if ($Difference -ne 'null:') {
    $differenceTarget = [System.IO.Path]::GetFullPath($Difference)
    $differenceDirectory = Split-Path -Parent $differenceTarget
    if ($differenceDirectory) {
        New-Item -ItemType Directory -Path $differenceDirectory -Force | Out-Null
    }
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    # ImageMagick intentionally writes the metric to stderr, including on an exact match.
    $metricOutput = (& $magick.Source compare -metric RMSE $referencePath $actualPath $differenceTarget 2>&1 |
            ForEach-Object { $_.ToString() } |
            Out-String).Trim()
    $compareExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($compareExit -gt 1) {
    throw "ImageMagick compare failed with exit $compareExit`: $metricOutput"
}

$match = [regex]::Match($metricOutput, '\((?<normalized>[-+0-9.eE]+)\)')
if (-not $match.Success) {
    throw "Could not parse normalized RMSE from ImageMagick output: $metricOutput"
}

$normalizedRmse = [double]::Parse(
    $match.Groups['normalized'].Value,
    [System.Globalization.CultureInfo]::InvariantCulture
)
$accepted = $normalizedRmse -le $MaximumNormalizedRmse

[pscustomobject]@{
    Reference = $referencePath
    Actual = $actualPath
    Dimensions = $referenceSize
    Metric = 'RMSE'
    RawMetric = $metricOutput
    NormalizedRmse = $normalizedRmse
    MaximumNormalizedRmse = $MaximumNormalizedRmse
    Difference = $differenceTarget
    Accepted = $accepted
}

if (-not $accepted) {
    exit 1
}

exit 0
