# 11. Проверка работы Codex GPT-5.6 Sol

## Назначение

Self-check Codex, зелёная сборка и один успешный запуск не равны приёмке. Этап получает статус `FACT` только после проверки точного Git-снимка, риск-ориентированных автоматических gates и наблюдаемого пользовательского результата. Независимая проверка выполняется свежей задачей Codex, а не тем же контекстом реализации.

## Активный стек и доказательная сила

| Средство | Роль | Что доказывает | Ограничение / риск |
|---|---|---|---|
| Codex `gpt-5.6-sol` | реализация (`high`) и свежий review (`xhigh`) | способен исследовать repo, редактировать, запускать локальные проверки и работать с большим контекстом | модель не является источником истины; требуются runtime/file/Git evidence |
| `AGENTS.md` + этапный prompt | стабильные правила и локальный контракт | явно задают outcome, scope, authority и acceptance | дубли и противоречия вызывают лишние остановки; общие правила пишутся один раз |
| Codex skills | специализированные инструкции только по совпадающей задаче | дают проверенный workflow для browser, security, documents и т. п. | skill применяется по trigger; не превращается в обязательную зависимость каждого этапа |
| Codex Browser | публичное исследование и in-app browser | текущая страница, DOM, screenshot и публичные источники | внешний источник не разрешает копировать закрытые данные/дизайн |
| Codex Computer Use | ручной smoke настоящего Windows-окна | видимый пользовательский сценарий в выбранном окне | без воспроизводимого driver/file evidence недостаточно; не давать доступ стороннему MCP |
| Git | граница проверяемого снимка | branch, `BASE..HEAD`, A/M/D/R, worktree и authored inventory | summary без полного diff недостаточен |
| pre-commit `4.6.2` | менеджер быстрых commit hooks | запускает pinned checks до commit | сам устанавливает/исполняет hook code; источники должны быть закреплены и просмотрены |
| pre-commit-hooks `v6.0.0` | файловая гигиена | conflicts, YAML/JSON, symlinks, large files, line endings, branch guard | не проверяет функциональность |
| Lychee `0.24.2` | Markdown links/anchors | отсутствие битых локальных ссылок в offline-gate | сетевой URL может быть временно недоступен или содержательно неверен |
| .NET SDK `10.0.400` | restore/build/test/format/analyzers | compile, test execution, style/analyzer diagnostics | зелёный suite не доказывает покрытие критических ветвей |
| NuGet Audit | известные уязвимости package graph | текущие advisories для direct/transitive dependencies | не доказывает отсутствие неизвестных уязвимостей; требует доступного audit source |
| dotnet-coverage `18.10.0` | targeted execution coverage | какие строки/ветви реально исполнялись | процент не измеряет силу assertions |
| Stryker.NET `4.16.0` | mutation testing | способны ли тесты ловить внесённые ошибки | дорог; запускать на изменённой критической логике и разбирать survivors |
| dotnet-counters/trace/gcdump `9.0.661903` | runtime diagnostics | CPU/GC/allocation/trace evidence процесса | `gcdump` может вызвать full GC; не запускать без performance/memory цели |
| Avalonia Headless | быстрый control/layout/input test без окна | control tree, binding, input и in-memory render | не доказывает native focus, menus, DPI и OS accessibility |
| project-owned UIA drivers | воспроизводимый native E2E | actions/checks в Release EXE через UI Automation | требуют устойчивых AutomationId и интерактивной Windows-сессии |
| Appium/WinAppDriver | опциональный внешний native UI harness | Selenium-подобное управление Windows UI | WinAppDriver стар и требует отдельного процесса; не добавлять без преимущества над project drivers |
| ImageMagick `7.1.2-30` | проверка изображений | dimensions/channels/alpha, normalized RMSE и diff | metric не определяет правильность дизайна; нужны vision/human review и fixed state |
| Accessibility Insights `1.1.2924.1` | accessibility настоящего окна | UIA tree, labels, focus/tab, automated rules, contrast | canvas pixels и бизнес-логика вне области; telemetry отключается настройкой при необходимости |
| FsCheck | property-based tests | множество сгенерированных входов и shrinking контрпримера | требует правильно заданного свойства; сейчас не установлен в project graph |
| ArchUnitNET | architecture tests | запрещённые зависимости между слоями | новая dependency; пока инварианты проверяются project/reference tests |
| Repomix | on-demand context pack | inventory/контекст большого repo для внешнего анализа | дублирует native repo access Codex, создаёт копию кода и может включить чувствительные файлы; по умолчанию не использовать |
| Codex Security | явный или boundary-triggered аудит | threat model, source-to-sink и supply-chain review | не является ежедневным gate для неизменившегося offline-кода; platform safeguards не отключаются |

