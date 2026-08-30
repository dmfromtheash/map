# Stage 11 — измеримая производительность, переносимая сборка и локальный release candidate

> Status: ACTIVE

## Execution profile

- Model: gpt-5.6-sol.
- Reasoning: high для реализации; xhigh для архитектурной, security- и независимой acceptance-проверки.
- Role: implementation одного вертикального этапа с последующим fresh independent review.
- Git authority: один stage commit только после всех обязательных gate; no push, PR, merge, tag, release или публикация.
- Expected branch: stage/11-performance-packaging-rc.
- Required worktree: C:\Users\Leshe\.codex\worktrees\stage11-performance-packaging-rc\Map.

## Source of truth and baseline

Перед любыми изменениями полностью прочитать:

1. AGENTS.md;
2. CODEX.md;
3. docs/00_START_HERE.md;
4. docs/STATE_HANDOFF.md;
5. раздел Stage 11 в docs/06_DEVELOPMENT_ROADMAP.md;
6. docs/07_CODEX_RUNBOOK.md;
7. docs/11_CODEX_VERIFICATION_PROTOCOL.md;
8. docs/DECISIONS.md, особенно принятые решения о нативном Windows-приложении, формате проекта, офлайн-работе и release boundary;
9. docs/08_PERFORMANCE_BUDGETS.md, docs/09_RELEASE_CHECKLIST.md и актуальные review-отчёты Stage 10.

Accepted BASE определяется однозначно: найти commit с точным subject
docs: accept stage 10f and activate stage 11, доказать, что это текущий HEAD чистого
stage/11-performance-packaging-rc до первого изменения, и записать полный hash в отчёт.
Если ветка, worktree, HEAD или чистота не совпадают, остановиться со статусом BLOCKED.

## Outcome

Получить первый воспроизводимый локальный Windows x64 release candidate Map из одного
точного чистого commit: программа запускается без сети и SDK, открывает, редактирует,
сохраняет и экспортирует представительные карты, укладывается в принятые бюджеты на
типичной и большой сцене, а пакет содержит проверяемый manifest, лицензии зависимостей,
SBOM, контрольные суммы, инструкцию проверки и безопасный rollback.

Результат не является публичным релизом. Это локальный, самодостаточный RC-артефакт для
приёмки владельцем и отдельного независимого review.

## Current facts and gaps

- FACT: Stage 10F завершает функциональный срез до release-candidate работы.
- FACT: приложение остаётся нативным офлайн Windows-приложением на Avalonia и Skia.
- FACT: в репозитории уже есть тесты, benchmark project, doctor, pre-commit и протокол проверки.
- PARTIAL: отдельные performance-проверки существуют, но ещё нет единого доказанного RC-профиля.
- PARTIAL: Release build сам по себе не доказывает переносимость на чистой машине.
- Gap closed by this stage: связать измеримые бюджеты, representative fixtures, publish,
  package provenance, clean-machine smoke и rollback в один повторяемый поток.

## In scope

- зафиксировать два репозиторных representative fixtures: типичная карта и большая карта,
  без закрытых, нелицензированных или пользовательских ассетов;
- сделать измерения запуска, открытия, взаимодействия, памяти, большого canvas и экспорта
  воспроизводимыми, с warm/cold разграничением и сырой машинно-читаемой evidence;
- устранить только подтверждённые узкие места, необходимые для принятых бюджетов Stage 11;
- подготовить self-contained win-x64 Release publish без обязательного .NET SDK/runtime;
- сформировать локальный RC-каталог и архив из exact commit;
- включить manifest с commit, RID, build command, UTC-временем, версиями инструментов и
  перечнем файлов;
- включить SHA-256 для всех поставляемых файлов, SBOM и notices/licenses зависимостей;
- добавить повторяемый smoke на распакованном пакете и отдельную clean Windows VM проверку;
- документировать установку как распаковку в новый каталог, проверку hash и запуск;
- документировать rollback как закрытие приложения и возврат к предыдущему неизменённому
  каталогу без удаления пользовательских проектов и настроек;
- обновить только относящиеся к Stage 11 документацию, scripts, benchmark/tests и код,
  если измеренный дефект требует минимального исправления.

