[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$syncScript = Join-Path $PSScriptRoot 'sync-map-research-lab.ps1'
$systemTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')

function Invoke-GitFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CheckoutPath,
        [Parameter(Mandatory = $true)]
        [string[]]$GitArguments
    )

    & git -C $CheckoutPath @GitArguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture git command failed: git -C '$CheckoutPath' $($GitArguments -join ' ')"
    }
}

function Write-FixtureManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$TargetRoot,
        [Parameter(Mandatory = $true)]
        [string]$Commit,
        [Parameter(Mandatory = $true)]
        [string[]]$SparsePaths,
        [Parameter(Mandatory = $true)]
        [string]$LicenseFile
    )

    $manifest = [ordered]@{
        schemaVersion = 1
        capturedDate = '2026-08-29'
        defaultLabRoot = $TargetRoot
        policy = 'Regression fixture only.'
        repositories = @(
            [ordered]@{
                id = 'fixture'
                url = 'https://github.com/example/map-research-fixture.git'
                commit = $Commit
                license = 'MIT'
                licenseFile = $LicenseFile
                admission = 'test-only'
                sparsePaths = $SparsePaths
            }
        )
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-SyncProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [string]$TargetRoot,
        [switch]$Acquire
    )

    $processArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $syncScript,
        '-ManifestPath', $ManifestPath,
        '-TargetRoot', $TargetRoot
    )
    if ($Acquire) {
        $processArguments += '-Acquire'
    }

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $outputLines = @(& powershell @processArguments 2>&1 | ForEach-Object { [string]$_ })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $outputLines -join [Environment]::NewLine
    }
}

function Assert-OutputContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Output,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedFragments,
        [Parameter(Mandatory = $true)]
        [string]$CaseName
    )

    foreach ($fragment in $ExpectedFragments) {
        if (-not $Output.Contains($fragment)) {
            throw "$CaseName output did not contain '$fragment'. Output:`n$Output"
        }
    }
}

function Assert-FailingProbe {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedFragments,
        [Parameter(Mandatory = $true)]
        [string]$CaseName
    )

    if ($Result.ExitCode -eq 0) {
        throw "$CaseName unexpectedly returned exit 0. Output:`n$($Result.Output)"
    }
    if ($Result.Output.Contains('ResearchLab verification: PASS')) {
        throw "$CaseName emitted the final PASS marker. Output:`n$($Result.Output)"
    }
    Assert-OutputContains -Output $Result.Output -ExpectedFragments $ExpectedFragments -CaseName $CaseName
    Write-Host "$CaseName`: PASS (rejected with exit $($Result.ExitCode))."
}

$fixtureRoot = $null
do {
    $fixtureRoot = Join-Path $systemTempRoot ("mrt-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
} while (Test-Path -LiteralPath $fixtureRoot)

$initialMapHead = (& git -C $repoRoot rev-parse HEAD).Trim()
$initialMapStatus = @(& git -C $repoRoot status --porcelain=v2)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($initialMapHead)) {
    throw 'Unable to capture the Map source repository before probes.'
}