Старые внешние workflow-плагины больше не входят в активный стек Map и не должны упоминаться в
новых prompt'ах. Полезные процессные идеи — TDD, systematic debugging, fresh verification и
UI/accessibility review — перенесены непосредственно в репозиторные правила Codex.

## Обязательная матрица инструментов в prompt

Каждый prompt содержит таблицу:

```markdown
| Риск / claim | Tool | Evidence | Required? | Skip condition |
```

Правила:

1. `dotnet build/test/format`, Git inventory и установленный `pre-commit` обязательны перед разрешённым code commit.
2. NuGet vulnerable/deprecated audit обязателен при добавлении/изменении dependency и входит в полный stage gate; в локальной быстрой итерации может не повторяться после каждого файла.
3. Coverage требуется для новой сложной ветвящейся логики или claim о покрытии.
4. Stryker требуется для изменённой критической команды, persistence/recovery/import path или когда обычные тесты могут проходить без meaningful assertions.
5. Diagnostics/benchmark требуются только для performance/memory claim или бюджета этапа.
6. Native UI evidence требуется для любого видимого UI/canvas или ввода. Headless-only не принимается.
7. Security review требуется при новом trust boundary: импорт/архив/десериализация непроверенного файла, external process/tool, network/account/secret, installer/updater/plugin, новая dependency из неутверждённого источника — либо по прямому запросу владельца.
8. Пропуск инструмента фиксируется с конкретной причиной. Фраза «не понадобилось» без привязки к риску недостаточна.
9. Для алгоритма с зрелым публичным аналогом source review по exact commit является инженерным
   инструментом: читаются только относящиеся методы/тесты, фиксируются license/files/выводы и
   строится собственный oracle. Само чтение upstream не является trigger для его сборки, запуска,
   полного security scan или добавления dependency.

## Verification budget и условия остановки

Бюджет задаётся в prompt **до** изменения кода и первого проверочного запуска. Цель — получить минимально достаточное независимое доказательство, а не максимальное число зелёных логов.

1. На одном и том же snapshot один успешно пройденный command/gate выполняется не более одного раза в каждой роли: author self-check и fresh review считаются разными ролями. Повтор «для уверенности» запрещён.
2. После изменения повторяется только доказательство, которое это изменение могло сделать устаревшим. Targeted RED → GREEN выполняется один раз; затем один раз запускается содержащий тестовый проект. Полный solution suite добавляется, только если менялся production-код, общий test harness, solution/project graph или есть конкретный межпроектный риск.
3. Для test-only review-fix без production/shared-boundary изменений достаточны: exact diff, доказанный RED → GREEN, один прогон содержащего test project, `git diff --check` и относящаяся repository-boundary проверка. Full solution build/test, native UI, dependency audit и unrelated gates не повторяются без отдельного risk trigger.
4. Coverage **или** Stryker выбирается по assertion-риску. Оба обязательны только когда prompt заранее называет разные claims, которые один инструмент не доказывает. Для изменения только deterministic regression test они по умолчанию не нужны.
5. Flake/timing/concurrency допускает максимум три последовательных успешных прогона, если риск и число прогонов записаны до запуска. Для детерминированного теста серия повторов запрещена.
6. Неуспешный gate можно повторить после относящегося изменения или смены диагностического метода. Если два последовательных результата показывают одну и ту же причину, слепой третий retry запрещён: изменить подход либо зафиксировать `PARTIAL`/`BLOCKED`.
7. Инструменты UI/visual/accessibility/security/dependency/performance включаются только соответствующим changed-risk trigger. Наличие инструмента в каталоге не является причиной запуска.
8. Fresh review обязан прочитать весь exact diff и проверить самый опасный изменённый путь, но не обязан заново выполнять весь self-check автора. Gate D не расширяет fix до полного stage re-acceptance.
9. Stop condition достигнут, когда выполнены acceptance-specific test, один соответствующий regression boundary, все risk-triggered gates и Git/scope inventory. После этого проверки прекращаются; в отчёте перечисляются сознательно пропущенные gates и причины.
10. Для docs-only policy change выполняются только проверка изменённых документов/ссылок или prompt
    contract и `git diff --check`/scope inventory. Restore/build/test/format solution, coverage,
    Stryker, package audit, native UI и visual gates запрещено добавлять без отдельного changed-risk
    trigger.

