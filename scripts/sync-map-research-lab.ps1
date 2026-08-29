[CmdletBinding()]
param(
    [string]$TargetRoot,
    [switch]$Acquire,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'docs/research/external-sources.json'
}

$manifestPathFull = [System.IO.Path]::GetFullPath($ManifestPath)
$manifest = Get-Content -Raw -LiteralPath $manifestPathFull | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    $TargetRoot = [string]$manifest.defaultLabRoot
}

function Test-ExactMaterializedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CheckoutPath,
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $currentPath = $CheckoutPath
    foreach ($segment in $RelativePath.Split('/')) {
        if (-not [System.IO.Directory]::Exists($currentPath)) {
            return $false
        }

        $exactEntry = $null
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($currentPath)) {
            if ([System.IO.Path]::GetFileName($entry).Equals($segment, [StringComparison]::Ordinal)) {
                $exactEntry = $entry
                break
            }
        }
        if ($null -eq $exactEntry) {
            return $false
        }
        $currentPath = $exactEntry
    }

    return [System.IO.File]::Exists($currentPath)
}

function Get-PinnedTreeValidationErrors {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CheckoutPath,
        [Parameter(Mandatory = $true)]
        [string]$Id,
        [Parameter(Mandatory = $true)]
        [string]$Commit,
        [Parameter(Mandatory = $true)]
        [string[]]$SparsePaths,
        [Parameter(Mandatory = $true)]
        [string]$LicenseFile
    )

    $validationErrors = [System.Collections.Generic.List[string]]::new()
    $treeFiles = @(& git -c core.quotepath=false -C $CheckoutPath ls-tree -r --name-only $Commit)
    if ($LASTEXITCODE -ne 0) {
        $validationErrors.Add((
                "{0}: pinned SHA {1}: unable to read the exact Git tree." -f $Id, $Commit))
        return $validationErrors.ToArray()
    }

    $fileEntries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $treeEntries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($treeFileValue in $treeFiles) {
        $treeFile = [string]$treeFileValue
        [void]$fileEntries.Add($treeFile)
        [void]$treeEntries.Add($treeFile)
        $segments = $treeFile.Split('/')
        for ($segmentCount = 1; $segmentCount -lt $segments.Length; $segmentCount++) {
            [void]$treeEntries.Add(($segments[0..($segmentCount - 1)] -join '/'))
        }
    }

    foreach ($sparsePath in $SparsePaths) {
        if (-not $treeEntries.Contains($sparsePath)) {
            $validationErrors.Add((
                    "{0}: pinned SHA {1}: sparse path '{2}' has no exact case-sensitive tree match." -f
                    $Id, $Commit, $sparsePath))
        }
    }

    if (-not $fileEntries.Contains($LicenseFile)) {
        $validationErrors.Add((
                "{0}: pinned SHA {1}: licenseFile '{2}' has no exact case-sensitive blob match." -f
                $Id, $Commit, $LicenseFile))
    }
    else {
        $licenseType = (& git -C $CheckoutPath cat-file -t "${Commit}:$LicenseFile").Trim()
        if ($LASTEXITCODE -ne 0 -or $licenseType -cne 'blob') {
            $validationErrors.Add((
                    "{0}: pinned SHA {1}: licenseFile '{2}' is not an available Git blob." -f
                    $Id, $Commit, $LicenseFile))
        }
    }

    return $validationErrors.ToArray()
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CheckoutPath,
        [Parameter(Mandatory = $true)]
        [string[]]$GitArguments,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & git -C $CheckoutPath @GitArguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
