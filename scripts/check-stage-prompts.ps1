[CmdletBinding()]
param(
    [string]$PromptDirectory = "docs/prompts",
    [switch]$RequireActive
)

$ErrorActionPreference = "Stop"
$requiredMarkers = @(
    "gpt-5.6-sol",
    "AGENTS.md",
    "CODEX.md",
    "docs/07_CODEX_RUNBOOK.md",
    "docs/11_CODEX_VERIFICATION_PROTOCOL.md",
    "## Execution profile",
    "## Source of truth and baseline",
    "## Outcome",
    "## Current facts and gaps",
    "## In scope",
    "## Out of scope",
    "## Invariants",
    "## Internal checkpoints",
    "## Acceptance criteria",
    "## Failure and adversarial scenarios",
    "## Risk and tool matrix",
    "## Required automatic verification",
    "## Required native UI verification",
    "## Git and stop condition",
    "## Required report"
)

$promptFiles = Get-ChildItem -LiteralPath $PromptDirectory -File -Filter "*.md" |
    Where-Object { $_.Name -notin @('README.md', 'PROMPT_TEMPLATE_CODEX.md') }
$active = @($promptFiles | Where-Object {
    (Get-Content -Raw -LiteralPath $_.FullName) -match '(?im)^> Status:\s*ACTIVE\s*$'
})

if ($RequireActive -and $active.Count -eq 0) {
    throw "No ACTIVE stage prompt found in $PromptDirectory."
}

$errors = [System.Collections.Generic.List[string]]::new()
foreach ($file in $active) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($marker in $requiredMarkers) {
        if ($content.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $errors.Add("$($file.Name): missing '$marker'")
        }
    }

    if ($content -match '(?i)\b(Claude Code|Opus)\b|CLAUDE\.md|07_OPUS_RUNBOOK|11_CLAUDE_VERIFICATION_PROTOCOL') {
        $errors.Add("$($file.Name): contains a legacy Claude/Opus instruction")
    }

    foreach ($toolRow in @("compile and regression", "repository boundary", "dependency graph", "critical branch assertions", "native behavior", "security/supply chain")) {
        if ($content.IndexOf($toolRow, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $errors.Add("$($file.Name): risk/tool matrix is missing '$toolRow'")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Stage prompt contract: PASS ($($active.Count) active prompt(s))."
