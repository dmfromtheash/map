# Stage 10D — Полные inspectors объектов и Brush

> Status: HISTORICAL CONTRACT — completed and independently accepted through the bounded Gate D range; do not execute again.

Этот файл сохранён как исторический контракт завершённого Stage 10D. Реализация и последующий
test-only Gate D приняты; активным исполняемым контрактом является отдельный Stage 10E.

## Execution profile

- Model: `gpt-5.6-sol`.
- Reasoning: `high` для реализации; `xhigh` для security/review-fix и финального review.
- Role: implementation → self-acceptance → fresh independent review после разрешённого commit.
- Autonomy: read-only проверки и обратимые изменения строго внутри Stage 10D разрешены. Остановка
  нужна только перед новым продуктовым/архитектурным решением, зависимостью или лицензией,
  аккаунтом/секретом, внешним либо необратимым действием и материальным расширением scope.
- Git authority: no commit без прямого разрешения владельца; no push/PR/release/tag.

## Source of truth and baseline

Прочитать полностью и по порядку:

1. `AGENTS.md`;
2. `CODEX.md`;
3. `docs/00_START_HERE.md`;
4. `docs/STATE_HANDOFF.md`;
5. Stage 10D в `docs/06_DEVELOPMENT_ROADMAP.md`;
6. accepted decisions `DEC-001`–`DEC-014` и относящееся к текущим fixes решение `DEC-015`;
7. `docs/02_FUNCTIONAL_SPEC.md`, `docs/04_TECH_ARCHITECTURE.md`,
   `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`, `docs/09_ASSET_PIPELINE_AND_LICENSING.md`;
8. `docs/07_CODEX_RUNBOOK.md`;
9. `docs/11_CODEX_VERIFICATION_PROTOCOL.md`.

Expected branch: `stage/10d-object-inspectors`
Accepted BASE: `4b50ba0a707b45ace00da607650ee4d5c1a952bb`

До правок сообщить фактические branch, HEAD, worktree, сохранённые пользовательские изменения и
расхождения. Никогда не очищать и не сбрасывать dirty tree владельца.

## Outcome

Пользователь выбирает Stamp, Text, Line, Shape или Brush в настоящем нативном окне, видит полный
инспектор нужного типа, меняет весь согласованный набор appearance-параметров законченными
undoable действиями, сохраняет `.mapproj`, открывает его снова и получает тот же визуальный и
доменный результат. Ассетные fill/stroke/tip работают офлайн и безопасно обрабатывают исчезнувшие,
изменённые, повреждённые и чрезмерные локальные файлы.

## Current facts and gaps

- `FACT`: Stage 10C уже даёт выбор, группы, multi-selection и геометрические transforms.
- `FACT`: asset catalog/variant sets и базовое placement существуют после Stage 10B.
- `PARTIAL`: до этого среза часть inspectors была placeholder/неполной, appearance не полностью
  проходил через render → save/reopen → undo/redo, а file/image boundaries имели пробелы.
- Gap closed by this stage: единая сквозная реализация всех пяти inspectors плюс доказанный
  persistence/render/native/security contract, без перехода к Grid geometry Stage 10E.

## In scope

- полный Stamp inspector: scale/reference dimensions, flips, opacity, sublayer, shadow, blend,
  blur и color adjustments;
- полный Text inspector: content/font fallback/weight/size/fill/outline/shadow/curve/spacing/
  line height/alignment/flips/blend/blur;
- полный Line inspector: stroke colour/width/opacity/pattern/offset/caps/shadow/blend/blur,
  asset stroke, spacing и color adjustments;
- полный Shape inspector: fill/opacity/asset texture/texture transform/stroke/portals и общие
  appearance effects;
- полный Brush inspector: colour/size/opacity/softness/spacing/scatter/angle/random flips/jitter/
  eraser и локальный named asset tip;
- команды undo/redo, rendering, DTO/persistence, save/reopen, backward compatibility;
- bounded exact-byte asset/image/cache/thumbnail и `.mapproj` read boundaries, затронутые 10D;
- targeted tests, mutation/coverage evidence, Release native UI driver, screenshots и отчёт;
- expected project areas: `src/Map.Core`, `src/Map.Rendering`, `src/Map.Persistence`,
  `src/Map.Assets`, `src/Map.Presentation`, `src/Map.App`, соответствующие `tests`, `scripts` и docs.

