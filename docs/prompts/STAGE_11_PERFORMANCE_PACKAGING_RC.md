# Stage 11 — измеримый portable RC: performance envelope, package integrity и rollback

> Status: ACTIVE

## Execution profile

- Model: `gpt-5.6-sol`.
- Reasoning: `high` для реализации и self-check; `xhigh` только для отдельной fresh acceptance,
  архитектурного решения или нового security/supply-chain boundary.
- Role: реализация одного вертикального Stage 11 slice. Same-context self-check не называется
  независимой приёмкой.
- Authority: внутри этого контракта действуй самостоятельно и обратимо. Остановись только перед
  новым продуктовым решением, внешним/необратимым действием, расходами, публикацией или
  существенным расширением scope.
- Git authority: после всех author-gates разрешён один implementation commit; push, PR, merge,
  tag, GitHub Release и публичная публикация не разрешены.
- Expected branch: `stage/11-performance-packaging-rc` в отдельном worktree, не в
  `C:\Projects\Map`.

## Source of truth and baseline

До изменения кода полностью прочитать:

1. `AGENTS.md` и `CODEX.md`;
2. `docs/00_START_HERE.md`, актуальную верхнюю запись `docs/STATE_HANDOFF.md` и Stage 9/11 в
   `docs/06_DEVELOPMENT_ROADMAP.md`;
3. `docs/07_CODEX_RUNBOOK.md`, `docs/08_QA_AND_ACCEPTANCE.md`,
   `docs/09_ASSET_PIPELINE_AND_LICENSING.md`, `docs/10_ENVIRONMENT.md` и
   `docs/11_CODEX_VERIFICATION_PROTOCOL.md`;
4. `docs/12_RELEASE_VERSIONING_AND_ROLLBACK.md`;
5. accepted `DEC-009`, `DEC-015` и `DEC-016` в `docs/DECISIONS.md`;
6. `docs/reviews/STAGE_10F_FONTS_BINDINGS_VERIFICATION.md`;
7. `docs/research/PUBLIC_REUSE_RESEARCH_20260829.md` и
   `docs/research/external-sources.json`;
8. существующие `benchmarks/Map.Benchmarks`, `scripts/build-local-release.ps1`,
   `scripts/check-version-consistency.ps1`, package/project files и относящиеся тесты.

Функциональный checkpoint до Stage 11:

`TRANSITION_BASE=1387b1b1a786017c325f2318a6b2fe930ed5b57c`
(`docs: accept stage 10f and activate stage 11`). Его parent должен быть
`2e30e78b0b86a4ab070158b7c773200611c62d58`.

Перед началом запиши `START_HEAD`. Он обязан быть либо `TRANSITION_BASE`, либо его чистым
docs-only descendant, где `TRANSITION_BASE..START_HEAD` меняет только этот stage prompt. Это
позволяет отдельно зафиксировать исправленный контракт, не присваивая ему production-работу.
Любой другой code/assets diff, неверная ancestry, ветка, root или dirty tree — `BLOCKED`.

Не изменяй, не очищай и не коммить:

- основной dirty checkout `C:\Projects\Map`;
- источник выбранного значка
  `C:\Users\Leshe\.codex\worktrees\fix-app-icon\Map` на branch `fix/app-icon`.

Предыдущий Stage 10F уже принят: feature `77dd7afa3881542894f5d959f6a52fbb09e2491f`, отдельный
fix `2e30e78b0b86a4ab070158b7c773200611c62d58`, full suite `738/738`, native `40/40`, scoped
security findings `0`. Не повторяй его старые targeted/native gates без changed-risk trigger.

## Public source reference plan

Основной вопрос: какие lifecycle, offline, downgrade и failure boundaries должен закрыть будущий
installer/updater, и нужен ли Map production dependency до принятого решения владельца?

