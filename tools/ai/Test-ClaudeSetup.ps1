[CmdletBinding()]
param([string]$ProjectRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$context = & (Join-Path $PSScriptRoot 'Get-MapContext.ps1') -ProjectRoot $ProjectRoot
$root = $context.root
$required = @('CLAUDE.md', 'docs/ai/CLAUDE_WORKFLOW.md', 'docs/ai/PROMPT_CLAUDE.md',
    'docs/ai/RESEARCH_AND_REPOSITORIES.md', 'tools/ai/New-ClaudeTask.ps1', 'tools/ai/Get-RepositoryStatus.ps1')
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { throw "Missing $path" }
}
$models = Get-Content -LiteralPath (Join-Path $root 'docs/ai/claude-models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($key in @('opus', 'fable')) {
    $model = @($models.profiles | Where-Object key -EQ $key)
    if ($model.Count -ne 1 -or -not $model[0].modelId -or $model[0].guidance.Count -eq 0) { throw "Invalid profile: $key" }
}
$skills = @(Get-ChildItem -LiteralPath (Join-Path $root '.claude/skills') -Filter SKILL.md -Recurse -File)
if ($skills.Count -eq 0) { throw 'No project skills found.' }
foreach ($skill in $skills) {
    $content = Get-Content -LiteralPath $skill.FullName -Raw -Encoding UTF8
    $front = [regex]::Match($content, '\A---\r?\n([\s\S]*?)\r?\n---')
    if (-not $front.Success) { throw "Missing skill frontmatter: $($skill.FullName)" }
    $name = [regex]::Match($front.Groups[1].Value, '(?m)^name: (.+)$').Groups[1].Value.Trim()
    if ($name -ne $skill.Directory.Name -or $name -notmatch '^[a-z0-9-]+$' -or
        $front.Groups[1].Value -notmatch '(?m)^description: .+') { throw "Invalid skill metadata: $($skill.FullName)" }
}
$mcp = Get-Content -LiteralPath (Join-Path $root '.mcp.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($mcp.mcpServers.'microsoft-learn'.type -ne 'http' -or
    ([uri]$mcp.mcpServers.'microsoft-learn'.url).Host -ne 'learn.microsoft.com') { throw 'Unexpected Learn configuration.' }
$repos = Get-Content -LiteralPath (Join-Path $root 'docs/ai/repository-candidates.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($repos.repositories.repository | Select-Object -Unique).Count -ne $repos.repositories.Count) { throw 'Duplicate repositories.' }
foreach ($repo in $repos.repositories) {
    if ($repo.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name.' }
}
[pscustomobject]@{ status='PASS'; skills=$skills.Count; models=$models.profiles.Count; repositories=$repos.repositories.Count; scope='Local files and syntax; no model execution or MCP trust approval.' }