## Out of scope

- Stage 10E Grid geometry и следующие roadmap stages;
- новая архитектура, формат/schema version, облако, web shell или новая dependency;
- unrelated cleanup, массовый refactor и миграция тестового framework;
- исправление старых соседних UX-долгов 10B/10C без прямой связи с 10D acceptance;
- push/PR/release/publication.

## Invariants

- Domain остаётся независимым от Avalonia/Skia; UI не является document truth; rendering не
  изменяет document.
- Каждое document edit — тестируемая undo/redo command; document/layer/object/asset IDs стабильны.
- Persistence атомарен; migration/open не перезаписывает единственную старую копию.
- Startup/edit/save/export остаются offline.
- Schema v1 и существующие contracts сохраняются, если accepted DEC явно не говорит иначе.
- Используются только собственные/разрешённые assets и clean-room observations.
- Один законченный slider gesture создаёт не более одной history entry; cancel/no-op — ноль.
- Linked/catalog identity проверяется по SHA-256 exact bytes; optional thumbnail не блокирует
  здоровый original; allocation/decode/materialization имеют явные budgets.

## Internal checkpoints

Это внутренние ворота одного крупного prompt, а не причины остановиться после маленькой части:

1. Зафиксировать model/DTO/command contracts и red tests всех типов объектов.
2. Реализовать общий appearance edit lifecycle и type-specific параметры.
3. Провести свойства через renderer и deterministic save/reopen без schema bump.
4. Собрать полный нативный inspector surface и asset actions.
5. Закрыть adversarial path/image/archive/cache cases и regressions.
6. Выполнить полный self-check, native evidence, документацию и scope report.

Если checkpoint обнаруживает решение вне scope, пометить только эту ветвь `BLOCKED` и продолжить
остальную независимую работу внутри Stage 10D.

## Acceptance criteria

1. Каждый из пяти типов показывает полный правильный inspector; смешанный/locked/hidden выбор не
   выполняет частичное скрытое изменение.
2. Законченное изменение даёт ровно одну undo entry; undo/redo восстанавливают domain и render;
   no-op/cancel не загрязняют history.
3. Все документные параметры сохраняются и повторно открываются с той же семантикой; старый v1
   без новых optional полей открывается с совместимыми defaults.
4. Release EXE визуально и через UIA доказывает Shape, Brush, Stamp, Line, Text, save и reopen.
5. Invalid enum/color/SHA/non-finite, corrupt/oversized image, changed/missing linked content,
   hostile ZIP/JSON и повреждённая optional thumbnail дают точный отказ без mutation документа.
6. Renderer, asset library, selection и сохранение предыдущих stages не получают regression.
7. Полные stage gates проходят без неучтённых файлов, новых warnings и скрытых skips.
8. Всё недоказанное обозначается `PARTIAL`/`BLOCKED`, а не подразумевается готовым.

## Failure and adversarial scenarios

- rooted/drive-relative/UNC/device/ADS/reparse/escaping linked paths и verify→reopen race;
- empty/truncated/oversized/decompression-bomb image/thumbnail и cache resident-memory pressure;
- duplicate/non-canonical/over-compressed/oversized `.mapproj` entries, excessive preview IHDR,
  миллионы коротких JSON objects/notes/nodes/items и чрезмерные strings;
- locked/hidden selection, mixed types, no-op/cancel, repeated gesture, undo/redo/save/reopen;
- для review-fix: targeted regression должен падать без исправления или mutation survivor должен
  быть классифицирован с конкретным доказательством.

## Risk and tool matrix