- Mandatory bounded reference: Velopack, exact commit
  `214f8be7384201826ba03d4bad8f0f646d727613`, MIT; читать только `LICENSE`,
  `samples/CSharpAvalonia/`, `src/lib-csharp/VelopackApp.cs`, `UpdateManager.cs`,
  `Sources/SimpleFileSource.cs` и минимальные относящиеся tests.
- Result: короткий provider-admission memo с license, exact API/files, install/update/offline/
  downgrade/uninstall risks, filesystem/network/process authority, antivirus/signing question,
  rollback/removal plan и сравнением с текущим dependency-free side-by-side bundle.
- Default decision: не добавлять Velopack или иной updater в production graph без новой записи
  `docs/DECISIONS.md` со статусом `accepted`.
- Upstream не собирать и не запускать по умолчанию. Собственный минимальный probe допустим только в
  `C:\Projects\Map-ResearchLab\probes\stage11-update` на disposable VM snapshot, если чтение API не
  отвечает на один заранее записанный риск. Не собирать весь upstream и не подключать probe к Map.
- Conditional performance references: только если profiler локализовал bottleneck, прочитать
  минимальные относящиеся методы/tests pinned SkiaSharp
  `f74bd9adb82a77fd4a423db03c1077ea9d3be7e7`, Mapsui
  `7af71dbf2730322416b46b8fb58524a57f5356be` или Avalonia.Samples
  `27204ddbd36f7d99c3ba8f20a3b0cdb84ffe7775`. Не просматривать все 15 checkouts.

Публичный код можно читать на уровне конкретных фрагментов, методов и тестов, чтобы извлечь
алгоритмы, структуры данных, порядок операций, failure cases и тестовые идеи. Реализация Map
пишется самостоятельно под её domain/command/persistence/rendering contracts и проверяется
собственным oracle/fixture. Перенос подсистемы, механический перевод, узнаваемые имена,
комментарии или fixtures, копирование assets и package/direct reuse без отдельного admission,
attribution и removal plan запрещены.

## Outcome

Подготовить репозиторий к первому проверяемому portable `win-x64` release candidate текущего
функционального среза:

- фиксированные performance-профили и машинно-читаемые результаты показывают реальные бюджеты и
  границы, а узкие подтверждённые bottleneck исправлены минимально;
- существующий dependency-free publish flow создаёт self-contained immutable bundle с версией,
  exact source identity, manifest, SHA-256, SBOM/notices и отдельной проверкой целостности;
- выбранный владельцем собственный акварельный значок встроен в `Map.App.exe`, окно и package;
- распакованный candidate bundle проходит локальный offline smoke, а fresh independent acceptance
  может из committed HEAD построить окончательный пакет, проверить clean VM и side-by-side rollback.

Это локальный engineering RC, не публичный релиз и не утверждение, что весь продукт завершён.
Stage 9B–9E остаются отдельными срезами; отсутствие их возможностей фиксируется как release
prerequisite, а не реализуется скрытно внутри Stage 11.

## Current facts and gaps

- `FACT`: accepted functional base — `1387b1b1...`; Stage 10F повторно принимать не требуется.
- `FACT`: version foundation уже существует: `0.2.0`, `version.json`, assembly consistency,
  `scripts/build-local-release.ps1`, immutable output path, release manifest и SHA-256.
- `PARTIAL`: старый disposable dirty bundle доказал только работу foundation; clean source,
  clean VM и реальный двухверсийный rollback не доказаны.
- `PARTIAL`: текущий benchmark покрывает 50k culling/render, mixed scene, 10k catalog и Land, но
  не является единым 1k/10k/50k RC-профилем запуска, взаимодействия, памяти, stress и package.
- `FACT`: roadmap требует canvas `65 536×65 536` логических units; это не bitmap и не
  `65 536×65 536` export pixels. Export budgets — отдельные 8K, 16K и >16K tiled/region profiles.