## Security и supply-chain без лишних блокировок

Map офлайн и не имеет серверных аккаунтов, но принимает локальные `.mapproj`, изображения, manifests/archives и запускает development tools. Поэтому утверждение «чужих входов нет и security нечего проверять» неверно.

Политика:

- secret scanner не входит в каждый commit-gate, пока в проекте нет секретов/интеграций;
- Gitleaks не возвращается как автоматически собираемый Go-hook: прежняя конфигурация скачивала toolchain и сканировала историю, создавая непропорциональную задержку;
- NuGet Audit остаётся базовой гигиеной зависимостей;
- corrupted/oversized/path-traversal/zip-bomb-like inputs остаются обязательными reliability/security cases для import/archive;
- security diff/full scan запускается по boundary-триггерам выше или по запросу владельца, а не запрещается blanket-правилом;
- новые hooks/plugins/MCP сначала проверяются: официальный источник, exact version/commit, license, hooks/commands, network, filesystem authority, telemetry, rollback;
- broad desktop-control MCP не устанавливается: он получает лишний доступ ко всему desktop. Используются Codex Computer Use и project-owned drivers с заданной целью;
- доступные connectors не разрешают внешнюю запись. Push/PR/release/messages/uploads остаются отдельным полномочием владельца;
- нельзя убрать встроенные safeguards Codex. Чтобы уменьшить ложные остановки, prompt один раз и ясно задаёт разрешённые локальные действия и отдельно перечисляет действия, требующие подтверждения.

## Git-протокол полного этапа

1. Зафиксировать branch, `BASE`, `HEAD`, status и существующий dirty worktree.
2. Работать в `stage/NN-*` или `fix/*`, не в `main`.
3. Prompt разрешает один полный stage commit; несколько commits — только по прямому указанию.
4. Перед commit установить hook и выполнить полный self-check.
5. После commit исполнитель останавливается.
6. Свежий Codex-review проверяет `BASE..HEAD` и отдельный worktree.
7. Дефект исправляется новым commit; `--amend`, rebase и history rewrite запрещены.
8. Push/PR/release/tag — только по прямому запросу владельца.

Обязательный inventory:

```powershell
git status --porcelain=v2 --branch
git diff --name-status BASE..HEAD
git diff --stat BASE..HEAD
git diff --check BASE..HEAD
git ls-files
dotnet sln Map.slnx list
Get-ChildItem src,tests,benchmarks,tools -Recurse -Filter *.csproj |
  ForEach-Object { dotnet list $_.FullName reference }
```

Отдельно считать `A/M/D/R`, проверять authored files, solution/project references, `.gitignore`, случайные `bin/obj/artifacts`, Markdown links, package versions и licenses.

## Gates

### Gate A — быстрый commit gate

```powershell
python -m pre_commit install
python -m pre_commit run --all-files
```

Hook проверяет структуру/files/links. Он не доказывает build или пользовательский outcome.

### Gate B — self-check полного этапа

База для production .NET stage:

```powershell
dotnet restore Map.slnx --force-evaluate
dotnet build Map.slnx -c Release --no-restore
dotnet test Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m
dotnet format Map.slnx --verify-no-changes --severity info --no-restore
dotnet package list --project Map.slnx --vulnerable --include-transitive --no-restore
dotnet package list --project Map.slnx --deprecated --include-transitive --no-restore
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```

