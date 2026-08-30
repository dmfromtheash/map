[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot,

    [switch]$AllowDirtyDisposableProbe,

    [ValidateRange(67108864, [long]::MaxValue)]
    [long]$MinimumFreeBytes = 1073741824
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionManifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'version.json') |
    ConvertFrom-Json
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function ConvertTo-HexString {
    param([byte[]]$Bytes)
    return ([System.BitConverter]::ToString($Bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-TextSha256 {
    param([string]$Text)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ConvertTo-HexString ($algorithm.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)))
    }
    finally {
        $algorithm.Dispose()
    }
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Get-RelativeBundlePath {
    param([string]$RootWithSeparator, [string]$FullPath)
    $candidate = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $candidate.StartsWith($RootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release file escaped the candidate directory: $candidate"
    }
    return $candidate.Substring($RootWithSeparator.Length).Replace('\', '/')
}

function Get-XmlElementText {
    param([System.Xml.XmlNode]$Metadata, [string]$LocalName)
    $node = $Metadata.SelectSingleNode("*[local-name()='$LocalName']")
    if ($null -eq $node) { return '' }
    return [string]$node.InnerText
}

function Get-SpdxId {
    param([string]$Id, [string]$Version)
    return 'SPDXRef-Package-' + (($Id + '-' + $Version) -replace '[^A-Za-z0-9.-]', '-')
}

function Copy-DeclaredLicense {
    param(
        [string]$PackageDirectory,
        [string]$PackageId,
        [string]$PackageVersion,
        [System.Xml.XmlNode]$Metadata,
        [string]$BundleRoot
    )

    $licenseNode = $Metadata.SelectSingleNode("*[local-name()='license']")
    $candidate = $null
    if ($null -ne $licenseNode -and [string]$licenseNode.Attributes['type'].Value -eq 'file') {
        $declared = [string]$licenseNode.InnerText
        if (-not [string]::IsNullOrWhiteSpace($declared)) {
            $candidate = Join-Path $PackageDirectory $declared
        }
    }
    if ($null -eq $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $candidate = Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse |
            Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)(\.|$)' } |
            Sort-Object FullName |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ($null -eq $candidate) { return $null }

    $packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\') + '\'
    $licenseFull = [System.IO.Path]::GetFullPath($candidate)
    if (-not $licenseFull.StartsWith($packageRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package '$PackageId/$PackageVersion' declared a license outside its package directory."
    }

    $safeId = ($PackageId -replace '[^A-Za-z0-9._-]', '-')
    $safeVersion = ($PackageVersion -replace '[^A-Za-z0-9._-]', '-')
    $destinationDirectory = Join-Path $BundleRoot ("licenses/{0}-{1}" -f $safeId, $safeVersion)
    [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    $destination = Join-Path $destinationDirectory ([System.IO.Path]::GetFileName($licenseFull))
    Copy-Item -LiteralPath $licenseFull -Destination $destination
    return $destination
}

& (Join-Path $PSScriptRoot 'check-version-consistency.ps1')

$sourceStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git worktree.'
}
if ($sourceStatus.Count -gt 0 -and -not $AllowDirtyDisposableProbe) {
    throw 'Release bundle requires a clean worktree. Use -AllowDirtyDisposableProbe only for an explicitly disposable author probe.'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the release commit.'
}
$sourceStatusText = $sourceStatus -join "`n"
$sourceStatusHash = Get-TextSha256 -Text $sourceStatusText

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts/releases'
}
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $outputRootFull -PathType Leaf) {
    throw "Release output root is a file: $outputRootFull"
}
[System.IO.Directory]::CreateDirectory($outputRootFull) | Out-Null
$driveRoot = [System.IO.Path]::GetPathRoot($outputRootFull)
$drive = [System.IO.DriveInfo]::new($driveRoot)
if ($drive.AvailableFreeSpace -lt $MinimumFreeBytes) {
    throw "Insufficient free space for a release candidate: $($drive.AvailableFreeSpace) bytes available, $MinimumFreeBytes required."
}

$releaseId = "Map-$($versionManifest.version)-$Runtime-$($commit.Substring(0, 12))"
$finalPath = Join-Path $outputRootFull $releaseId
$incompletePath = "$finalPath.incomplete"
if ((Test-Path -LiteralPath $finalPath) -or (Test-Path -LiteralPath $incompletePath)) {
    throw "Immutable release path already exists: $finalPath"
}

[System.IO.Directory]::CreateDirectory($incompletePath) | Out-Null
$candidateRoot = [System.IO.Path]::GetFullPath($incompletePath).TrimEnd('\') + '\'

$appProject = Join-Path $repoRoot 'src/Map.App/Map.App.csproj'
& dotnet restore $appProject -r $Runtime --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed. The incomplete directory was preserved for diagnosis: $incompletePath"
}

& dotnet publish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --no-restore `
    -p:Version=$($versionManifest.version) `
    -o $incompletePath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. The incomplete directory was preserved for diagnosis: $incompletePath"
}