- `PARTIAL`: Stage 9A принят, но Stage 9B checkpoints, 9C resize/crop/region, 9D full user export/
  cancellation и 9E migrations/corrupt diagnostics не завершены. В текущем production UI нет
  полного пользовательского export workflow; Stage 11 не присваивает себе Stage 9D.
- `FACT`: final icon work существует только как owner-approved uncommitted input. Активные файлы:
  `src/Map.App/Assets/AppIcon-Large.png`, SHA-256
  `8BF8D23D901D005670DE90C34E08DBB0BFA39993C6363E8DE229C64352DED4E1`, и
  `AppIcon-Large.ico`, SHA-256
  `7E396A60DFB2B6AF62807445D62FF5C7E19168EA494F923EB150039F3FDCD4AC`; проектные свойства —
  `ApplicationIcon` и `AvaloniaIncludeApplicationIconAsWindowIcon`.
- `PARTIAL`: Stage 10F сохранил known formatter baseline, machine-local font differences и четыре
  существующие Accessibility Insights baseline findings; они не становятся новыми Stage 11
  дефектами без regression evidence.

## In scope

- зафиксировать deterministic generators/descriptors для `objects-1k`, `objects-10k`,
  `objects-50k`, large map `65 536×65 536` units, 20+ layers/masks, 10k catalog, long brush/mask,
  undo-200, save/open и rendering export profiles; большие generated outputs не коммитить;
- определить один measurement contract: fixed seed/state, hardware/software/renderer fingerprint,
  cold/warm boundary, warmup, raw samples, median/P95, managed/native memory, GC/allocations,
  cache/undo size и допустимая вариативность;
- измерить baseline, устранить только доказанные Stage 11 bottleneck и повторить только затронутые
  профили;
- проверить 8K/16K и >16K renderer/export boundary без full-canvas allocation. Если production
  user export отсутствует, доказать только реально существующий renderer path и отметить Stage 9D
  prerequisite; не добавлять user export UI/API в этом коммите;
- усилить существующий `scripts/build-local-release.ps1` и добавить минимальный verifier/smoke,
  вместо создания второго параллельного packaging flow;
- формировать verifiable bundle inventory, manifest, checksums, machine-readable SBOM и
  dependency/license notices из текущего production graph без новой package dependency по
  умолчанию;
- механически перенести только выбранную large icon pair и относящиеся project settings из
  read-only icon worktree после проверки exact hashes; записать её собственное происхождение;
- проверить embedded EXE icon, Avalonia window icon и отображение Windows shortcut/package icon на
  нескольких стандартных размерах; внешний development launcher/desktop shortcut не считать RC;
- подготовить candidate package smoke и точные инструкции fresh acceptance для clean Windows VM,
  offline launch, package tamper, side-by-side old/new bundle и rollback;
- обновить только относящиеся benchmarks, scripts, tests, `src`, release metadata и Stage 11 docs.

## Out of scope

- Stage 9B checkpoints, 9C resize/crop/new-from-region, 9D product export/cancellation и 9E
  migrations/corrupt diagnostics;
- installer, MSI/MSIX, install/uninstall provider, registry/file association, shell extension,
  updater/download channel, telemetry, crash upload и обязательная сеть;
- code signing, certificate purchase, SmartScreen reputation, изменение antivirus/firewall/VM
  protection или публичный release;
- новая product version/tag policy, schema change, MapProject migration или изменение accepted
  performance/cache limits;
- новые editor features, asset packs, visual redesign и соседний refactor;
- перенос чужого кода/assets или добавление package «на всякий случай».

Installer/update/file association из общего release gate остаются `PROPOSED` до отдельного
accepted owner decision по `DEC-016`. Stage 11 выдаёт evidence для решения, но не принимает его за
владельца.

## Invariants

- доменная модель не зависит от Avalonia/Skia; UI не источник истины; rendering не изменяет
  document;
- save остаётся atomic, migrations не перезаписывают единственную старую копию, editing остаётся
  command-based, stable IDs сохраняются;