Expected project areas: benchmarks, scripts, tests, src, docs, release metadata и
репозиторные synthetic fixtures. Этот список не разрешает побочный рефакторинг.

## Out of scope

- installer, MSI, MSIX, file associations, shell extensions или изменение реестра;
- автообновление, download channel, telemetry, crash upload или обязательная сеть;
- code signing, сертификаты, SmartScreen reputation и публичная публикация;
- GitHub Release, package registry, сайт загрузки, push, PR, merge или tag;
- изменение формата MapProject, архитектурных границ или продуктовых лимитов;
- новые функции редактора, ассет-паки и визуальный redesign;
- изменение системной защиты, антивируса, firewall или политики чистой VM;
- исправление соседних дефектов, не мешающих критериям Stage 11.

Для installer, updater, signing, telemetry или file association сначала нужна отдельная
запись в docs/DECISIONS.md со статусом proposed и решение владельца. Не реализовывать их
в этом этапе.

## Invariants

- доменная модель не зависит от Avalonia и Skia;
- UI не является источником истины, rendering не изменяет документ;
- сохранение атомарно, миграция не перезаписывает единственную старую копию;
- редактирование остаётся command-based и пригодным для undo/redo;
- идентификаторы документов, слоёв, объектов и ассетов стабильны;
- сеть не нужна для запуска, редактирования, сохранения или экспорта;
- performance fixture детерминирован и не содержит чужих ассетов;
- измерение не маскирует ошибки, не отключает production validation и не подменяет код;
- RC строится только из чистого exact commit; uncommitted-файлы не входят в пакет;
- package smoke не запускает бинарники из bin/obj вместо распакованного RC;
- пользовательские проекты и настройки не удаляются при install/rollback smoke.

## Internal checkpoints

1. Baseline freeze: доказать root, branch, BASE, HEAD, clean state и перечень входных docs.
2. Measurement contract: зафиксировать fixtures, hardware/software fingerprint, метрики,
   число прогонов, warmup, median/P95 и допустимую дисперсию.
3. Red evidence: показать хотя бы один meaningful budget assertion или packaging check,
   который действительно обнаруживает нарушение при контролируемом негативном случае.
4. Minimal implementation: исправить только измеренные bottleneck и собрать publish pipeline.
5. Package integrity: manifest, hashes, SBOM, notices, archive и распакованный smoke.
6. Native evidence: реальные сценарии в точном Release publish EXE.
7. Clean-machine evidence: новая snapshot чистой Windows VM без SDK и исходников.
8. Full Gate B self-check, commit, post-commit inventory и остановка.

## Acceptance criteria

1. Typical fixture открывается не более чем за 5 секунд при размере проекта до 500 MB.
2. Typical 10k-object interaction стремится к 60 FPS и не опускается ниже 30 FPS в
   принятом повторяемом сценарии.
3. Large 50k-object interaction удерживает не менее 30 FPS; benchmark median кадра не
   превышает 33.3 ms.
4. Asset library на 10k записей не делает eager decode; исходная загрузка не более 2000 ms,
   warm search P95 не более 100 ms.
5. Zoom boundary 0.5–6400%, canvas/export boundary 65536 square pixels, undo history 200 и
   autosave UI blocking не более 100 ms проверены релевантными тестами или измерениями.
6. 8K и 16K export завершаются корректно; экспорт выше 16K использует принятый tiled/region
   путь и не создаёт один полный гигантский canvas.
7. Повторное открытие, save/reopen, undo/redo и export сохраняют семантический результат.
8. Self-contained win-x64 publish запускается из распакованного RC-каталога без сети,
   исходников, SDK и заранее установленного .NET runtime.
9. Manifest, SBOM, notices/licenses и SHA-256 полны, внутренне согласованы и проверяются
   отдельной командой.
10. Clean VM smoke проходит открытие существующего проекта, одно редактирование, save as,
    reopen и PNG export; проекты пользователя вне RC-каталога не меняются.
11. Rollback к предыдущему отдельному каталогу доказан без удаления новой версии и без
    изменения пользовательских данных.
12. Release EXE, evidence и package hash привязаны к одному post-commit HEAD.

## Failure and adversarial scenarios