Команды уточняются под stage и запускаются один раз после стабилизации среза; prompt обязан добавить исходный failure path и stage-specific drivers/tests. Полный базовый набор применим к production stage, но не переносится автоматически на docs-only или test-only fix.

### Gate C — свежая независимая приёмка

Ревьюер заново устанавливает Git identity/range, читает полный diff и выполняет high-risk path, один относящийся regression boundary и gates по risk matrix. Он не называет результаты исполнителя свежими фактами, но и не воспроизводит весь author self-check. По изменённому риску добавляются targeted coverage **или** Stryker, adversarial files, semantic roundtrip, Release EXE/UIA, screenshot/vision/diff, accessibility, benchmark и diagnostics.

### Gate D — после review-fix

1. воспроизвести исходный дефект;
2. показать red/green regression test;
3. один раз выполнить targeted regression и один относящийся regression boundary;
4. добавить только risk-triggered части Gate A/B/C; для test-only fix не повторять full solution/native/dependency/security gates без нового риска;
5. пересчитать `BASE..HEAD` и scope;
6. сохранить исправление отдельным commit и остановиться по verification budget.

## Практические проверки по типу изменения

| Изменение | Обязательные adversarial/failure cases |
|---|---|
| command / undo | empty/one/many, boundary values, repeated undo/redo, atomic rejection, long history |
| geometry / hit-test | negative/huge coordinates, zoom extremes, overlap, stable IDs, brute-force oracle где уместно |
| raster / mask | tile edge, unchanged neighbors, zero/full alpha, ownership, no edit overlays in export |
| save / recovery | kill process by phase, locked/read-only target, corrupt temp/journal, repeated recovery, semantic reopen |
| import / archive | canonical-path traversal, duplicates, compressed/uncompressed limits, oversized dimensions, corrupt/unsupported input |
| export | real decode, width/height/alpha, deterministic fixed seed, cancellation/OOM-like failure, no overlays |
| native UI | AutomationId, keyboard-only, focus, long Russian text, 100/150/200% DPI, Release window and screenshot |
| performance | Release without debugger, warmup, fixed fixture/seed, median/P95, allocations/GC, trace for regression |
| dependency/tool | exact source/version/license, install/execute hooks, network/filesystem authority, rollback and audit |
| внешний алгоритм/reference | exact commit/files/license, сравнение edge cases, собственный oracle; upstream не исполнять без trigger |

## Visual gate

Для принятого UI/canvas-state нужны три независимых вида evidence:

1. Release EXE + точные input actions + process identity + numbered PNG.
2. Vision review каждого PNG с перечислением фактически видимых controls/state/clipping/overlap/hierarchy/alignment/focus/canvas result.
3. Machine checks: project-owned UIA assertions и, для сравнимых fixed states, ImageMagick dimensions/RMSE/diff; Accessibility Insights для standard controls.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\compare-ui-screenshots.ps1 `
  -Reference .\path\expected.png `
  -Actual .\path\actual.png `
  -Difference .\artifacts\visual-diff\screen.png `
  -MaximumNormalizedRmse 0.002
```

Reference/actual используют одинаковые DPI, renderer, seed, zoom, viewport и document state. Новый expected сначала `PROPOSED` и принимается только после отдельного просмотра.

## Что не принимается

- нет точного commit/range или worktree содержит неучтённые authored changes;
- prompt не содержит outcome, acceptance, failure path и risk-tool matrix;
- проходит только default suite;
- coverage/mutation/visual/performance claim не подтверждён своим инструментом;
- mutation survivors не разобраны;
- save/export доказан только UI status;
- UI проверен только headless/process existence/screenshot existence;
- golden создан и принят тем же дефектным прогоном;
- security/reliability boundary blanket-пропущен фразой «приложение офлайн»;
- `PARTIAL`/`BLOCKED` выдан за `FACT`.

## Артефакт review

Каждый stage получает `docs/reviews/STAGE_NN_VERIFICATION.md` с branch/base/head/worktree, A/M/D/R и полным file list, acceptance matrix, свежими командами/exit codes, native UI evidence, findings, tool-matrix outcome, `FACT/PARTIAL/BLOCKED` и точным следующим fix/stage.
