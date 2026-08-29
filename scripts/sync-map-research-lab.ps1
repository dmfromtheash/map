[CmdletBinding()]
param(
    [string]$TargetRoot,
    [switch]$Acquire
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'docs/research/external-sources.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    $TargetRoot = [string]$manifest.defaultLabRoot
}

$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
$targetRootFull = [System.IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
if ($targetRootFull.Equals($repoRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $targetRootFull.StartsWith("$repoRootFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ResearchLab must stay outside the Map repository.'
}

$sourcesRoot = Join-Path $targetRootFull 'sources'
if ($Acquire) {
    New-Item -ItemType Directory -Path $sourcesRoot -Force | Out-Null
}

$errors = [System.Collections.Generic.List[string]]::new()
$rows = foreach ($repository in $manifest.repositories) {
    $id = [string]$repository.id
    if ($id -notmatch '^[a-z0-9-]+$') {
        throw "Unsafe repository id in manifest: '$id'."
    }
    $url = [string]$repository.url
    $commit = [string]$repository.commit
    if ($url -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git$') {
        throw "${id}: only an explicit GitHub HTTPS repository URL is allowed."
    }
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        throw "${id}: manifest commit must be a full lowercase SHA-1."
    }
    foreach ($sparsePath in @($repository.sparsePaths)) {
        $candidatePath = [string]$sparsePath
        if ([string]::IsNullOrWhiteSpace($candidatePath) -or
            [System.IO.Path]::IsPathRooted($candidatePath) -or
            $candidatePath -match '(^|[\\/])\.\.([\\/]|$)' -or
            $candidatePath.Contains(':')) {
            throw "${id}: unsafe sparse path '$candidatePath'."
        }
    }

    $path = Join-Path $sourcesRoot $id
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $Acquire) {
            $errors.Add("${id}: missing (run with -Acquire to create pinned checkout)")
            [pscustomobject]@{ id = $id; status = 'MISSING'; commit = ''; license = $repository.license }
            continue
        }

        New-Item -ItemType Directory -Path $path -Force:$false | Out-Null
        & git -C $path init --quiet
        & git -C $path remote add origin $url
        & git -C $path sparse-checkout init --cone
        & git -C $path sparse-checkout set -- @($repository.sparsePaths)
        & git -C $path fetch --depth 1 --filter=blob:none origin $commit
        if ($LASTEXITCODE -ne 0) {
            throw "${id}: fetch failed; partial checkout was preserved at $path."
        }
        & git -C $path checkout --detach --quiet $commit
        if ($LASTEXITCODE -ne 0) {
            throw "${id}: checkout failed; partial checkout was preserved at $path."
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) {
        $errors.Add("${id}: target exists but is not a Git checkout")
        [pscustomobject]@{ id = $id; status = 'INVALID'; commit = ''; license = $repository.license }
        continue
    }

    $actualOrigin = (& git -C $path remote get-url origin).Trim()
    $actualCommit = (& git -C $path rev-parse HEAD).Trim()
    $dirty = @(& git -C $path status --porcelain)
    $status = 'PASS'
    if ($actualOrigin -ne $url) {
        $errors.Add("${id}: origin mismatch ('$actualOrigin')")
        $status = 'ORIGIN_MISMATCH'
    }
    if ($actualCommit -ne $commit) {
        $errors.Add("${id}: commit mismatch ('$actualCommit')")
        $status = 'COMMIT_MISMATCH'
    }
    if ($dirty.Count -gt 0) {
        $errors.Add("${id}: checkout is dirty; refusing reset or update")
        $status = 'DIRTY'
    }

    [pscustomobject]@{
        id = $id
        status = $status
        commit = $actualCommit
        license = [string]$repository.license
    }
}

$rows | Format-Table -AutoSize
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "ResearchLab verification: PASS ($($rows.Count) pinned public repositories at $targetRootFull)."
