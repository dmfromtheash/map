[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$verifier = Join-Path $PSScriptRoot 'verify-local-release.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("map-stage11-release-{0}" -f [Guid]::NewGuid().ToString('N'))
$tempRootFull = [System.IO.Path]::GetFullPath($tempRoot)
$systemTempFull = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $tempRootFull.StartsWith($systemTempFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a release-test fixture outside the system temp directory: $tempRootFull"
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:passed = 0

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Write-Checksums {
    param([string]$BundlePath)
    $root = [System.IO.Path]::GetFullPath($BundlePath).TrimEnd('\') + '\'
    $rows = foreach ($file in Get-ChildItem -LiteralPath $BundlePath -File -Recurse |
        Where-Object { $_.Name -ne 'checksums.sha256' } |
        Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $BundlePath 'checksums.sha256'),
        [string[]]$rows,
        $utf8NoBom)
}

function New-HealthyBundle {
    param([string]$BundlePath)
    [System.IO.Directory]::CreateDirectory($BundlePath) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $BundlePath 'data')) | Out-Null

    $hostExe = (Get-Process -Id $PID).Path
    Copy-Item -LiteralPath $hostExe -Destination (Join-Path $BundlePath 'Map.App.exe')
    $hostVersionParts = @((Get-Item -LiteralPath $hostExe).VersionInfo.FileVersion -split '\.')
    $fixtureVersion = $hostVersionParts[0..2] -join '.'
    Write-Utf8File -Path (Join-Path $BundlePath 'data/profile.txt') -Content "stage11`r`n"

    $sbom = [ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = 'Map-test-sbom'
        documentNamespace = 'urn:uuid:11111111-1111-1111-1111-111111111111'
        creationInfo = [ordered]@{
            created = '2026-08-30T00:00:00Z'
            creators = @('Tool: Map Stage 11 release test')
        }
        packages = @(
            [ordered]@{
                SPDXID = 'SPDXRef-Package-Map'
                name = 'Map'
                versionInfo = $fixtureVersion
                downloadLocation = 'NOASSERTION'
                filesAnalyzed = $false
                licenseConcluded = 'NOASSERTION'
                licenseDeclared = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
            }
        )
        relationships = @()
    }
    Write-Utf8File -Path (Join-Path $BundlePath 'sbom.spdx.json') -Content (($sbom | ConvertTo-Json -Depth 8) + "`r`n")
    Write-Utf8File -Path (Join-Path $BundlePath 'THIRD-PARTY-NOTICES.txt') -Content "Map Stage 11 synthetic verifier fixture.`r`n"

    $payload = foreach ($relative in @('Map.App.exe', 'data/profile.txt')) {
        $file = Get-Item -LiteralPath (Join-Path $BundlePath $relative)
        [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            bytes = [long]$file.Length
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 2
        product = 'Map'
        applicationVersion = $fixtureVersion
        releaseStatus = 'development'
        commit = '1111111111111111111111111111111111111111'
        cleanSource = $true
        sourceStatus = @()
        sourceStatusSha256 = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
        runtime = 'win-x64'
        configuration = 'Release'
        selfContained = $true
        mapProjectSchema = 1
        assetCatalogSchema = 2
        createdUtc = '2026-08-30T00:00:00Z'
        payloadFileCount = $payload.Count
        sbomPackageCount = 1
        metadataFiles = @('release-manifest.json', 'sbom.spdx.json', 'THIRD-PARTY-NOTICES.txt')
        payload = @($payload)
    }
    Write-Utf8File -Path (Join-Path $BundlePath 'release-manifest.json') -Content (($manifest | ConvertTo-Json -Depth 8) + "`r`n")
    Write-Checksums -BundlePath $BundlePath
}

function Copy-CaseBundle {
    param([string]$TemplatePath, [string]$Name)
    $casePath = Join-Path $tempRootFull $Name
    Copy-Item -LiteralPath $TemplatePath -Destination $casePath -Recurse
    return $casePath
}

function Invoke-Verifier {
    param([string]$BundlePath)
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifier `
        -BundlePath $BundlePath `
        -ExpectedCommit '1111111111111111111111111111111111111111' `
        -ExpectedRuntime 'win-x64' 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorAction
    return [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) -join [Environment]::NewLine }
}

function Assert-Case {
    param([string]$Name, [bool]$ExpectSuccess, [scriptblock]$Arrange)
    $casePath = Copy-CaseBundle -TemplatePath $template -Name $Name
    if ($null -ne $Arrange) {
        & $Arrange $casePath
    }
    $result = Invoke-Verifier -BundlePath $casePath
    if ($ExpectSuccess -and $result.ExitCode -ne 0) {
        throw "$Name expected PASS but verifier exited $($result.ExitCode): $($result.Output)"
    }
    if (-not $ExpectSuccess -and $result.ExitCode -eq 0) {
        throw "$Name expected rejection but verifier passed: $($result.Output)"
    }
    $script:passed++
    Write-Host ("PASS {0}: verifier exit {1}" -f $Name, $result.ExitCode)
}

try {
    [System.IO.Directory]::CreateDirectory($tempRootFull) | Out-Null
    $template = Join-Path $tempRootFull 'template with spaces - Юникод'
    New-HealthyBundle -BundlePath $template

    Assert-Case -Name 'healthy-unicode-path' -ExpectSuccess $true -Arrange $null
    Assert-Case -Name 'tampered-payload' -ExpectSuccess $false -Arrange {
        param($path)
        [System.IO.File]::AppendAllText((Join-Path $path 'data/profile.txt'), 'tamper', $utf8NoBom)
    }
    Assert-Case -Name 'missing-payload' -ExpectSuccess $false -Arrange {
        param($path)
        Remove-Item -LiteralPath (Join-Path $path 'data/profile.txt')
    }
    Assert-Case -Name 'unexpected-extra-file' -ExpectSuccess $false -Arrange {
        param($path)
        Write-Utf8File -Path (Join-Path $path 'unexpected.txt') -Content 'not inventoried'
    }
    Assert-Case -Name 'tampered-manifest' -ExpectSuccess $false -Arrange {
        param($path)
        [System.IO.File]::AppendAllText((Join-Path $path 'release-manifest.json'), ' ', $utf8NoBom)
    }
    Assert-Case -Name 'semantic-sbom-corruption' -ExpectSuccess $false -Arrange {
        param($path)
        Write-Utf8File -Path (Join-Path $path 'sbom.spdx.json') -Content '{"spdxVersion":"SPDX-1.0","packages":[]}'
        Write-Checksums -BundlePath $path
    }
    Assert-Case -Name 'manifest-path-traversal' -ExpectSuccess $false -Arrange {
        param($path)
        $manifestPath = Join-Path $path 'release-manifest.json'
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        $manifest.payload[0].path = '../outside.exe'
        Write-Utf8File -Path $manifestPath -Content (($manifest | ConvertTo-Json -Depth 8) + "`r`n")
        Write-Checksums -BundlePath $path
    }
    Assert-Case -Name 'locked-payload' -ExpectSuccess $false -Arrange {
        param($path)
        $script:lockedStream = [System.IO.File]::Open(
            (Join-Path $path 'data/profile.txt'),
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
    }
}
finally {
    if ($null -ne $script:lockedStream) {
        $script:lockedStream.Dispose()
    }
    if ($tempRootFull.StartsWith($systemTempFull, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $tempRootFull)) {
        Remove-Item -LiteralPath $tempRootFull -Recurse -Force
    }
}

if ($script:passed -ne 8) {
    throw "Stage 11 release verifier harness expected 8 cases, observed $script:passed."
}

Write-Host "Stage 11 release verifier harness: PASS ($script:passed/8)."