- сеть не требуется для launch/open/edit/save/rendering export; smoke отдельно доказывает это;
- large logical canvas никогда не материализуется одним bitmap; RAM/VRAM/cache/undo bounded;
- fixture и evidence детерминированы и не содержат чужих, закрытых или пользовательских assets;
- measurement не отключает production validation и не меняет budget ради зелёного результата;
- immutable final RC может быть построен только из clean committed HEAD; author dirty candidate не
  называется финальным RC;
- package verification запускает EXE из распакованного bundle, не из `bin/obj`;
- smoke/rollback не удаляет и не перезаписывает пользовательские проекты и настройки;
- source icon worktree и основной checkout сохраняются без изменений.

## Internal checkpoints

1. **Freeze:** root, branch, ancestry, `TRANSITION_BASE`, `START_HEAD`, clean state, accepted input
   hashes и inventory существующих performance/package/export возможностей.
2. **Contract:** profiles, budgets, fixtures, machine fingerprint, sampling, raw evidence schema и
   заранее ограниченный verification budget.
3. **RED/baseline:** один baseline performance set и контролируемые negative package/icon checks,
   которые действительно падают на tamper, неверном source state или missing icon.
4. **Minimal implementation:** measurement harness, только измеренные fixes и selected icon без
   соседней Stage 9 функциональности.
5. **Package boundary:** один publish flow, manifest/SBOM/notices/hashes/verifier, corrupt/tamper/
   path/space/locked-output cases и локальный unpacked candidate smoke.
6. **Author acceptance:** targeted green, один containing/full gate по changed risk, final affected
   measurements, exact candidate EXE native/icon/offline smoke, scope and diff audit.
7. **Commit and handoff:** pre-commit, один implementation commit и немедленная остановка; fresh
   task строит clean-HEAD RC и выполняет independent clean-VM/rollback acceptance.

## Acceptance criteria

1. Профили 1k/10k/50k и large-map описаны одним детерминированным contract, создаются без
   committed multi-hundred-MB fixtures и сохраняют raw results с fingerprint.
2. Typical project до 500 MB открывается не более чем за 5 секунд на зафиксированном reference PC;
   cold/warm значения не смешаны.
3. 10k interaction стремится к 60 FPS и не падает ниже 30 FPS в принятом сценарии; 50k large-map
   interaction и benchmark удерживают минимум 30 FPS / median frame time не выше `33.3 ms`.
4. 10k catalog не делает eager original decode; metadata load не выше `2000 ms`, warm search P95
   не выше `100 ms`; cache counts/bytes соответствуют `DEC-015`.
5. Canvas `65 536×65 536` units, zoom `0.5–6400%`, 20+ layers/masks, undo минимум 200 транзакций и
   autosave UI blocking не более `100 ms` проверены относящимся тестом или измерением.
6. Renderer profiles 8K/16K и >16K tiled/region не создают full-canvas allocation; cancellation,
   locked target и insufficient-space behavior классифицированы. Отсутствующий product export из
   Stage 9D честно остаётся prerequisite и не подменяется test-only renderer harness.
7. Long brush/mask/export stress не создаёт unbounded snapshot/cache growth; report содержит peak
   managed heap, working set, native estimate, GC/allocations и undo/cache bytes с оговорками.
8. `build-local-release.ps1` отказывает на dirty source без явного disposable-probe switch,
   existing/non-empty destination, inconsistent version и неполный output; incomplete output не
   выдаётся за release.
9. Candidate self-contained `win-x64` bundle содержит согласованные version/commit/RID/source-state,
   полный payload inventory, SHA-256, SBOM и notices/licenses; изменение одного файла ломает
   verifier.
10. Exact candidate `Map.App.exe` запускается из распакованного bundle офлайн без SDK/runtime,
    открывает/редактирует/save-as/reopen существующий project и не запускает `bin/obj` binary.
