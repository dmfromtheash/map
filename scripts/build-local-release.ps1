[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'version.json') |
    ConvertFrom-Json

& (Join-Path $PSScriptRoot 'check-version-consistency.ps1')

$dirty = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git worktree.'
}
if ($dirty.Count -gt 0 -and -not $AllowDirty) {
    throw 'Release bundle requires a clean worktree. Commit/checkpoint the exact snapshot or use -AllowDirty only for a disposable packaging probe.'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the release commit.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts/releases'
}
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)

$releaseId = "Map-$($manifest.version)-$Runtime-$($commit.Substring(0, 12))"
$finalPath = Join-Path $outputRootFull $releaseId
$incompletePath = "$finalPath.incomplete"
if ((Test-Path -LiteralPath $finalPath) -or (Test-Path -LiteralPath $incompletePath)) {
    throw "Immutable release path already exists: $finalPath"
}

New-Item -ItemType Directory -Path $incompletePath -Force:$false | Out-Null

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
    -p:Version=$($manifest.version) `
    -o $incompletePath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. The incomplete directory was preserved for diagnosis: $incompletePath"
}

$payloadFiles = @(Get-ChildItem -LiteralPath $incompletePath -File -Recurse |
    Sort-Object FullName)
$payloadPrefix = $incompletePath.TrimEnd('\') + '\'
$hashRows = foreach ($file in $payloadFiles) {
    if (-not $file.FullName.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload escaped the release directory: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    [pscustomobject]@{ path = $relative; sha256 = $hash; bytes = $file.Length }
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$checksumLines = @($hashRows | ForEach-Object { "$($_.sha256)  $($_.path)" })
[System.IO.File]::WriteAllLines(
    (Join-Path $incompletePath 'checksums.sha256'),
    $checksumLines,
    $utf8NoBom)

$releaseManifest = [ordered]@{
    product = [string]$manifest.product
    applicationVersion = [string]$manifest.version
    releaseStatus = [string]$manifest.releaseStatus
    commit = $commit
    cleanSource = ($dirty.Count -eq 0)
    runtime = $Runtime
    configuration = $Configuration
    selfContained = $true
    mapProjectSchema = [int]$manifest.mapProjectSchema
    assetCatalogSchema = [int]$manifest.assetCatalogSchema
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    payloadFileCount = $payloadFiles.Count
    payload = $hashRows
}
$releaseManifestJson = $releaseManifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText(
    (Join-Path $incompletePath 'release-manifest.json'),
    $releaseManifestJson + [Environment]::NewLine,
    $utf8NoBom)

Move-Item -LiteralPath $incompletePath -Destination $finalPath

Write-Host "Local immutable bundle: $finalPath"
Write-Host 'Rollback: retain an older verified bundle and launch its Map.App.exe. No install, switch, upload, tag or automatic update was performed.'