| Risk / claim | Tool | Required evidence | Required? | Valid skip condition |
|---|---|---|---:|---|
| compile and regression | `dotnet restore/build/test/format` | exit codes, counts, 0 build warnings/errors; format debt separated | yes | none |
| repository boundary | Git inventory + `pre-commit` | BASE/worktree A/M/D/R, hook result, no hidden publish | yes | immutable range waits for authorized commit |
| dependency graph | NuGet audit | vulnerable/deprecated output and package diff | yes | none for final full gate |
| critical branch assertions | coverage + Stryker | Stage10D filters, branch/file evidence, survivors disposition | yes | only a tool failure reported `BLOCKED` |
| file/import/archive boundary | targeted adversarial tests | exact-byte/path/decode/JSON/ZIP/atomicity results | yes | none: 10D crosses this boundary |
| performance budget | benchmark + counters/trace/gcdump | retained cache and bounded-admission evidence | partial | full profiling belongs to Stage 11; no throughput claim |
| native behavior | Release EXE + project UIA driver | process identity, 13 actions, evidence JSON | yes | none: UI changed |
| visual fidelity | screenshots + vision + ImageMagick | numbered PNG review and fixed-state RMSE/diff | yes | no accepted external visual clone/reference |
| accessibility | UIA + Accessibility Insights | names/roles/focus/tab and tool result | partial | full clean-context audit remains explicit debt |
| security/supply chain | Codex Security or targeted review | fresh source-to-sink review, path/image/archive/cache findings | yes | none: owner requested and boundaries changed |

Не удалять строки. Каждый skip объяснить в итоговом отчёте.

## Verification budget and stop rules

Текущие лимиты из `docs/11_CODEX_VERIFICATION_PROTOCOL.md` обязательны. Каждый зелёный gate — один
раз на неизменившемся snapshot и в одной роли; повтор только после относящегося изменения. Coverage
и Stryker вместе допускаются лишь для заранее разделённых claims. Для test-only review-fix нужны
RED → GREEN, один containing test project и Git/scope gate; полный Stage 10D suite/native/security
контур не повторяется без нового changed-risk trigger. После исчерпания бюджета —
`PARTIAL`/`BLOCKED`, а не цикл одинаковых прогонов.

## Required automatic verification

```powershell
dotnet restore .\Map.slnx --force-evaluate
dotnet build .\Map.slnx -c Release --no-restore
dotnet test .\Map.slnx -c Release --no-build
dotnet format .\Map.slnx --verify-no-changes --no-restore
dotnet list .\Map.slnx package --vulnerable --include-transitive --no-restore
dotnet list .\Map.slnx package --deprecated --include-transitive --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-stage-prompts.ps1 -RequireActive
python -m pre_commit install
python -m pre_commit run --all-files
git diff --check
```

Дополнительно выполнить Stage10D-filtered coverage, Stryker по appearance/path/archive/image
boundaries и все targeted adversarial tests. Broad green suite не заменяет отсутствующий
acceptance-specific test.

## Required native UI verification

Собрать Release EXE `src/Map.App/bin/Release/net10.0/Map.App.exe`, убедиться в точной identity
процесса и запустить:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\drive-stage10d-object-inspectors.ps1
```

Нужны PASS всех 13 действий и 30 checks, 7 numbered PNG, archive assertions, reopen assertions,
clean shutdown без оставшегося `Map.App`, визуальный review ключевых Shape/Brush/reopened Text
кадров и ImageMagick RMSE/diff между нормализованными состояниями. Same-task vision не выдавать за
независимый clean-context UX gate.

## Git and stop condition

- Сохранять pre-existing owner changes и authored files.
- Не применять `--amend`, rebase, reset, history rewrite или cleanup unrelated files.
- Если commit прямо разрешён, сделать один Stage 10D checkpoint только после обязательных gates,
  сообщить полный `BASE` и `HEAD`, затем остановиться для fresh independent verification.
- Никогда не push, не создавать PR/release/tag и не публиковать без нового прямого запроса.

## Required report

Использовать точную структуру `docs/07_CODEX_RUNBOOK.md`: acceptance-by-acceptance status,
назначение файлов, commands/exit/count, native evidence, каждый tool used/skipped с причиной,
`FACT/PARTIAL/PROPOSED/BLOCKED`, residual risks, scope check и одну следующую задачу, которая не
реализована. Текущий self-acceptance сохранять в
`docs/reviews/STAGE_10D_OBJECT_INSPECTORS_VERIFICATION.md`.