11. Выбранный icon имеет заявленные source hashes, встроен в EXE и окно и различим как Map в
    Windows shortcut/package views на 16/32/48/256 px; Windows icon cache не является единственным
    доказательством — проверяются embedded resources и fresh shortcut identity.
12. Provider memo отвечает на вопрос Velopack без production integration. Любая рекомендация
    installer/updater остаётся `PROPOSED` до решения владельца.
13. Author self-check подготавливает все scripts/evidence для fresh clean-HEAD acceptance, но не
    заявляет final RC, clean VM или независимость. Stage 11 получает `FACT` только после отдельной
    exact-range проверки, final bundle build, clean-VM smoke и side-by-side rollback.
14. Stage 9B–9E и известные Stage 10F residuals перечислены как ограничения; отсутствие соседней
    функции не маскируется и не расширяет implementation commit.

## Failure and adversarial scenarios

- modified/missing payload, неверный checksum/manifest/SBOM count, truncated archive и повторное
  использование immutable output directory;
- dirty source, wrong HEAD, version mismatch, release script из другого worktree, EXE из `bin/obj`;
- пути с пробелами/Unicode/long path, read-only/locked destination, недостаток места и interrupted
  publish/export;
- offline host без SDK/runtime, повторный smoke без зависимости от следов первого;
- 50k objects, 10k catalog, 20+ layers/masks, extreme zoom и 8K/16K/>16K renderer requests;
- cancellation/OOM-like refusal без полного giant bitmap и без повреждения предыдущего файла;
- cache/undo pressure, long brush/mask stress и repeated open/save без unbounded growth;
- wrong icon hash, ICO без нужных sizes, отсутствующий embedded icon и stale Windows icon cache;
- side-by-side old/new bundle с отдельными пользовательскими project copies; rollback не удаляет
  новую версию и данные;
- Velopack source/API не доказывает offline/downgrade property — memo помечает gap, а не делает
  production integration.

## Risk and tool matrix

| Risk | Tool | Required evidence | Use | Reason if skipped |
|---|---|---|---|---|
| compile and regression | `dotnet restore/build/test` | exact commands, counts, zero new errors | yes | production/scripts/project graph change |
| repository boundary | Git status/diff/ls-files/diff-check | root, ancestry, A/M/D/R, staged allowlist | yes | immutable review boundary |
| dependency graph | NuGet audit + generated SBOM/license inventory | direct/transitive graph, vulnerabilities, deprecated baseline, notices | yes | distributable package claim |
| critical branch assertions | targeted tests plus **coverage or Stryker**, not both by default | exercised new verifier/package/failure branches | risk-triggered | skip if no critical production branch changed; state why |
| file/import/archive boundary | negative script/tests on bundle and output paths | tamper, incomplete, locked, space/path behavior | yes | package is untrusted file boundary |
| performance budget | benchmark + counters; trace/gcdump only on failed budget | raw samples, median/P95, allocations/memory/fingerprint | yes | core Stage 11 claim |
| native behavior | exact candidate EXE + bounded project driver | process path/hash, offline actions, evidence JSON | yes | package must work as a program |
| visual fidelity | embedded icon inspection + minimal screenshots/export comparison | icon sizes/window/shortcut and representative canvas/export | yes | icon and renderer regression risk |
| accessibility | existing project UIA + one release keyboard/focus pass | names/focus/high-impact regression only | risk-triggered | Accessibility Insights repeat only if controls changed or baseline regressed |
| security/supply chain | targeted Codex Security review of changed package/tool boundary | source/tool authority, path/process/network and findings | yes | new distributable/tool boundary |
| public algorithm/reference | pinned minimal Velopack source review; conditional renderer refs | exact commit/license/files, extracted risks, own memo/oracle | yes, bounded | no whole-upstream audit/build |