$appExe = Join-Path $incompletePath 'Map.App.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf) -or (Get-Item -LiteralPath $appExe).Length -eq 0) {
    throw "Publish did not produce a usable Map.App.exe. The incomplete directory was preserved: $incompletePath"
}
$expectedFileVersion = "$($versionManifest.version).0"
$actualFileVersion = (Get-Item -LiteralPath $appExe).VersionInfo.FileVersion
if ($actualFileVersion -ne $expectedFileVersion) {
    throw "Published EXE version '$actualFileVersion' does not match '$expectedFileVersion'."
}

$publishedPayload = @(Get-ChildItem -LiteralPath $incompletePath -File -Recurse | Sort-Object FullName)
if ($publishedPayload.Count -eq 0) {
    throw "Publish output is empty. The incomplete directory was preserved: $incompletePath"
}

$assetsPath = Join-Path $repoRoot 'src/Map.App/obj/project.assets.json'
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
$targetName = "net10.0/$Runtime"
$targetProperty = $assets.targets.PSObject.Properties | Where-Object { $_.Name -eq $targetName } | Select-Object -First 1
if ($null -eq $targetProperty) {
    throw "project.assets.json does not contain target '$targetName'."
}
$target = $targetProperty.Value
$packageNodes = @($target.PSObject.Properties | Where-Object { [string]$_.Value.type -eq 'package' } | Sort-Object Name)

$globalPackagesLine = @(& dotnet nuget locals global-packages --list) | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or $globalPackagesLine -notmatch ':\s*(.+)$') {
    throw 'Unable to resolve the NuGet global-packages directory.'
}
$globalPackages = [System.IO.Path]::GetFullPath($Matches[1].Trim())

$directIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$dependencyGroup = $assets.projectFileDependencyGroups.PSObject.Properties |
    Where-Object { $_.Name -eq 'net10.0' } | Select-Object -First 1
foreach ($entry in @($dependencyGroup.Value)) {
    $id = ([string]$entry -split '\s+')[0]
    if (-not $id.StartsWith('Map.', [System.StringComparison]::OrdinalIgnoreCase)) {
        [void]$directIds.Add($id)
    }
}

$packages = @()
$metadataFiles = [System.Collections.Generic.List[string]]::new()
$metadataFiles.Add('release-manifest.json')
$metadataFiles.Add('sbom.spdx.json')
$metadataFiles.Add('THIRD-PARTY-NOTICES.txt')
$noticeLines = [System.Collections.Generic.List[string]]::new()
$noticeLines.Add('Map third-party package notices')
$noticeLines.Add('Generated from the exact win-x64 project.assets.json and local NuGet package metadata.')
$noticeLines.Add('This inventory is not a legal opinion; verify upstream terms before a public release.')
$noticeLines.Add('')
$needsMitText = $false

