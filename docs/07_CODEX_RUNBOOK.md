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

## Работа с публичным исходным кодом

Публичный код разрешено и, для зрелого аналога рискованной функции, ожидается читать глубже
документации. Цель — использовать накопленный инженерный опыт, не переносить чужую подсистему.

Ограниченный reference-проход:

1. Сформулировать один вопрос реализации: например, rounding hex boundary, viewport culling или
   update rollback.
2. Выбрать минимальный набор относящихся upstream-файлов/тестов и зафиксировать repository, exact
   commit, license и admission из `docs/research/external-sources.json`.
3. Прочитать реализацию; выписать формулы, структуры данных, порядок операций, failure cases и
   контрпримеры. Допустимо сопоставить два upstream, если они дают независимую проверку.
4. Спроектировать собственное решение в инвариантах Map. Не делать механический перевод и не
   сохранять уникальные имена, комментарии, fixtures или структуру метода без direct-reuse admission.
5. Написать собственный oracle/fixture и проверить наблюдаемое свойство, а не сходство строк кода.
6. В отчёте указать прочитанные файлы, использованное понимание, собственную реализацию и причину
   отказа от package/direct reuse.

Reference-проход входит в scope функции и verification budget. Он не разрешает автоматически
собирать/исполнять upstream, сканировать все 15 checkouts или добавлять dependency. Для docs-only
правки достаточно document/link/prompt и Git checks; build/test/Stryker/native UI пропускаются как
не относящиеся к изменённому риску.

## Один рабочий цикл

1. Установить факты: branch, `HEAD`, worktree, handoff, current roadmap stage, accepted decisions и существующие пользовательские изменения.
2. Создать или проверить один prompt по шаблону `docs/prompts/PROMPT_TEMPLATE_CODEX.md`.
3. Codex-исполнитель возвращает короткий план checkpoint'ов и матрицу инструментов, затем работает автономно внутри scope.
4. На каждом checkpoint'е он получает локальное доказательство в пределах заранее заданного verification budget; после последнего один раз запускает относящийся к риску self-check этапа.
5. Если prompt разрешает commit, создаётся один commit полного этапа. Серия промежуточных commits допустима только по прямому указанию владельца; история не переписывается.
6. Исполнитель сообщает `BASE`/`HEAD`, фактический статус и останавливается.
7. Свежая задача Codex читает весь `BASE..HEAD`, пересчитывает файлы, выполняет риск-ориентированные gates и проверяет настоящее окно/артефакт.
8. Найденные дефекты получают отдельный fix-prompt и новый commit без `--amend`/rebase.
9. На новом `HEAD` повторяются только gates, чьи доказательства затронуты fix-изменением, плюс один соответствующий regression boundary; уже зелёные независимые доказательства неизменённых областей не воспроизводятся «для уверенности».
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
10. **Public source reference plan** — exact upstream/files и один вопрос, либо явное `N/A`.
11. **Verification budget and stop rules** — по одному необходимому прогону, условия единственного повтора, flake-limit и точка остановки.
12. **Manual/native UI scenario** — если меняется UI/canvas; иначе явное `N/A` с причиной.
13. **Git boundary** — branch/base, разрешение или запрет commit/push/PR/release.
14. **Required report** — факты, числа, файлы, evidence, gaps, риски и следующая задача.

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
| внешний алгоритмический reference | read-only exact-commit source review | файлы, понятые решения, собственный oracle | нет зрелого аналога или алгоритмической неопределённости |

## Конечный verification budget

- Один успешно пройденный command/gate выполняется не более одного раза на неизменившемся снимке и в одной роли (`author self-check` или `fresh review`).
- Красный targeted test повторяется после относящегося изменения; зелёный targeted test и содержащий его test project запускаются по одному разу. Полный solution suite нужен только при изменении production/shared harness/solution boundary либо при доказанном межпроектном риске.
- Coverage **или** mutation выбирается по риску assertions. Оба инструмента нужны только когда они доказывают две разные заранее названные гипотезы; «на всякий случай» запускать оба нельзя.
- Flake/timing/concurrency допускает максимум три последовательных успешных прогона, если риск и требуемое число были записаны до запуска. Детерминированный тест не повторяется серией.
- После двух одинаковых неуспешных результатов, несмотря на целевые изменения, запрещён слепой третий retry: нужно сменить диагностический подход либо завершить ветвь как `PARTIAL`/`BLOCKED`.
- Независимый review читает весь exact diff, но исполняет только high-risk path, один относящийся regression boundary и risk-triggered gates. Он не воспроизводит весь author self-check.
- Итоговый отчёт перечисляет выполненный бюджет, каждый пропуск и stop condition. Эти правила конкретизированы в `docs/11_CODEX_VERIFICATION_PROTOCOL.md` и не дублируются расширенными вариантами в stage prompt.

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
