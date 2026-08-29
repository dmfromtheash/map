[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$versionManifestPath = Join-Path $repoRoot "version.json"
$buildPropsPath = Join-Path $repoRoot "Directory.Build.props"
$appVersionPath = Join-Path $repoRoot "src/Map.App/AppVersion.cs"
$mainWindowPath = Join-Path $repoRoot "src/Map.App/MainWindow.cs"
$editorViewModelPath = Join-Path $repoRoot "src/Map.Presentation/ViewModels/EditorViewModel.cs"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
$releaseDocumentPath = Join-Path $repoRoot "docs/12_RELEASE_VERSIONING_AND_ROLLBACK.md"

$manifest = Get-Content -Raw -LiteralPath $versionManifestPath | ConvertFrom-Json
[xml]$buildProps = Get-Content -Raw -LiteralPath $buildPropsPath
$versionGroup = @($buildProps.Project.PropertyGroup) |
    Where-Object { $_.VersionPrefix } |
    Select-Object -First 1

if ($null -eq $versionGroup) {
    throw "Directory.Build.props does not define VersionPrefix."
}

$version = [string]$manifest.version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "version.json contains an invalid SemVer core: '$version'."
}

$expectedAssemblyVersion = "$version.0"
$checks = [ordered]@{
    VersionPrefix = [string]$versionGroup.VersionPrefix
    Version = [string]$versionGroup.Version
    InformationalVersion = [string]$versionGroup.InformationalVersion
    AssemblyVersion = [string]$versionGroup.AssemblyVersion
    FileVersion = [string]$versionGroup.FileVersion
}

if ($checks.VersionPrefix -ne $version) {
    throw "VersionPrefix '$($checks.VersionPrefix)' does not match version.json '$version'."
}

foreach ($name in @('Version', 'InformationalVersion')) {
    if ($checks[$name] -ne '$(VersionPrefix)') {
        throw "$name must be derived from VersionPrefix."
    }
}

foreach ($name in @('AssemblyVersion', 'FileVersion')) {
    if ($checks[$name] -ne $expectedAssemblyVersion) {
        throw "$name '$($checks[$name])' does not match '$expectedAssemblyVersion'."
    }
}

$appVersion = Get-Content -Raw -LiteralPath $appVersionPath
if ($appVersion -notmatch 'AssemblyInformationalVersionAttribute' -or
    $appVersion.IndexOf('"' + $version + '"', [System.StringComparison]::Ordinal) -ge 0) {
    throw "AppVersion must resolve the assembly informational version without a hard-coded product version."
}

$mainWindow = Get-Content -Raw -LiteralPath $mainWindowPath
if ($mainWindow -notmatch 'appVersion:\s*AppVersion\.Current') {
    throw "Map.App does not pass its embedded product version into EditorViewModel."
}
if ($mainWindow -notmatch 'Title\s*=\s*\$"Map \u00B7 \{AppVersion\.Current\}"') {
    throw "The native MainWindow title does not expose AppVersion.Current."
}

$editorViewModel = Get-Content -Raw -LiteralPath $editorViewModelPath
if ($editorViewModel -match 'appVersion\s*=\s*"\d+\.\d+\.\d+"') {
    throw "EditorViewModel still contains a hard-coded application version."
}

$versionHeadingPattern = '(?m)^##\s+' + [regex]::Escape($version) + '(?=\s|$)'
$changelog = Get-Content -Raw -LiteralPath $changelogPath
if ($changelog -notmatch $versionHeadingPattern) {
    throw "CHANGELOG.md does not contain a heading for application version '$version'."
}

$releaseDocument = Get-Content -Raw -LiteralPath $releaseDocumentPath
if ($releaseDocument.IndexOf($version, [System.StringComparison]::Ordinal) -lt 0) {
    throw "Release/versioning documentation does not mention application version '$version'."
}

Write-Host "Version consistency: PASS ($version; mapproj schema $($manifest.mapProjectSchema); catalog schema $($manifest.assetCatalogSchema))."
