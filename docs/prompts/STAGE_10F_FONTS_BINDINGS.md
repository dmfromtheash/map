# Stage 10F — локальный каталог шрифтов и реально редактируемые сочетания

> Status: ACTIVE

## Execution profile

- Model: `gpt-5.6-sol`.
- Reasoning: `high`.
- Role: `implementation` и author self-check; это не fresh independent review.
- Autonomy: локальные read-only проверки и обратимые изменения внутри Stage 10F разрешены.
  Остановиться перед новым продуктовым/архитектурным решением, dependency, schemaVersion,
  расходами, публикацией, необратимым действием или существенным расширением scope.
- Git authority: один feature commit после всех обязательных gates; no push/PR/release/tag.

## Source of truth and baseline

Прочитать полностью:

1. `AGENTS.md`;
2. `CODEX.md`;
3. `docs/00_START_HERE.md`;
4. `docs/STATE_HANDOFF.md`;
5. Stage 7 и Stage 10F в `docs/06_DEVELOPMENT_ROADMAP.md`;
6. accepted `DEC-001`, `DEC-002`, `DEC-003`, `DEC-004`, `DEC-005`, `DEC-007`, `DEC-008`,
   `DEC-009`, `DEC-010`, `DEC-012`, `DEC-015`, `DEC-016` в `docs/DECISIONS.md`;
7. Text и Settings в `docs/02_FUNCTIONAL_SPEC.md`;
8. относящиеся к text/keyboard разделы `docs/03_UX_UI_SYSTEM.md`,
   `docs/04_TECH_ARCHITECTURE.md`, `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`,
   `docs/08_QA_AND_ACCEPTANCE.md`;
9. `docs/reviews/STAGE_10E_GRID_GEOMETRY_VERIFICATION.md`;
10. `docs/07_CODEX_RUNBOOK.md`;
11. `docs/11_CODEX_VERIFICATION_PROTOCOL.md`;
12. этот контракт.

Ожидаемые границы:

```text
Accepted BASE: документальный checkpoint «docs: accept stage 10e and activate stage 10f»
Expected branch: stage/10f-fonts-bindings
Required worktree: C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map
```

Точный `STAGE10F_BASE` берётся как полный hash checkpoint-коммита, содержащего этот prompt, и
фиксируется в отчёте. До правок проверить root, branch, HEAD, status и отсутствие неизвестных
изменений. `C:\Projects\Map` и Stage 10E worktree не менять и не очищать.

## Public source reference plan

- Question: `N/A` — каталог использует уже подключённый Skia/Windows runtime, а bindings адаптируют
  существующие команды Map; алгоритмической неопределённости нет.
- HarfBuzz/shaping probe не запускать: Stage 10F не меняет shaping engine. Он разрешён только после
  отдельного failing multiscript/curved-text fixture и нового решения владельца.
- Новые packages, direct reuse и внешний research не требуются.

## Outcome

Пользователь в настоящем нативном окне открывает офлайн-каталог доступных Windows-шрифтов,
выбирает семейство для одного текста, видит отдельно запрошенный шрифт и фактический fallback,
использует undo/redo и save/reopen; затем меняет поддерживаемое сочетание, сразу управляет им
реальной командой редактора, видит конфликт до сохранения, сбрасывает настройки и после
перезапуска получает сохранённый профиль.

## Current facts and gaps

- `FACT`: `TextStyle` уже хранит requested/fallback family, weight и size; `.mapproj` сохраняет их.
- `FACT`: `TextFontResolver` и renderer уже дают bounded machine-local fallback.
- `FACT`: реальные editor-команды и совместимые defaults существуют.
- `PARTIAL`: `EditorShortcutSettings` хранит строки, но `MainWindow`, help и tooltips используют
  отдельные hardcoded истины; изменение настройки не меняет routing.
- Gap: единый реестр реально подключённых команд, безопасное user persistence и полный font UX.

## In scope

- стабильный реестр keyboard-команд: id, название, контекст, default/effective gesture, action;
- parsing/normalization, overlapping-context conflict detection, unassign, reset one/all;
- фактическая маршрутизация всех поддерживаемых keyboard-команд из одного профиля;
- dynamic help/tooltips/key chips из того же профиля и guard для text/search/edit fields;
- атомарный bounded JSON вне `.mapproj`, только known IDs, понятное damaged-file warning;
- изолированный settings path для tests/native driver;
- уникальный стабильно отсортированный offline-каталог `SKFontManager` с case-insensitive search;
- current requested family, отдельный resolved fallback, выбор одного text object одной command;
- Text-context previous/next defaults `[` и `]` через общий editable profile;
- undo/redo, save/reopen/export через существующий resolver;
- targeted tests, project-owned driver, screenshots, machine-readable evidence и stage docs.