$targetRootFull = [System.IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
$targetPathRoot = [System.IO.Path]::GetPathRoot($targetRootFull).TrimEnd('\')
if ($targetRootFull.Equals($targetPathRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ResearchLab TargetRoot must not be a drive root: '$targetRootFull'."
}

$maximumTargetRootLength = 80
if ($targetRootFull.Length -gt $maximumTargetRootLength) {
    throw (
        "ResearchLab TargetRoot is $($targetRootFull.Length) characters; Windows acquisition requires " +
        "a short TargetRoot of at most $maximumTargetRootLength characters. No files were created.")
}

if ($targetRootFull.Equals($repoRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $targetRootFull.StartsWith("$repoRootFull\", [StringComparison]::OrdinalIgnoreCase) -or
    $repoRootFull.StartsWith("$targetRootFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ResearchLab must not overlap the Map repository.'
}

$sourcesRoot = Join-Path $targetRootFull 'sources'
if ($Acquire) {
    New-Item -ItemType Directory -Path $sourcesRoot -Force | Out-Null
}

$errors = [System.Collections.Generic.List[string]]::new()
$rows = @(foreach ($repository in @($manifest.repositories)) {
    $id = [string]$repository.id
    $url = [string]$repository.url
    $commit = [string]$repository.commit
    $licenseFile = ([string]$repository.licenseFile).Replace('\', '/').TrimEnd('/')

    if ($id -notmatch '^[a-z0-9-]+$') {
        throw "Unsafe repository id in manifest: '$id'."
    }
    if ($url -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git$') {
        throw "${id}: only an explicit GitHub HTTPS repository URL is allowed."
    }
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        throw "${id}: manifest commit must be a full lowercase SHA-1."
    }

    $sparsePaths = @()
    foreach ($sparsePathValue in @($repository.sparsePaths)) {
        $candidatePath = ([string]$sparsePathValue).Replace('\', '/').TrimEnd('/')
        if ([string]::IsNullOrWhiteSpace($candidatePath) -or
            [System.IO.Path]::IsPathRooted($candidatePath) -or
            $candidatePath -match '(^|/)\.\.(/|$)' -or
            $candidatePath.Contains(':')) {
            throw ("{0}: pinned SHA {1}: unsafe sparse path '{2}'." -f
                $id, $commit, $candidatePath)
        }
        $sparsePaths += $candidatePath
    }
    if ($sparsePaths.Count -eq 0) {
        throw "${id}: pinned SHA ${commit}: at least one sparse path is required."
    }
    if ([string]::IsNullOrWhiteSpace($licenseFile) -or
        [System.IO.Path]::IsPathRooted($licenseFile) -or
        $licenseFile -match '(^|/)\.\.(/|$)' -or
        $licenseFile.Contains(':')) {
        throw "${id}: pinned SHA ${commit}: unsafe licenseFile '$licenseFile'."
    }

    $licenseIsSparse = $false
    foreach ($sparsePath in $sparsePaths) {
        if ($sparsePath.Equals($licenseFile, [StringComparison]::Ordinal)) {
            $licenseIsSparse = $true
            break
        }
    }
    if (-not $licenseIsSparse) {
        throw "${id}: pinned SHA ${commit}: licenseFile '$licenseFile' must be an exact declared sparse path."
    }

    $path = Join-Path $sourcesRoot $id
    $treeWasValidated = $false
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $Acquire) {
            $errors.Add("${id}: missing (run with -Acquire to create pinned checkout)")
            [pscustomobject]@{ id = $id; status = 'MISSING'; commit = ''; license = $repository.license }
            continue
        }

        New-Item -ItemType Directory -Path $path -Force:$false | Out-Null
        Invoke-GitChecked -CheckoutPath $path -GitArguments @('init', '--quiet') `
            -FailureMessage "${id}: git init failed; partial checkout was preserved at $path."
        Invoke-GitChecked -CheckoutPath $path -GitArguments @('config', 'core.longpaths', 'true') `
            -FailureMessage "${id}: core.longpaths configuration failed at $path."
        Invoke-GitChecked -CheckoutPath $path -GitArguments @('remote', 'add', 'origin', $url) `
            -FailureMessage "${id}: remote configuration failed at $path."
        Invoke-GitChecked -CheckoutPath $path -GitArguments @('sparse-checkout', 'init', '--cone') `
            -FailureMessage "${id}: sparse-checkout initialization failed at $path."
        Invoke-GitChecked -CheckoutPath $path `
            -GitArguments (@('fetch', '--depth', '1', '--filter=blob:none', 'origin', $commit)) `
            -FailureMessage "${id}: fetch failed; partial checkout was preserved at $path."

        $newTreeErrors = @(Get-PinnedTreeValidationErrors -CheckoutPath $path -Id $id `
            -Commit $commit -SparsePaths $sparsePaths -LicenseFile $licenseFile)
        if ($newTreeErrors.Count -gt 0) {
            foreach ($treeError in $newTreeErrors) {
                $errors.Add($treeError)
            }
            [pscustomobject]@{ id = $id; status = 'TREE_INVALID'; commit = $commit; license = $repository.license }
            continue
        }
        $treeWasValidated = $true

        Invoke-GitChecked -CheckoutPath $path `
            -GitArguments (@('sparse-checkout', 'set', '--') + $sparsePaths) `
            -FailureMessage "${id}: sparse-checkout set failed; partial checkout was preserved at $path."
        Invoke-GitChecked -CheckoutPath $path -GitArguments @('checkout', '--detach', '--quiet', $commit) `
            -FailureMessage "${id}: checkout failed; partial checkout was preserved at $path."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) {
        $errors.Add("${id}: target exists but is not a Git checkout")
        [pscustomobject]@{ id = $id; status = 'INVALID'; commit = ''; license = $repository.license }
        continue
    }

    $actualOrigin = (& git -C $path remote get-url origin).Trim()
    $originExitCode = $LASTEXITCODE
    $actualCommit = (& git -C $path rev-parse HEAD).Trim()
    $commitExitCode = $LASTEXITCODE
    $dirty = @(& git -C $path status --porcelain)
    $statusExitCode = $LASTEXITCODE
    $status = 'PASS'
    if ($originExitCode -ne 0 -or $actualOrigin -cne $url) {
        $errors.Add("${id}: origin mismatch ('$actualOrigin')")
        $status = 'ORIGIN_MISMATCH'
    }
    if ($commitExitCode -ne 0 -or $actualCommit -cne $commit) {
        $errors.Add("${id}: commit mismatch ('$actualCommit'; expected pinned SHA '$commit')")
        $status = 'COMMIT_MISMATCH'
    }
    if ($statusExitCode -ne 0) {
        $errors.Add("${id}: git status failed")
        $status = 'INVALID'
    }
    elseif ($dirty.Count -gt 0) {
        $errors.Add("${id}: checkout is dirty; refusing reset or update")
        $status = 'DIRTY'
    }

    if (-not $treeWasValidated) {
        $treeErrors = @(Get-PinnedTreeValidationErrors -CheckoutPath $path -Id $id `
            -Commit $commit -SparsePaths $sparsePaths -LicenseFile $licenseFile)
        if ($treeErrors.Count -gt 0) {
            foreach ($treeError in $treeErrors) {
                $errors.Add($treeError)
            }
            $status = 'TREE_INVALID'
        }
    }

    if ($status -ceq 'PASS' -and -not (Test-ExactMaterializedFile -CheckoutPath $path -RelativePath $licenseFile)) {
        $errors.Add((
                "{0}: pinned SHA {1}: licenseFile '{2}' was not materialized as an exact-case file." -f
                $id, $commit, $licenseFile))
        $status = 'LICENSE_MISSING'
    }

    [pscustomobject]@{
        id = $id
        status = $status
        commit = $actualCommit
        license = [string]$repository.license
    }
})

$rows | Format-Table -AutoSize
if ($errors.Count -gt 0) {
    foreach ($errorMessage in $errors) {
        [Console]::Error.WriteLine($errorMessage)
    }
    exit 1
}

Write-Host "ResearchLab verification: PASS ($($rows.Count) pinned public repositories at $targetRootFull)."