Каждый инструмент отвечает на указанный риск. Недоступность инструмента фиксируется точно; локальный
эквивалент допустим, выдуманный PASS — нет.

## Verification budget and stop rules

- До изменений: один Git/source/icon preflight, один performance baseline set и по одному
  контролируемому negative check для package tamper и icon/source mismatch.
- Performance: один warmup и минимум пять measured samples на профиль; полный набор выполняется
  baseline и один раз на final candidate. Между ними запускается только профиль, затронутый fix.
- Один `trace` или `gcdump` на нарушенный budget; второй — только после относящегося fix.
- Targeted RED→GREEN; затем каждый изменённый containing test project один раз. Один полный
  `Map.slnx` test gate обязателен, потому что затрагиваются production/package/project boundaries.
  Не перечислять те же пять test projects отдельными повторными full runs.
- `coverage` или narrow Stryker выбирается для одного нового critical branch; оба нужны только если
  доказывают разные заранее названные claims.
- Formatter, vulnerable/deprecated audit, doctor, prompt checker и pre-commit — по одному успешному
  прогону на final author snapshot. Known unchanged baseline не исправлять попутно.
- Native author driver: один candidate run; один повтор только после изменения app/driver.
- Author не дублирует clean VM. Один final clean-VM/offline/rollback run выполняет fresh independent
  acceptance из committed HEAD; повтор только после относящегося fix.
- Для timing/VM допускается максимум три заранее объявленных flake-run. После двух одинаковых
  failures сменить метод; после третьего остановиться с `PARTIAL`/`BLOCKED` и evidence.
- Успешный gate на неизменном snapshot не повторять «для уверенности». Не ослаблять budget,
  fixture, assertion или protection ради зелёного результата.

## Required automatic verification

Зафиксировать command, exit code и evidence path. Точные test filters определяются изменёнными
рисками; не запускать blanket-копии одной suite.

1. Preflight: `git rev-parse --show-toplevel`, branch, `HEAD`, ancestry от `TRANSITION_BASE`,
   status, worktree list и `TRANSITION_BASE..START_HEAD` inventory.
2. Icon input: SHA-256 обоих selected files, ICO frame inventory, provenance и exact diff
   переносимых project settings; source worktree после чтения неизменен.
3. Restore/build: `dotnet restore Map.slnx --force-evaluate`, затем один Release build
   `--no-restore`.
4. Tests: targeted new/changed filters, содержащие projects один раз и один final full
   `dotnet test Map.slnx -c Release --no-build`.
5. Performance: repository driver для fixed profiles с raw JSON и machine fingerprint; counters,
   trace/gcdump только по matrix trigger.
6. Package: disposable author candidate через существующий release script с явным dirty-probe
   marker, verifier, tamper/path/locked-output negative cases и unpacked offline smoke. Manifest
   обязан говорить `cleanSource=false`; такой bundle не называется final RC.
7. Hygiene/supply chain: один `dotnet format ... --verify-no-changes --no-restore`, vulnerable и
   deprecated transitive audits, `scripts/doctor.ps1`,
   `scripts/check-stage-prompts.ps1 -RequireActive`, `pre-commit run --all-files` и
   `git diff --check`.
8. Final scope: unstaged/staged A/M/D/R, ignored artifact separation, staged allowlist, diff stats
   и отсутствие owner-source changes.

Не использовать `dotnet restore --locked-mode`: repository не имеет принятого lockfile decision.
Не добавлять lockfiles скрытно; это отдельный supply-chain выбор.

## Required native UI verification

Author выполняет один ограниченный прогон exact candidate package:

1. Проверить process path, PID, command line, SHA-256, version, manifest source identity, DPI и что
   EXE запущен из распакованного candidate bundle, а не `bin/obj`.
2. В реальном окне офлайн открыть representative existing project, выполнить pan/zoom/select,
   одно редактирование, undo/redo, Save As, закрыть и открыть копию повторно.
