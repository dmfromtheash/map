[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Opus', 'Fable')][string]$Model,
    [ValidatePattern('^(R\d\d[a-z]?)?$')][string]$Stage = '',
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
$roadmap = Get-Content -LiteralPath (Join-Path $context.root 'docs/ROADMAP_2026-10_PRODUCT.md') -Raw -Encoding UTF8
$stageContext = ''
if ($Stage) {
    $section = [regex]::Match($roadmap, "(?m)^#### $Stage ·[^\r\n]*\r?\n[\s\S]*?(?=^#{1,4} |\z)")
    if (-not $section.Success) { throw "Stage $Stage has no section in docs/ROADMAP_2026-10_PRODUCT.md." }
    $stageContext = $section.Value.Trim()
    $objective = "Реализуй этап $Stage дорожной карты до пользовательского результата и его пунктов «Готово, когда». Следующие этапы не начинай."
} else {
    if ([string]::IsNullOrWhiteSpace($context.nextAction) -and [string]::IsNullOrWhiteSpace($Task)) {
        throw 'docs/STATE.md has no next step. Specify -Task or -Stage, or update docs/STATE.md.'
    }
    $objective = "Следующий шаг из docs/STATE.md: $($context.nextAction)"
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
- Прочитай docs/STATE.md (обновлено: $($context.stateUpdated)) и выполни чек-лист docs/00_STAGE_PROTOCOL.md.
- AGENTS.md подключён через CLAUDE.md. Относящиеся решения, спецификацию и журнал — только по теме.
- Следуй профильным навыкам из .claude/skills/ по смыслу задачи.
$changes

Подход для выбранной модели
$strategy

Границы и результат
- Сохрани прежние этапы, последующие исправления, пользовательские объекты и изменения.
- Не закрывай приложения и реальную карту владельца. Проверяй в собственном изолированном окне.
- Редактирование undoable, сохранение атомарное; renderer не меняет документ, Core независим от UI.
- Доведи разрешённую цель до работающего результата. Минимальные проверки — по AGENTS.md.
- Для внешнего вида покажи настоящий холст и сопоставь с образцами; тесты не заменяют сравнение.
- Используй фактически доступные инструменты.
- Не начинай соседние этапы. Commit и push — по AGENTS.md §Git после проверенного этапа.

Финал
Запись в docs/journal, переписанный docs/STATE.md, отчёт по docs/00_STAGE_PROTOCOL.md. Заверши разделом ИТОГИ:
Сделано; Что дальше; Реализация проекта — округлённая оценка всей текущей roadmap
с явным основанием. Не подменяй общую готовность процентом текущей задачи или тестов.
"@
if ($stageContext) { "`nПорученный раздел дорожной карты (перепроверь в файле перед реализацией)`n$stageContext" }
