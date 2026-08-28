# 07. Runbook для Codex GPT-5.6 Sol

## Роли и независимость

| Роль | Обязанность |
|---|---|
| Владелец | принимает продуктовые, визуальные, лицензионные, внешние и необратимые решения |
| Codex-исполнитель (`gpt-5.6-sol`, `high`) | закрывает один полный вертикальный этап, создаёт код/tests/evidence и честно фиксирует границы доказанного |
| Codex-ревьюер в свежей задаче (`gpt-5.6-sol`, `xhigh`) | независимо проверяет точный Git-снимок и реальный пользовательский результат; не переиспользует self-check как своё доказательство |

Одна и та же модель допустима для реализации и review, но контекст review должен быть свежим. Проверка в контексте автора полезна как self-check, но не независима.

## Что считается крупным рабочим блоком

Обычная единица — один целостный roadmap-этап с одним наблюдаемым пользовательским outcome и полным acceptance-пакетом. Prompt может затрагивать несколько слоёв (`Core`, `Persistence`, `Rendering`, `Presentation`, `App`, tests и drivers), если они нужны этому outcome.

Не нужно останавливаться после каждой модели, команды или панели. Внутри этапа Codex ведёт короткие checkpoint'ы, но доходит до полного результата или честного `PARTIAL/BLOCKED`.

Границы большого блока:

- не смешивать соседние этапы с разными продуктовым смыслом и gates;
- не включать найденный по пути рефакторинг;
- не превращать цель в «весь Inkarnate»;
- отдельный review-fix остаётся узким: один подтверждённый дефект, red/green и новый commit.

## Один рабочий цикл

1. Установить факты: branch, `HEAD`, worktree, handoff, current roadmap stage, accepted decisions и существующие пользовательские изменения.
2. Создать или проверить один prompt по шаблону `docs/prompts/PROMPT_TEMPLATE_CODEX.md`.
3. Codex-исполнитель возвращает короткий план checkpoint'ов и матрицу инструментов, затем работает автономно внутри scope.
4. На каждом checkpoint'е он получает локальное доказательство; после последнего запускает полный self-check этапа.
5. Если prompt разрешает commit, создаётся один commit полного этапа. Серия промежуточных commits допустима только по прямому указанию владельца; история не переписывается.
6. Исполнитель сообщает `BASE`/`HEAD`, фактический статус и останавливается.
7. Свежая задача Codex читает весь `BASE..HEAD`, пересчитывает файлы, выполняет риск-ориентированные gates и проверяет настоящее окно/артефакт.
8. Найденные дефекты получают отдельный fix-prompt и новый commit без `--amend`/rebase.
9. На новом `HEAD` повторяется весь относящийся к этапу gate.
10. Только после `FACT` обновляются handoff и статус этапа.

## Обязательная структура этапного промта

Каждый новый prompt содержит:

1. **Execution profile** — `gpt-5.6-sol`, reasoning, роль, автономность и Git-authority.
2. **Source of truth** — обязательные документы, accepted DEC и base commit.
3. **Outcome** — один наблюдаемый результат полного этапа.
4. **Current facts** — что уже существует и какие gaps закрываются.
5. **In scope / Out of scope** — функциональные и файловые границы.
6. **Invariants** — архитектура, формат, offline, undo/redo, licensing и preservation.
7. **Internal checkpoints** — 3–7 логических контрольных точек до общего результата.
8. **Acceptance criteria** — нумерованные проверяемые критерии, включая failure paths.
9. **Risk and tool matrix** — для каждого риска инструмент, evidence и условие обоснованного пропуска.
10. **Manual/native UI scenario** — если меняется UI/canvas; иначе явное `N/A` с причиной.
11. **Git boundary** — branch/base, разрешение или запрет commit/push/PR/release.
12. **Required report** — факты, числа, файлы, evidence, gaps, риски и следующая задача.

Prompt не должен многократно повторять общие запреты из `AGENTS.md`. Ссылка на источник плюс одно этапное уточнение лучше длинной цепочки одинаковых предостережений.

## Матрица выбора инструментов

Минимальный пример, который конкретизируется под этап:

| Риск этапа | Инструмент | Требуемое доказательство | Когда можно пропустить |
|---|---|---|---|
| компиляция/регрессия | `dotnet build/test/format` | exit code, test counts, 0 warnings/errors | практически никогда для code stage |
| повреждение границ repo | Git inventory + `pre-commit` | `BASE..HEAD`, A/M/D/R, hook PASS | без commit — только commit-specific часть |
| слабые assertions | targeted coverage / Stryker | покрытая ветвь; survivors разобраны | нет изменённой критической логики |
| persistence/import/archive | adversarial fixtures + semantic roundtrip | corruption/limits/path/atomicity results | этап не касается файлов/парсинга |
| performance | benchmark + counters/trace/GC | warmup, median/P95, allocations/trace | нет performance claim/бюджета |
| UI/canvas | Release EXE + UIA driver + screenshots/vision | сценарий, checks, PNG review | UI не меняется |
| visual regression | ImageMagick | fixed-state RMSE и diff | нет сравнимого reference/state |
| accessibility | UIA + Accessibility Insights | labels/focus/tab/contrast | нет новых/изменённых controls |
| dependency/supply chain | NuGet audit + source/license review | package graph/advisories/license | нет зависимостей; NuGet audit всё равно остаётся базовой гигиеной |
| security boundary | Codex Security / targeted review | source-to-sink и controls | нет нового boundary и нет явного запроса владельца |

## Когда Codex спрашивает владельца

Спросить до реализации нужно, если:

- выбор меняет accepted DEC, формат файла, поддерживаемую ОС или offline-инвариант;
- нужна новая dependency, платная лицензия, аккаунт, секрет или внешний сервис, а решение не принято;
- требуется публикация, push, PR, release, отправка сообщения или другая внешняя мутация;
- надо удалить материал владельца, переписать историю или расширить scope до соседнего этапа;
- критерии конфликтуют так, что разумное допущение изменит продукт.

Не нужно спрашивать повторно о чтении файлов, запуске безопасных локальных проверок и обратимых изменениях, прямо необходимых утверждённому scope.

## Формат итогового отчёта исполнителя

```markdown
## RESULT
FACT / PARTIAL / PROPOSED / BLOCKED — один абзац о наблюдаемом результате.

## CHANGED
- путь — назначение и связь с acceptance.

## VERIFIED AUTOMATICALLY
- команда — exit code, ключевые числа и что именно доказано.

## VERIFIED IN NATIVE UI
- шаги в настоящем Release-окне, process identity и наблюдаемый результат; либо N/A.

## TOOL MATRIX RESULT
- инструмент — used/skipped; evidence или конкретная причина.

## NOT VERIFIED
- что не доказано и почему.

## RISKS
- конкретный остаточный риск и его impact.

## GIT / SCOPE
- branch, BASE, HEAD, worktree, A/M/D/R, подтверждение out-of-scope.

## NEXT
- один точный следующий roadmap-этап или review-fix, не выполненный самовольно.
```

## Минимум независимой приёмки

Ревьюер:

- проверяет полный diff и authored-file inventory, а не только summary;
- сверяет acceptance по пунктам и отделяет факты автора от свежих фактов review;
- проверяет happy path и самый опасный failure path;
- открывает созданный `.mapproj`/export/PNG и проверяет содержимое, а не toast;
- запускает настоящее Release-окно для UI/canvas-изменения;
- фиксирует неисполненные gates как `PARTIAL` или `BLOCKED`;
- не исправляет production-код внутри review без отдельного разрешённого fix-среза.