try {
    $fixtureSource = Join-Path $fixtureRoot 'sources\fixture'
    $manifestRoot = Join-Path $fixtureRoot 'manifests'
    New-Item -ItemType Directory -Path $fixtureSource -Force:$false | Out-Null
    New-Item -ItemType Directory -Path $manifestRoot -Force:$false | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fixtureSource 'Healthy') -Force:$false | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fixtureSource 'CasePath') -Force:$false | Out-Null
    Set-Content -LiteralPath (Join-Path $fixtureSource 'LICENSE') -Value 'fixture license' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fixtureSource 'README.md') -Value '# fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fixtureSource 'Healthy\probe.txt') -Value 'healthy' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fixtureSource 'CasePath\probe.txt') -Value 'case-sensitive' -Encoding ASCII

    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('init', '--quiet')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('config', 'user.name', 'Map Research Fixture')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('config', 'user.email', 'fixture@invalid.local')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('config', 'core.autocrlf', 'false')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('add', '--all')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @('commit', '--quiet', '-m', 'fixture')
    Invoke-GitFixture -CheckoutPath $fixtureSource -GitArguments @(
        'remote', 'add', 'origin', 'https://github.com/example/map-research-fixture.git')

    $fixtureCommit = (& git -C $fixtureSource rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $fixtureCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the fixture commit.'
    }
    $initialFixtureStatus = @(& git -C $fixtureSource status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $initialFixtureStatus.Count -ne 0) {
        throw 'Fixture repository is not clean before the probes.'
    }

    $healthyManifest = Join-Path $manifestRoot 'healthy.json'
    Write-FixtureManifest -Path $healthyManifest -TargetRoot $fixtureRoot -Commit $fixtureCommit `
        -SparsePaths @('LICENSE', 'README.md', 'Healthy', 'CasePath') -LicenseFile 'LICENSE'
    $healthyResult = Invoke-SyncProbe -ManifestPath $healthyManifest -TargetRoot $fixtureRoot
    if ($healthyResult.ExitCode -ne 0) {
        throw "Healthy manifest failed with exit $($healthyResult.ExitCode). Output:`n$($healthyResult.Output)"
    }
    Assert-OutputContains -Output $healthyResult.Output `
        -ExpectedFragments @('fixture', $fixtureCommit, 'ResearchLab verification: PASS (1 pinned public repositories') `
        -CaseName 'healthy manifest'
    Write-Host 'healthy manifest: PASS.'

    $missingPathManifest = Join-Path $manifestRoot 'missing-path.json'
    Write-FixtureManifest -Path $missingPathManifest -TargetRoot $fixtureRoot -Commit $fixtureCommit `
        -SparsePaths @('LICENSE', 'MissingPath') -LicenseFile 'LICENSE'
    $missingPathResult = Invoke-SyncProbe -ManifestPath $missingPathManifest -TargetRoot $fixtureRoot
    Assert-FailingProbe -Result $missingPathResult `
        -ExpectedFragments @('fixture', $fixtureCommit, "sparse path 'MissingPath'") `
        -CaseName 'nonexistent sparse path'

    $wrongCaseManifest = Join-Path $manifestRoot 'wrong-case.json'
    Write-FixtureManifest -Path $wrongCaseManifest -TargetRoot $fixtureRoot -Commit $fixtureCommit `
        -SparsePaths @('LICENSE', 'casepath') -LicenseFile 'LICENSE'
    $wrongCaseResult = Invoke-SyncProbe -ManifestPath $wrongCaseManifest -TargetRoot $fixtureRoot
    Assert-FailingProbe -Result $wrongCaseResult `
        -ExpectedFragments @('fixture', $fixtureCommit, "sparse path 'casepath'", 'case-sensitive tree match') `
        -CaseName 'wrong-case sparse path'

    $missingLicenseManifest = Join-Path $manifestRoot 'missing-license.json'
    Write-FixtureManifest -Path $missingLicenseManifest -TargetRoot $fixtureRoot -Commit $fixtureCommit `
        -SparsePaths @('Healthy', 'MISSING-LICENSE') -LicenseFile 'MISSING-LICENSE'
    $missingLicenseResult = Invoke-SyncProbe -ManifestPath $missingLicenseManifest -TargetRoot $fixtureRoot
    Assert-FailingProbe -Result $missingLicenseResult `
        -ExpectedFragments @('fixture', $fixtureCommit, "licenseFile 'MISSING-LICENSE'", 'blob match') `
        -CaseName 'missing license'

    $longTargetRoot = Join-Path $systemTempRoot ('mrt-long-' + ('x' * 100))
    if (Test-Path -LiteralPath $longTargetRoot) {
        throw "Long-path fixture unexpectedly exists: '$longTargetRoot'."
    }
    $longPathResult = Invoke-SyncProbe -ManifestPath $healthyManifest -TargetRoot $longTargetRoot -Acquire
    Assert-FailingProbe -Result $longPathResult `
        -ExpectedFragments @('at most 80 characters', 'No files were created') `
        -CaseName 'excessively long TargetRoot'
    if (Test-Path -LiteralPath $longTargetRoot) {
        throw "Long TargetRoot preflight wrote to '$longTargetRoot'."
    }

    $finalFixtureHead = (& git -C $fixtureSource rev-parse HEAD).Trim()
    $finalFixtureStatus = @(& git -C $fixtureSource status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $finalFixtureHead -cne $fixtureCommit -or $finalFixtureStatus.Count -ne 0) {
        throw 'Regression probes mutated the fixture source checkout.'
    }

    $finalMapHead = (& git -C $repoRoot rev-parse HEAD).Trim()
    $finalMapStatus = @(& git -C $repoRoot status --porcelain=v2)
    if ($LASTEXITCODE -ne 0 -or $finalMapHead -cne $initialMapHead -or
        ($finalMapStatus -join "`n") -cne ($initialMapStatus -join "`n")) {
        throw 'Regression probes mutated the Map source repository.'
    }
    Write-Host (
        "ResearchLab regression harness: PASS (5 cases; Map HEAD/status $finalMapHead unchanged by sync probes).")
}
finally {
    if ($null -ne $fixtureRoot -and (Test-Path -LiteralPath $fixtureRoot)) {
        $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\')
        $fixtureLeaf = [System.IO.Path]::GetFileName($resolvedFixtureRoot)
        if (-not $resolvedFixtureRoot.StartsWith("$systemTempRoot\", [StringComparison]::OrdinalIgnoreCase) -or
            $fixtureLeaf -notmatch '^mrt-[0-9a-f]{8}$') {
            throw "Refusing to remove unexpected fixture path '$resolvedFixtureRoot'."
        }
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
