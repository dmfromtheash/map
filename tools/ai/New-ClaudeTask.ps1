[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Opus', 'Fable')][string]$Model,
    [int]$Stage = 0,
    [string]$Task,
    [string]$ProjectRoot = ''
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Join-Path $PSScriptRoot '../..' }
$context = & (Join-Path $PSScriptRoot 'Get-MapContext.ps1') -ProjectRoot $ProjectRoot
$profiles = Get-Content -LiteralPath (Join-Path $context.root 'docs/ai/claude-models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$profile = @($profiles.profiles | Where-Object key -EQ $Model.ToLowerInvariant())
if ($profile.Count -ne 1) { throw "No unique model profile for $Model" }
$profile = $profile[0]
$roadmap = Get-Content -LiteralPath (Join-Path $context.root 'docs/06_DEVELOPMENT_ROADMAP.md') -Raw -Encoding UTF8
$stageContext = ''
if ($Stage -ne 0) {
    $section = [regex]::Match($roadmap, "(?m)^### Stage $Stage —[^\r\n]*\r?\n[\s\S]*?(?=^#{1,3} |\z)")
    if (-not $section.Success) { throw "Stage $Stage has no task section in the current roadmap." }
    $stageContext = $section.Value.Trim()
    $objective = "Реализуй этап $Stage из текущей дорожной карты до пользовательского результата и предусмотренных проверок. Следующие этапы не начинай."
} else {
    if ([string]::IsNullOrWhiteSpace($context.nextAction) -and [string]::IsNullOrWhiteSpace($Task)) {
        throw 'Current handoff has no explicit next action. Specify -Task or update the handoff.'
    }
    $objective = "Продолжи текущую работу из верхнего раздела docs/STATE_HANDOFF.md. $($context.nextAction)"
}
if (-not [string]::IsNullOrWhiteSpace($Task)) { $objective = $Task }
$changes = if ($context.changes.Count -eq 0) { 'На момент подготовки рабочая папка чистая.' } else {
    "Существующие изменения на момент подготовки (сохрани, изучи diff):`n" + ($context.changes -join "`n")
}
$strategy = ($profile.guidance | ForEach-Object { "- $_" }) -join "`n"
@"
Среда: Claude Code, локальный Windows-проект Map.
Выбранная модель: $($profile.name), Anthropic model ID: $($profile.modelId).
Имя в этом промпте не переключает модель. Сверь фактический выбор в интерфейсе/статусе Claude.
Рабочая папка: $($context.root)
Снимок при подготовке: $($context.branch), HEAD $($context.head).
Повторно проверь Git перед правкой; этот снимок не является командой отката.

Цель
$objective

Контекст
- Прочитай CLAUDE.md, AGENTS.md, CODEX.md и верхний актуальный раздел docs/STATE_HANDOFF.md.
- Текущий раздел handoff: $($context.handoffHeading)
- Прочитай относящиеся решения, спецификацию и раздел docs/06_DEVELOPMENT_ROADMAP.md.
- Следуй docs/ai/CLAUDE_WORKFLOW.md и профильным навыкам из .claude/skills/ по смыслу задачи.
$changes

Подход для выбранной модели
$strategy

Границы и результат
- Сохрани прежние этапы, последующие исправления, пользовательские объекты и изменения.
- Не закрывай приложения и реальную карту владельца. Проверяй в собственном изолированном окне.
- Редактирование undoable, сохранение атомарное; renderer не меняет документ, Core независим от UI.
- Доведи разрешённую цель до работающего результата. Минимальные проверки — по AGENTS.md.
- Для внешнего вида покажи настоящий холст и сопоставь с образцами; тесты не заменяют сравнение.
- Используй фактически доступные инструменты. Подборка репозиториев не означает их установку.
- Не начинай соседние этапы. Этот промпт сам по себе не разрешает commit, push или публикацию;
  учитывай прямые разрешения владельца в той сессии, где он поручит работу.

Финал
Обнови существующую документацию по фактическим изменениям. Коротко объясни результат,
проверки и оставшиеся ограничения. По AGENTS.md заверши разделом ИТОГИ:
Сделано; Что дальше; Реализация проекта — округлённая оценка всей текущей roadmap
с явным основанием. Не подменяй общую готовность процентом текущей задачи или тестов.
"@
if ($stageContext) { "`nПорученный раздел дорожной карты (перепроверь в файле перед реализацией)`n$stageContext" }