- повреждённый или неполный архив, неверный SHA-256, пропущенный manifest file;
- путь с пробелами и Unicode, read-only каталог, длинный путь и недостаток места;
- отсутствующий runtime/SDK и полностью отключённая сеть;
- locked destination, cancelled export и повторный запуск после failure;
- project на верхней границе размера, 50k объектов, 10k assets и экстремальный zoom;
- экспорт 16K и запрос выше 16K без попытки выделить полный гигантский bitmap;
- два одинаковых запуска smoke, которые не должны зависеть от остатков первого;
- rollback при сохранённых отдельно пользовательских проектах и настройках;
- контролируемо изменённый файл пакета должен провалить hash verification.

## Risk and tool matrix

| Risk | Tool | Required evidence | Use | Reason if skipped |
|---|---|---|---|---|
| compile and regression | dotnet restore/build/test | exact commands, counts, zero errors | yes | mandatory final gate |
| repository boundary | git status/diff/ls-files/diff-check | exact BASE..HEAD inventory and clean tree | yes | mandatory |
| dependency graph | NuGet audit plus SBOM/license inventory | vulnerable/deprecated output and package graph | yes | RC supply chain |
| critical branch assertions | targeted tests and coverage where logic changes | exercised limits and failure branches | risk-triggered | skip only if no critical branch changed |
| file/import/archive boundary | adversarial package and fixture tests | corrupt, limit, path and roundtrip evidence | yes | package is a trust boundary |
| performance budget | benchmark, counters and trace/gcdump when needed | fingerprint, raw runs, median/P95, allocations | yes | core Stage 11 claim |
| native behavior | exact publish EXE plus project UIA driver | process identity, actions, checks and evidence JSON | yes | RC must work as a program |
| visual fidelity | numbered screenshots and vision review | representative frames and export review | yes | canvas/export regression risk |
| accessibility | UIA and Accessibility Insights | focus, names, roles and high-impact failures | risk-triggered | skip only if no UI changed; still run native keyboard smoke |
| security/supply chain | Codex Security or targeted review | package boundary, archive paths, provenance and tools | yes | distributable boundary |
| public algorithm/reference | exact-commit read-only source review | license, files, extracted principle and own oracle | no by default | no algorithm may be copied without a measured need |

Каждый применённый инструмент должен отвечать на конкретный риск. Не запускать каталог
инструментов ради количества. Если инструмент недоступен, записать точную причину и
использовать проверяемый локальный эквивалент, не выдумывая результат.

## Verification budget and stop rules

- Один baseline run, один run после реализации и один финальный post-commit package run.
- Performance: один warmup и минимум пять измеряемых прогонов на fixture; отчёт median,
  P95, min/max, fingerprint и сырые результаты. Повторять только после кода или доказанного
  загрязнения измерения.
- Один trace или gcdump на каждый нарушенный бюджет; второй только после конкретного fix.
- Один formatter run. Legacy-проблемы вне changed files отметить PARTIAL, не чинить.
- Один full test suite после targeted green. Не повторять неизменившийся green gate.
- Native driver: один основной прогон и один повтор только после исправления driver/app.
- Clean VM: один свежий snapshot и один повтор после исправления конкретного failure.
- После трёх одинаковых ошибок одного обязательного gate прекратить повторы и сообщить
  BLOCKED с логом, точной причиной и безопасным следующим действием.
- Не ослаблять budgets, fixtures или assertions для получения зелёного результата.

## Required automatic verification

Выполнить из точного worktree и сохранить команды, exit code и evidence:

