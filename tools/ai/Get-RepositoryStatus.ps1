[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$registryPath = Join-Path $ProjectRoot 'docs/ai/repository-candidates.json'
$registry = Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json
$records = foreach ($candidate in $registry.repositories) {
    if ($candidate.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Invalid repository name: $($candidate.repository)"
    }
    try {
        $repo = Invoke-RestMethod -Uri "https://api.github.com/repos/$($candidate.repository)" `
            -Headers @{ 'User-Agent' = 'Map-Repository-Check' } -TimeoutSec 15
        [pscustomobject]@{
            repository = $repo.full_name
            checkedAtUtc = [DateTime]::UtcNow.ToString('o')
            status = 'READ'
            archived = $repo.archived
            lastPushUtc = $repo.pushed_at
            licenseId = $repo.license.spdx_id
            stages = $candidate.stages
            use = $candidate.use
            url = $repo.html_url
        }
    } catch {
        [pscustomobject]@{
            repository = $candidate.repository
            checkedAtUtc = [DateTime]::UtcNow.ToString('o')
            status = 'UNAVAILABLE'
            error = $_.Exception.Message
        }
    }
}
if ($AsJson) { $records | ConvertTo-Json -Depth 5 } else { $records }
# Metadata only: no clone, install, scheduled polling, credentials, or file writes.