## Out of scope

- network font download, font-file import, embedding/distribution, bundled font pack;
- HarfBuzz, новый shaping engine, rich text;
- mouse/pointer remapping, scripts/macros, cloud sync, multi-user profiles;
- исправления Stage 9, Stage 10B–10E, Stage 11, DPI/file-picker/старого accessibility debt;
- общий refactor `MainWindow`/input architecture, новый command bus или settings framework;
- `.mapproj` schema change, новые dependencies, push/PR/release/publication.

Соседние findings только перечислить в отчёте.

## Invariants

- Domain не зависит от Avalonia/Skia; UI не источник истины; rendering не меняет document.
- Смена font family — одна тестируемая undoable-команда только для выбранного текста.
- Requested и resolved fallback не подменяют друг друга; отсутствующий request не стирается.
- Map перечисляет локальные системные fonts, но не заявляет их поставку или право распространения.
- Keyboard routing использует command ID/profile, не UI text и не отдельные hardcoded tables.
- Settings не входят в карту; map save и settings save атомарны и полностью offline.
- Повреждённая единственная settings-копия не перезаписывается до явного user save.
- Не показывать editable binding, за которым нет реально подключённой команды.
- Не перехватывать глобальные команды при вводе в text/search/edit controls.
- Формат `.mapproj` не менять. Если существующих полей недостаточно — `BLOCKED`.

## Internal checkpoints

1. Preflight, hardcoded-key inventory и один meaningful routing RED.
2. Registry/profile/parser/conflicts/reset и atomic bounded persistence; targeted GREEN.
3. MainWindow routing, settings UI, dynamic help/tooltips и text-input guard.
4. System font catalog, search/select/previous/next, undo/redo и fallback presentation.
5. Roundtrip/export tests и repository-owned native scenario.
6. Triggered final gates, report, exact Git inventory, one feature commit, stop.

## Acceptance criteria

1. Каталог показывает реальные локальные families без дублей, в stable order, с поиском без регистра.
2. UI честно показывает requested family; missing request сохраняется, resolved fallback показан отдельно.
3. Выбор и previous/next меняют только один выбранный text одной history entry; exact undo/redo.
4. Save/reopen сохраняет requested/fallback, export использует тот же `TextFontResolver`.
5. UI сообщает, что это локальные системные шрифты и Map их не встраивает/не распространяет.
6. Registry содержит только реально routed commands и сохраняет прежние совместимые defaults.
7. User rebind немедленно отключает old gesture и включает new gesture в настоящем editor.
8. Конфликт в пересекающемся контексте виден до save и не сохраняется; disjoint contexts допустимы.
9. Unassign, reset one и reset all работают; defaults включают `[`/`]` для text font navigation.
10. Settings переживают restart; unknown/oversized/malformed entries не падают и bounded.
11. Полностью damaged file даёт defaults + warning и сохраняет исходные bytes до explicit save.
12. Tests/native driver используют isolated settings path, не real user settings.
13. Text/search/edit fields не перехватываются global editor commands.
14. Help/tooltips/key chips читают effective gesture из того же profile.
15. Нет новой dependency, сети, schema change или неучтённых файлов.
16. Native driver проходит на exact Release EXE и проверяет сохранённые `.mapproj`/settings как data.

## Failure and adversarial scenarios

- duplicate/case-variant fonts, empty search, missing requested family, zero/one available family;
- no selection, non-text selection, locked/hidden object/layer, no-op choice, repeated undo/redo;
- same gesture in overlapping versus disjoint contexts, invalid/empty/too-long gesture;
- unknown command IDs, too many entries, oversized JSON/string, malformed/truncated JSON;
- locked/unwritable settings target and preservation of damaged original until explicit save;
- rebind then old/new key, restart, reset, typing in content/search/settings fields.

## Risk and tool matrix

| Risk / claim | Tool | Required evidence | Required? | Valid skip condition |
|---|---|---|---:|---|
| compile and regression | targeted tests + Release build + one full solution run + format | RED/GREEN, counts, 0 build warnings/errors, honest format result | yes | none for code stage |
| repository boundary | Git inventory + installed `pre-commit` | BASE..HEAD, A/M/D/R, exact files, hook PASS | yes | none before commit |
| dependency graph | NuGet vulnerable/deprecated audit + package diff | advisories and proof of no added package | yes | none in final full-stage gate |
| critical branch assertions | deterministic targeted tests | routing/conflict/settings/font observable results | yes | coverage/Stryker skipped: no separate claim; prompt requires ordinary tests |
| file/import/archive boundary | adversarial settings tests | bounds, damaged preservation, atomic save, isolated path | yes | unrelated `.mapproj` archive/security suites skipped |
| performance budget | no benchmark | N/A | no | no performance claim or heavy algorithm |
| native behavior | exact Release EXE + project UIA driver | old/new key, conflict, font, restart/reset, report/data/process identity | yes | none: input/UI changes |
| visual fidelity | small screenshot set + vision | readable catalog/conflict/fallback/settings states | yes | pixel-golden/ImageMagick skipped: no accepted visual reference |
| accessibility | UIA in native driver | names, roles, focus, keyboard activation for changed controls | yes | full Accessibility Insights outside this bounded stage |
| security/supply chain | targeted settings reliability review | bounded untrusted JSON and atomic/preservation evidence | yes targeted | full Codex Security skipped: no network/new dependency/executable parser |
| public algorithm/reference | exact-commit review | N/A | no | existing APIs and no algorithmic uncertainty |

