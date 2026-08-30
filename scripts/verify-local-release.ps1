[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    [string]$ExpectedCommit,

    [ValidateSet('win-x64')]
    [string]$ExpectedRuntime = 'win-x64',

    [switch]$RequireCleanSource,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$requiredMetadata = @(
    'release-manifest.json',
    'sbom.spdx.json',
    'THIRD-PARTY-NOTICES.txt'
)
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function ConvertTo-HexString {
    param([byte[]]$Bytes)
    return ([System.BitConverter]::ToString($Bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-SafeRelativePath {
    param([string]$RootWithSeparator, [string]$CandidatePath)

    if ([string]::IsNullOrWhiteSpace($CandidatePath) -or
        [System.IO.Path]::IsPathRooted($CandidatePath) -or
        $CandidatePath.Contains('\') -or
        $CandidatePath.Contains([char]0)) {
        throw "Bundle inventory contains an unsafe path: '$CandidatePath'."
    }

    $segments = $CandidatePath.Split('/')
    if ($segments.Count -eq 0 -or $segments | Where-Object { $_ -in @('', '.', '..') }) {
        throw "Bundle inventory contains an unsafe path segment: '$CandidatePath'."
    }

    $full = [System.IO.Path]::GetFullPath((Join-Path $RootWithSeparator $CandidatePath))
    if (-not $full.StartsWith($RootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundle inventory escapes the candidate root: '$CandidatePath'."
    }
    return $full
}

function Read-Checksums {
    param([string]$Path, [string]$RootWithSeparator)

    $rows = [ordered]@{}
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Invalid checksums.sha256 line $lineNumber."
        }
        $hash = $Matches[1]
        $relative = $Matches[2]
        [void](Get-SafeRelativePath -RootWithSeparator $RootWithSeparator -CandidatePath $relative)
        if ($relative.Equals('checksums.sha256', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'checksums.sha256 cannot contain a self-referential checksum.'
        }
        if ($rows.Keys | Where-Object { $_.Equals($relative, [System.StringComparison]::OrdinalIgnoreCase) }) {
            throw "Duplicate checksum path: '$relative'."
        }
        $rows[$relative] = $hash
    }
    if ($rows.Count -eq 0) {
        throw 'checksums.sha256 is empty.'
    }
    return $rows
}

function Assert-ExactSet {
    param(
        [string]$Label,
        [string[]]$Expected,
        [string[]]$Actual
    )

    $expectedSet = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$Expected,
        [System.StringComparer]::OrdinalIgnoreCase)
    $actualSet = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$Actual,
        [System.StringComparer]::OrdinalIgnoreCase)
    if ($expectedSet.Count -ne $Expected.Count -or $actualSet.Count -ne $Actual.Count) {
        throw "$Label contains duplicate paths."
    }
    if (-not $expectedSet.SetEquals($actualSet)) {
        $missing = @($expectedSet | Where-Object { -not $actualSet.Contains($_) })
        $unexpected = @($actualSet | Where-Object { -not $expectedSet.Contains($_) })
        throw "$Label mismatch. Missing=[$($missing -join ', ')]; unexpected=[$($unexpected -join ', ')]."
    }
}

$bundleFull = [System.IO.Path]::GetFullPath($BundlePath)
if (-not (Test-Path -LiteralPath $bundleFull -PathType Container)) {
    throw "Release bundle directory does not exist: $bundleFull"
}
$rootWithSeparator = $bundleFull.TrimEnd('\') + '\'
$rootInfo = Get-Item -LiteralPath $bundleFull
if (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Release bundle root cannot be a reparse point: $bundleFull"
}

$allItems = @(Get-ChildItem -LiteralPath $bundleFull -Recurse -Force)
$reparse = @($allItems | Where-Object {
    ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparse.Count -gt 0) {
    throw "Release bundle contains reparse points: $($reparse.FullName -join ', ')"
}

$manifestPath = Join-Path $bundleFull 'release-manifest.json'
$checksumsPath = Join-Path $bundleFull 'checksums.sha256'
$sbomPath = Join-Path $bundleFull 'sbom.spdx.json'
$noticesPath = Join-Path $bundleFull 'THIRD-PARTY-NOTICES.txt'
foreach ($required in @($manifestPath, $checksumsPath, $sbomPath, $noticesPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Release bundle is missing required metadata: $required"
    }
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 2) {
    throw "Unsupported release manifest schema: '$($manifest.schemaVersion)'."
}
if ([string]$manifest.product -ne 'Map') {
    throw "Unexpected product identity: '$($manifest.product)'."
}
if ([string]$manifest.commit -notmatch '^[0-9a-f]{40}$') {
    throw "Release manifest commit is not a full lowercase Git hash: '$($manifest.commit)'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not ([string]$manifest.commit).Equals($ExpectedCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release commit '$($manifest.commit)' does not match expected '$ExpectedCommit'."
}
if ([string]$manifest.runtime -ne $ExpectedRuntime) {
    throw "Release runtime '$($manifest.runtime)' does not match expected '$ExpectedRuntime'."
}
if ([string]$manifest.configuration -ne 'Release' -or -not [bool]$manifest.selfContained) {
    throw 'Release manifest must describe a self-contained Release build.'
}
if ([string]$manifest.sourceStatusSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'Release manifest sourceStatusSha256 is missing or invalid.'
}
$sourceStatus = @($manifest.sourceStatus | ForEach-Object { [string]$_ })
$sourceStatusText = $sourceStatus -join "`n"
$sourceStatusBytes = [System.Text.Encoding]::UTF8.GetBytes($sourceStatusText)
$sourceStatusAlgorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $sourceStatusHash = ConvertTo-HexString ($sourceStatusAlgorithm.ComputeHash($sourceStatusBytes))
}
finally {
    $sourceStatusAlgorithm.Dispose()
}
if ($sourceStatusHash -ne [string]$manifest.sourceStatusSha256) {
    throw 'Release manifest sourceStatus does not match sourceStatusSha256.'
}
if ([bool]$manifest.cleanSource -ne ($sourceStatus.Count -eq 0)) {
    throw 'Release manifest cleanSource disagrees with sourceStatus.'
}
if ($RequireCleanSource -and -not [bool]$manifest.cleanSource) {
    throw 'This verification requires cleanSource=true.'
}

$payloadRows = @($manifest.payload)
$metadataPaths = @($manifest.metadataFiles | ForEach-Object { [string]$_ })
foreach ($required in $requiredMetadata) {
    if (-not ($metadataPaths | Where-Object { $_.Equals($required, [System.StringComparison]::OrdinalIgnoreCase) })) {
        throw "Release manifest metadataFiles is missing '$required'."
    }
}
if ([int]$manifest.payloadFileCount -ne $payloadRows.Count) {
    throw "payloadFileCount '$($manifest.payloadFileCount)' does not match payload rows '$($payloadRows.Count)'."
}

$payloadPaths = @()
foreach ($row in $payloadRows) {
    $relative = [string]$row.path
    $full = Get-SafeRelativePath -RootWithSeparator $rootWithSeparator -CandidatePath $relative
    if ([string]$row.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$row.bytes -lt 0) {
        throw "Invalid payload metadata for '$relative'."
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Manifest payload is missing: '$relative'."
    }
    $file = Get-Item -LiteralPath $full
    if ([long]$file.Length -ne [long]$row.bytes) {
        throw "Manifest byte count mismatch for '$relative'."
    }
    $payloadPaths += $relative
}
if (-not ($payloadPaths | Where-Object { $_.Equals('Map.App.exe', [System.StringComparison]::OrdinalIgnoreCase) })) {
    throw 'Release manifest payload does not contain Map.App.exe.'
}
$applicationVersion = [string]$manifest.applicationVersion
if ($applicationVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release manifest applicationVersion is not a three-part version: '$applicationVersion'."
}
$exeVersion = (Get-Item -LiteralPath (Join-Path $bundleFull 'Map.App.exe')).VersionInfo.FileVersion
$exeVersionParts = @($exeVersion -split '\.')
$exeProductVersion = if ($exeVersionParts.Count -ge 3) {
    $exeVersionParts[0..2] -join '.'
} else {
    ''
}
if ($exeProductVersion -ne $applicationVersion) {
    throw "Map.App.exe file version '$exeVersion' does not match manifest '$applicationVersion'."
}

foreach ($relative in $metadataPaths) {
    [void](Get-SafeRelativePath -RootWithSeparator $rootWithSeparator -CandidatePath $relative)
}
Assert-ExactSet -Label 'Manifest inventory' -Expected @($payloadPaths + $metadataPaths) -Actual @(
    $payloadPaths + $metadataPaths)

$checksumRows = Read-Checksums -Path $checksumsPath -RootWithSeparator $rootWithSeparator
$bundleFiles = @($allItems | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $_.FullName.Substring($rootWithSeparator.Length).Replace('\', '/')
})
$hashedBundleFiles = @($bundleFiles | Where-Object {
    -not $_.Equals('checksums.sha256', [System.StringComparison]::OrdinalIgnoreCase)
})
Assert-ExactSet -Label 'Checksum inventory' -Expected $hashedBundleFiles -Actual @($checksumRows.Keys)
Assert-ExactSet -Label 'Manifest versus bundle' -Expected @($payloadPaths + $metadataPaths + 'checksums.sha256') -Actual $bundleFiles

foreach ($relative in $checksumRows.Keys) {
    $full = Get-SafeRelativePath -RootWithSeparator $rootWithSeparator -CandidatePath $relative
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
    if ($actualHash -ne [string]$checksumRows[$relative]) {
        throw "SHA-256 mismatch for '$relative'."
    }
}
foreach ($row in $payloadRows) {
    $relative = [string]$row.path
    if ([string]$checksumRows[$relative] -ne [string]$row.sha256) {
        throw "Manifest/checksum disagreement for '$relative'."
    }
}

$sbom = Get-Content -Raw -LiteralPath $sbomPath | ConvertFrom-Json
if ([string]$sbom.spdxVersion -ne 'SPDX-2.3' -or [string]$sbom.dataLicense -ne 'CC0-1.0') {
    throw 'SBOM is not an SPDX 2.3 JSON document with CC0-1.0 dataLicense.'
}
$sbomPackages = @($sbom.packages)
if ($sbomPackages.Count -eq 0 -or [int]$manifest.sbomPackageCount -ne $sbomPackages.Count) {
    throw "SBOM package count '$($sbomPackages.Count)' does not match manifest '$($manifest.sbomPackageCount)'."
}
$spdxIds = @($sbomPackages | ForEach-Object { [string]$_.SPDXID })
Assert-ExactSet -Label 'SBOM SPDXID inventory' -Expected $spdxIds -Actual $spdxIds
if (-not ($sbomPackages | Where-Object {
    [string]$_.SPDXID -eq 'SPDXRef-Package-Map' -and [string]$_.name -eq 'Map'
})) {
    throw 'SBOM does not describe the Map application package.'
}
if ((Get-Item -LiteralPath $noticesPath).Length -eq 0) {
    throw 'THIRD-PARTY-NOTICES.txt is empty.'
}

$result = [ordered]@{
    result = 'PASS'
    bundlePath = $bundleFull
    commit = [string]$manifest.commit
    cleanSource = [bool]$manifest.cleanSource
    runtime = [string]$manifest.runtime
    applicationVersion = [string]$manifest.applicationVersion
    payloadFileCount = $payloadRows.Count
    metadataFileCount = $metadataPaths.Count + 1
    checksumCount = $checksumRows.Count
    sbomPackageCount = $sbomPackages.Count
    verifiedUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$json = $result | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportFull = [System.IO.Path]::GetFullPath($ReportPath)
    if ($reportFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Verifier report must be outside the immutable bundle.'
    }
    $reportDirectory = Split-Path -Parent $reportFull
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        [System.IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    }
    [System.IO.File]::WriteAllText($reportFull, $json + [Environment]::NewLine, $utf8NoBom)
}

Write-Output $json