- git rev-parse --show-toplevel
- git branch --show-current
- git rev-parse HEAD
- git status --short
- dotnet restore Map.slnx --locked-mode
- dotnet build Map.slnx -c Release --no-restore
- targeted tests для каждого изменённого проекта и каждого принятого budget
- dotnet test tests/Map.Core.Tests/Map.Core.Tests.csproj -c Release --no-build
- dotnet test tests/Map.Persistence.Tests/Map.Persistence.Tests.csproj -c Release --no-build
- dotnet test tests/Map.Rendering.Tests/Map.Rendering.Tests.csproj -c Release --no-build
- dotnet test tests/Map.Presentation.Tests/Map.Presentation.Tests.csproj -c Release --no-build
- dotnet test tests/Map.Assets.Tests/Map.Assets.Tests.csproj -c Release --no-build
- dotnet run --project benchmarks/Map.Benchmarks/Map.Benchmarks.csproj -c Release
- dotnet format Map.slnx --verify-no-changes --no-restore
- dotnet list Map.slnx package --vulnerable --include-transitive
- dotnet list Map.slnx package --deprecated
- powershell -NoProfile -ExecutionPolicy Bypass -File scripts/doctor.ps1
- powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-stage-prompts.ps1 -RequireActive
- pre-commit run --all-files
- git diff --check

Publish/package команды должны быть репозиторным script с детерминированными параметрами:
Release, win-x64, self-contained, exact clean HEAD и новый пустой output directory. Script
обязан отказать на dirty tree, несовпадающем commit, существующем непустом output и
неполном manifest/hash/SBOM.

## Required native UI verification

1. Запустить только Map.App.exe из распакованного RC и доказать process path, command line,
   SHA-256, commit manifest, PID, DPI и отсутствие процесса из bin/obj.
2. В реальном окне открыть typical fixture, выполнить zoom/pan/select, одно редактирование,
   undo/redo, Save As, закрыть, открыть сохранённый файл и экспортировать PNG.
3. Повторить ограниченный interaction/export smoke на large fixture и записать фактические
   времена, память и frame metrics.
4. Проверить keyboard-only путь основных команд и отсутствие очевидной focus trap.
5. Сделать нумерованные PNG ключевых состояний и визуально проверить canvas, текст,
   clipping, scaling и exported PNG.
6. На свежей clean Windows x64 VM без SDK/runtime и сети повторить launch, open, edit,
   save as, reopen, export и rollback. Зафиксировать snapshot identity и exact package hash.

Если clean VM недоступна, все локальные независимые gate продолжаются, но итог Stage 11
остаётся BLOCKED; локальный запуск не подменяет clean-machine acceptance.

## Git and stop condition

- Не использовать main checkout, reset, restore, stash, clean, amend или rebase.
- Не удалять и не перезаписывать owner changes.
- Staged allowlist должен соответствовать только Stage 11.
- Перед commit обязательны fresh self-check, pre-commit hook и clean diff-check.
- Разрешён ровно один implementation commit с subject:
  feat(stage-11): produce measured local release candidate
- После commit не менять код. Построить финальный RC из чистого committed HEAD, проверить
  package hash и собрать final BASE..HEAD inventory.
- Если post-commit gate находит дефект, не amend: остановиться и передать отдельный узкий
  review-fix с новым commit только по новому разрешению владельца.
- После commit и отчёта задача реализации останавливается. Fresh задача Codex независимо
  проверяет immutable BASE..HEAD и точный RC; same-context self-check не называется
  независимой приёмкой.
- No push, PR, merge, tag, release или публикация.

## Required report

Отчёт владельцу обязан содержать разделы:

- RESULT: FACT, PARTIAL, PROPOSED или BLOCKED;
- BASELINE: root, branch, BASE, HEAD и clean state;
- CHANGED: файлы и пользовательский результат;
- PERFORMANCE: fingerprint, fixtures, методика, сырые evidence и median/P95;
- PACKAGE: publish profile, manifest, SBOM, licenses, archive и SHA-256;
- VERIFIED AUTOMATICALLY: команды, exit codes и test counts;
- VERIFIED IN NATIVE UI: exact EXE/process, действия, checks и screenshots;
- CLEAN VM: snapshot, отсутствие SDK/runtime/network, сценарий и rollback;
- TOOL MATRIX RESULT: что применено или почему пропущено;
- NOT VERIFIED: только честные остатки;
- RISKS: известные риски и границы;
- GIT/SCOPE: BASE..HEAD inventory, commits, tree cleanliness, no external publication;
- NEXT: один fresh independent Stage 11 acceptance prompt.

Нельзя писать «готово», если любой обязательный критерий не выполнен. Любая непроверенная
обязательная clean-machine, performance, package-integrity или native граница делает
результат PARTIAL либо BLOCKED с точной причиной.