## Verification budget and stop rules

- Каждый successful gate — один раз на unchanged snapshot/role; без повторов «для уверенности».
- Сначала один targeted RED, после относящегося change один GREEN; затем containing test project once.
- Full solution run один раз, потому что меняются shared production projects.
- Native driver — один полный финальный pass; повтор только после change, обесценившего evidence.
- После падения повторять только affected gate. После двух одинаковых infrastructure failures сменить
  метод или выдать `PARTIAL/BLOCKED`; blind third retry запрещён.
- Flake-series не объявляется: routing/settings/font tests детерминированы.
- Не запускать Stryker, coverage, full Security, Accessibility Insights, ImageMagick, benchmark,
  shaping probe или внешний research без нового конкретного changed-risk trigger.
- Stop: acceptance-specific evidence, one regression boundary, triggered gates, Git/scope inventory
  и feature commit. После commit не повторять проверки и не начинать следующий stage.

## Required automatic verification

Сначала выполнить только новый routing test и сохранить RED. После реализации:

```powershell
dotnet restore .\Map.slnx --force-evaluate
dotnet build .\Map.slnx -c Release --no-restore
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Stage10F"
dotnet test .\tests\Map.Rendering.Tests\Map.Rendering.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Stage10F"
dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Stage10F"
dotnet test .\Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m
dotnet format .\Map.slnx --verify-no-changes --severity info --no-restore
dotnet package list --project .\Map.slnx --vulnerable --include-transitive --no-restore
dotnet package list --project .\Map.slnx --deprecated --include-transitive --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\drive-stage10f-fonts-bindings.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-stage-prompts.ps1 -RequireActive
git diff --check
python -m pre_commit install
python -m pre_commit run --all-files
```

Если `dotnet format` сохраняет подтверждённый baseline exit `2`, не повторять его: сравнить с BASE,
доказать отсутствие новых diagnostics в changed C# lines и отметить `PARTIAL`, не PASS.

## Required native UI verification

`scripts/drive-stage10f-fonts-bindings.ps1` запускает точный
`src\Map.App\bin\Release\net10.0\Map.App.exe` с isolated settings path и проверяет:

1. создать/открыть карту и text object;
2. открыть font catalog, найти/выбрать реальный family, previous/next, undo/redo;
3. save/reopen и data-check requested/resolved fallback;
4. открыть bindings, создать видимый rejected conflict;
5. rebind Text на свободное сочетание: old не действует, new действует;
6. help/tooltip показывает effective binding;
7. restart подтверждает persistence; reset возвращает defaults;
8. exact PID/EXE/hash/command line, settings/map JSON data и clean owned-process shutdown.

Driver сохраняет bounded `report.json` и небольшое число numbered PNG. UIA — основной путь;
coordinate fallback допустим только локально с явным result evidence. Каждый PNG просмотреть в
original resolution на hierarchy, clipping, overlap, focus, readable catalog/conflict/fallback.

## Git and stop condition

- Работать только в required Stage 10F worktree/branch от точного `STAGE10F_BASE`.
- Не использовать reset/restore/checkout/clean/stash/amend/rebase/history rewrite.
- Перед commit проверить root/branch/HEAD, exact diff/file count, сохранность owner docs и hook.
- Индексировать только точные Stage 10F files; не использовать `git add -A`.
- При выполненной приёмке один commit:

```text
feat(stage-10f): implement font catalog and editable bindings
```

- После commit зафиксировать HEAD/clean status и остановиться. No push/PR/release/merge.

## Required report

Использовать точную структуру `docs/07_CODEX_RUNBOOK.md`. Указать `STAGE10F_BASE`, итоговый HEAD,
commits, exact changed files, acceptance-by-acceptance status, automatic/native evidence, все
used/skipped tools с причиной, `FACT/PARTIAL/PROPOSED/BLOCKED`, residual risks и одну следующую
задачу: fresh independent review точного `STAGE10F_BASE..HEAD`.