3. Если product export UI отсутствует, не имитировать его: проверить только реально существующий
   renderer/export harness и записать Stage 9D prerequisite.
4. Проверить окно и fresh Windows shortcut с embedded icon на стандартных размерах; зафиксировать
   resource inventory и минимальные screenshots, очистку icon cache использовать только как
   диагностический обход, не как изменение продукта.
5. Проверить keyboard-only основные команды и отсутствие новой focus trap. Accessibility Insights
   запускать повторно только при изменении controls или новом high-impact symptom.
6. Подтвердить отсутствие обязательного сетевого соединения/listener в smoke и завершить только
   процесс этого candidate package.

Fresh independent acceptance после implementation commit отдельно строит final bundle из clean
HEAD и на одной свежей Windows x64 VM без SDK/runtime и сети выполняет: integrity verification,
launch, open/edit/save-as/reopen, доступный export path, package tamper refusal, old/new
side-by-side launch и rollback. Если VM или обязательная product capability недоступна, Stage 11
остаётся `PARTIAL`/`BLOCKED`; локальный author smoke это не заменяет.

## Git and stop condition

- Не использовать main checkout, `reset`, `restore`, `stash`, `clean`, `amend`, `rebase` и не
  изменять owner worktrees.
- Generated fixtures, raw traces, packages, screenshots и VM evidence остаются в ignored
  `artifacts/`; в commit входят только generators/contracts/scripts/tests/code/docs.
- Перед commit: final author snapshot, staged allowlist, fresh required gates, установленный
  pre-commit hook и clean `git diff --check`.
- Разрешён ровно один implementation commit:
  `feat(stage-11): prepare measured portable release candidate`.
- После успешного commit немедленно остановиться: не строить post-commit RC, не менять код и не
  «допроверять» тот же snapshot. Это работа fresh independent acceptance.
- Если последний author gate находит дефект, исправить до commit в scope. Если дефект найден после
  commit, не amend: передать отдельный узкий review-fix, который потребует нового разрешения и
  нового commit.
- No push, PR, merge, tag, GitHub Release, upload или публичная публикация.

## Required report

- `RESULT`: `FACT`, `PARTIAL`, `PROPOSED` или `BLOCKED`, отдельно для author slice и общей RC
  readiness;
- `BASELINE`: root, branch, `TRANSITION_BASE`, `START_HEAD`, pre-commit HEAD и cleanliness;
- `PREREQUISITES`: точный статус Stage 9B–9E и что реально доступно current product;
- `CHANGED`: файлы, минимальные fixes и пользовательский результат;
- `PERFORMANCE`: profiles, fingerprint, sampling, raw evidence, median/P95, memory/cache/undo;
- `PACKAGE`: candidate publish, cleanSource status, manifest, SBOM, notices, hashes и verifier;
- `ICON`: source hashes/provenance, embedded frames, EXE/window/shortcut evidence;
- `PUBLIC REFERENCES`: exact upstream/files/license, извлечённые решения, что написано
  самостоятельно и почему production direct reuse/package не сделаны;
- `VERIFIED AUTOMATICALLY`: команды, exit codes, test counts и risk-triggered tools;
- `VERIFIED IN NATIVE UI`: exact process/bundle, действия, DPI, offline и evidence;
- `NOT VERIFIED`: final clean-HEAD bundle, clean VM/rollback и любые честные gaps;
- `RISKS`: known baselines, missing product capabilities и provider decision;
- `GIT/SCOPE`: commit, `START_HEAD..HEAD` A/M/D/R, staged allowlist, owner worktrees unchanged,
  no external publication;
- `NEXT`: один fresh independent Stage 11 exact-range/package/clean-VM acceptance prompt. Не
  начинать следующий roadmap stage и не повторять Stage 10F.

Нельзя писать «готово» для продукта или final RC, если обязательная performance, package,
native, clean-VM, rollback либо Stage 9 prerequisite не доказана указанным способом.