foreach ($node in $packageNodes) {
    $parts = $node.Name -split '/', 2
    $packageId = $parts[0]
    $packageVersion = $parts[1]
    $libraryProperty = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -eq $node.Name } | Select-Object -First 1
    if ($null -eq $libraryProperty) {
        throw "Package '$($node.Name)' is missing from project.assets.json libraries."
    }
    $packageDirectory = Join-Path $globalPackages ([string]$libraryProperty.Value.path)
    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "Package '$($node.Name)' has no local nuspec."
    }
    [xml]$nuspecXml = Get-Content -Raw -LiteralPath $nuspec.FullName
    $metadata = $nuspecXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $licenseDeclared = if ($null -ne $licenseNode) { [string]$licenseNode.InnerText } else { 'NOASSERTION' }
    if ([string]::IsNullOrWhiteSpace($licenseDeclared)) { $licenseDeclared = 'NOASSERTION' }
    if ($licenseDeclared -eq 'MIT') { $needsMitText = $true }
    $copyright = Get-XmlElementText -Metadata $metadata -LocalName 'copyright'
    if ([string]::IsNullOrWhiteSpace($copyright)) { $copyright = 'NOASSERTION' }
    $projectUrl = Get-XmlElementText -Metadata $metadata -LocalName 'projectUrl'
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $repositoryUrl = if ($null -ne $repositoryNode) { [string]$repositoryNode.Attributes['url'].Value } else { '' }

    $copiedLicense = Copy-DeclaredLicense `
        -PackageDirectory $packageDirectory `
        -PackageId $packageId `
        -PackageVersion $packageVersion `
        -Metadata $metadata `
        -BundleRoot $incompletePath
    $licenseRelative = $null
    if ($null -ne $copiedLicense) {
        $licenseRelative = Get-RelativeBundlePath -RootWithSeparator $candidateRoot -FullPath $copiedLicense
        $metadataFiles.Add($licenseRelative)
    }

    $packages += [pscustomobject]@{
        Key = $node.Name
        Id = $packageId
        Version = $packageVersion
        SPDXID = Get-SpdxId -Id $packageId -Version $packageVersion
        Direct = $directIds.Contains($packageId)
        License = $licenseDeclared
        Copyright = $copyright
        ProjectUrl = $projectUrl
        RepositoryUrl = $repositoryUrl
        LicenseFile = $licenseRelative
        Dependencies = @($node.Value.dependencies.PSObject.Properties.Name)
    }

    $noticeLines.Add("$packageId $packageVersion")
    $noticeLines.Add("  scope: $(if ($directIds.Contains($packageId)) { 'direct' } else { 'transitive' })")
    $noticeLines.Add("  declared license: $licenseDeclared")
    $noticeLines.Add("  copyright: $copyright")
    if (-not [string]::IsNullOrWhiteSpace($projectUrl)) { $noticeLines.Add("  project: $projectUrl") }
    if (-not [string]::IsNullOrWhiteSpace($repositoryUrl)) { $noticeLines.Add("  repository: $repositoryUrl") }
    if ($null -ne $licenseRelative) { $noticeLines.Add("  bundled license: $licenseRelative") }
    elseif ($licenseDeclared -eq 'MIT') { $noticeLines.Add('  bundled license: licenses/MIT.txt') }
    else { $noticeLines.Add('  bundled license: unavailable; public release remains blocked') }
    $noticeLines.Add('')
}

if ($needsMitText) {
    $mitSource = Join-Path $repoRoot 'release/licenses/MIT.txt'
    $mitDestinationDirectory = Join-Path $incompletePath 'licenses'
    [System.IO.Directory]::CreateDirectory($mitDestinationDirectory) | Out-Null
    $mitDestination = Join-Path $mitDestinationDirectory 'MIT.txt'
    Copy-Item -LiteralPath $mitSource -Destination $mitDestination
    $metadataFiles.Add('licenses/MIT.txt')
}

$spdxPackages = @(
    [ordered]@{
        SPDXID = 'SPDXRef-Package-Map'
        name = 'Map'
        versionInfo = [string]$versionManifest.version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
        primaryPackagePurpose = 'APPLICATION'
    }
)
$spdxPackages += @($packages | ForEach-Object {
    [ordered]@{
        SPDXID = $_.SPDXID
        name = $_.Id
        versionInfo = $_.Version
        downloadLocation = "https://www.nuget.org/packages/$($_.Id)/$($_.Version)"
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = $_.License
        copyrightText = $_.Copyright
        primaryPackagePurpose = 'LIBRARY'
        externalRefs = @(
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$($_.Id)@$($_.Version)"
            }
        )
        comment = if ($_.Direct) { 'direct production dependency' } else { 'transitive production dependency' }
    }
})

$relationships = [System.Collections.Generic.List[object]]::new()
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-DOCUMENT'
    relationshipType = 'DESCRIBES'
    relatedSpdxElement = 'SPDXRef-Package-Map'
})
foreach ($package in $packages | Where-Object Direct) {
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-Map'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $package.SPDXID
    })
}
foreach ($package in $packages) {
    foreach ($dependencyId in $package.Dependencies) {
        $dependency = $packages | Where-Object { $_.Id -eq $dependencyId } | Select-Object -First 1
        if ($null -ne $dependency) {
            $relationships.Add([ordered]@{
                spdxElementId = $package.SPDXID
                relationshipType = 'DEPENDS_ON'
                relatedSpdxElement = $dependency.SPDXID
            })
        }
    }
}

$createdUtc = [DateTimeOffset]::UtcNow
$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "$releaseId-sbom"
    documentNamespace = "urn:uuid:$([Guid]::NewGuid().ToString('D'))"
    creationInfo = [ordered]@{
        created = $createdUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: Map scripts/build-local-release.ps1')
    }
    packages = $spdxPackages
    relationships = @($relationships)
}
Write-Utf8File -Path (Join-Path $incompletePath 'sbom.spdx.json') -Content (($sbom | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
Write-Utf8File -Path (Join-Path $incompletePath 'THIRD-PARTY-NOTICES.txt') -Content (($noticeLines -join [Environment]::NewLine) + [Environment]::NewLine)

$payloadRows = foreach ($file in $publishedPayload) {
    $relative = Get-RelativeBundlePath -RootWithSeparator $candidateRoot -FullPath $file.FullName
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    [ordered]@{ path = $relative; sha256 = $hash; bytes = [long]$file.Length }
}

$releaseManifest = [ordered]@{
    schemaVersion = 2
    product = [string]$versionManifest.product
    applicationVersion = [string]$versionManifest.version
    releaseStatus = [string]$versionManifest.releaseStatus
    commit = $commit
    cleanSource = ($sourceStatus.Count -eq 0)
    sourceStatus = @($sourceStatus)
    sourceStatusSha256 = $sourceStatusHash
    runtime = $Runtime
    configuration = $Configuration
    selfContained = $true
    mapProjectSchema = [int]$versionManifest.mapProjectSchema
    assetCatalogSchema = [int]$versionManifest.assetCatalogSchema
    createdUtc = $createdUtc.ToString('O')
    payloadFileCount = $payloadRows.Count
    sbomPackageCount = $spdxPackages.Count
    metadataFiles = @($metadataFiles | Sort-Object -Unique)
    payload = @($payloadRows)
}
Write-Utf8File -Path (Join-Path $incompletePath 'release-manifest.json') -Content (($releaseManifest | ConvertTo-Json -Depth 10) + [Environment]::NewLine)

$checksumRows = foreach ($file in Get-ChildItem -LiteralPath $incompletePath -File -Recurse |
    Where-Object { $_.Name -ne 'checksums.sha256' } |
    Sort-Object FullName) {
    $relative = Get-RelativeBundlePath -RootWithSeparator $candidateRoot -FullPath $file.FullName
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[System.IO.File]::WriteAllLines(
    (Join-Path $incompletePath 'checksums.sha256'),
    [string[]]$checksumRows,
    $utf8NoBom)

$verifyArguments = @{
    BundlePath = $incompletePath
    ExpectedCommit = $commit
    ExpectedRuntime = $Runtime
}
if ($sourceStatus.Count -eq 0) {
    $verifyArguments.RequireCleanSource = $true
}
& (Join-Path $PSScriptRoot 'verify-local-release.ps1') @verifyArguments | Write-Host
if ($LASTEXITCODE -ne 0) {
    throw "Release verification failed. The incomplete directory was preserved: $incompletePath"
}

Move-Item -LiteralPath $incompletePath -Destination $finalPath

Write-Host "Local immutable bundle: $finalPath"
Write-Host "Source status: clean=$($sourceStatus.Count -eq 0), statusSha256=$sourceStatusHash"
Write-Host "Inventory: payload=$($payloadRows.Count), metadata=$($releaseManifest.metadataFiles.Count + 1), SPDX packages=$($spdxPackages.Count)"
Write-Host 'Rollback: retain an older verified bundle and launch its Map.App.exe. No install, switch, upload, tag or automatic update was performed.'
