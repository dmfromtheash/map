# Stage 10F — Font catalog and editable bindings — final acceptance

## RESULT

`FACT / OWNER ACCEPTED FOR TRANSITION`.

Свежая независимая проверка зафиксировала неизменяемый feature snapshot
`766b81617874b4b8dbed2f4e781c94350305796a..77dd7afa3881542894f5d959f6a52fbb09e2491f`,
построчно проверила все `22` файла и связанные call sites, воспроизвела четыре in-scope finding,
а затем исправила их отдельным commit
`2e30e78b0b86a4ab070158b7c773200611c62d58` (`ACCEPTED_CODE_HEAD`).

Финальный кодовый snapshot прошёл обязательные automatic, native, data, security/reliability и
visual gates. Подтверждённых незакрытых Stage 10F defect не осталось. Прямое решение владельца
разрешает переход к Stage 11 без ещё одной Gate D-задачи; same-context проверка remediation не
называется новым независимым review.

Author evidence feature-коммита сохранено как исторический источник: Release build без warnings,
Stage10F `10/10 + 2/2 + 1/1`, full suite `732/732` и native `31/31`. Итоговые цифры ниже
получены свежей ролью reviewer и заменяют их как acceptance evidence.

## FINDINGS

- `S10F-F1`, `HIGH`, `src/Map.Platform.Windows/AppPaths.cs:21`,
  `src/Map.App/MainWindow.cs:55`: test mode при пустом, относительном или совпадающем с production
  override молча мог открыть настоящий `%LOCALAPPDATA%\Map\shortcuts.json`. RED-тест ожидал
  fail-closed, но исключение не возникало. Исправлено обязательным абсолютным изолированным путём,
  запретом production path и store без скрытого default fallback.
- `S10F-F2`, `MEDIUM`, `src/Map.Rendering/SystemFontCatalog.cs:8`: ограничение длины имени не
  ограничивало число системных семейств. RED: `5000` входов дали `5000` результатов вместо
  `4096`. Исправлено детерминированным cap `4096` с ordinal-ignore-case stable ordering.
- `S10F-F3`, `HIGH`, `src/Map.App/Views/ShortcutSettingsWindow.cs:180`,
  `src/Map.App/MainWindow.cs:598`: capture field поглощал bare `Tab`, поэтому физическая
  клавиатура не могла покинуть поле и достигнуть Save/Cancel/Reset; возврат focus после закрытия
  не был задан. Native RED трассировал focus только до `ShortcutGestureInput`. Исправлено:
  bare Tab/Shift+Tab оставлены стандартной навигацией, диалог awaited, focus возвращается на
  кнопку-источник.
- `S10F-F4`, `MEDIUM`, `scripts/drive-stage10f-fonts-bindings.ps1:365`: author driver не
  доказывал physical Tab, keyboard-only Save/Cancel/Reset, focus return и фактический DPI, а при
  старте мог принять кратковременное служебное окно за основное. Исправлено bounded physical-Tab
  traversal, keyboard actions, full-window bounds/DPI/process evidence и `40` checks.
- `S10F-A11Y-BASELINE`, `LOW / OUT OF SCOPE`: официальный Accessibility Insights FastPass для
  целого стартового окна `Map · 0.2.0` нашёл `4` существующие проблемы: `Page up` и
  `WindowChrome` с пустым BoundingRectangle, а также две одноимённые кнопки «Новая карта».
  Изменённое Stage 10F окно настроек к ним не относится; finding сохранён для Stage 11/native
  accessibility baseline и не исправлялся скрытым расширением scope.

## CHANGED

Feature snapshot (`77dd7afa...`) содержит исходные `22` Stage 10F файла: font catalog/UI,
requested/fallback persistence, единый shortcut registry/router, bounded atomic settings JSON,
transactional editor, tests, native driver и author docs.

Remediation commit (`2e30e78...`) изменяет ровно `7` файлов:

- `src/Map.Platform.Windows/AppPaths.cs` и `ShortcutSettingsStore.cs` — fail-closed изоляция
  пользовательских shortcut settings;
- `src/Map.Rendering/SystemFontCatalog.cs` — детерминированный hard cap каталога;
- `src/Map.App/Views/ShortcutSettingsWindow.cs` и `src/Map.App/MainWindow.cs` — свободная
  Tab-навигация и возврат focus;
- `tests/Map.Presentation.Tests/Stage10FFontsAndSettingsTests.cs` — RED/GREEN и adversarial
  settings/font boundaries;
- `scripts/drive-stage10f-fonts-bindings.ps1` — физический Tab, keyboard-only actions, DPI,
  полное окно и расширенное machine-readable evidence.

`.mapproj`, schemaVersion, package graph, renderer contract и сетевые границы не менялись.

## VERIFIED AUTOMATICALLY

- Exact preflight: root/worktree/branch/HEAD совпали; feature range — один commit,
  `A=10`, `M=12`, `D=0`, `R=0`, `+3069/-441`; main checkout владельца остался нетронут.
- `dotnet restore .\Map.slnx --force-evaluate` — exit `0`.
- `dotnet build .\Map.slnx -c Release --no-restore` — exit `0`, `0` warnings, `0` errors.
- Два meaningful RED: font count `expected 4096 / actual 5000`; settings test mode ожидал
  exception, но его не было. После fix оба targeted tests GREEN.
- Финальные Stage10F filters: Presentation `15/15`, Rendering `2/2`, Persistence `1/1`.
- `dotnet test .\Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m` — exit
  `0`, `738/738`: Core `215`, Persistence `129`, Rendering `32`, Presentation `229`,
  Assets `133`.
- Adversarial JSON: missing/relative/production test path, oversize file, excess nesting,
  `129` duplicate properties, overlong UTF-8 gesture и unwritable target — fail-safe; original
  не выдаётся за сохранённый, orphan temp отсутствует.
- NuGet vulnerable audit — уязвимых direct/transitive packages нет. Deprecated audit — только
  прежний test-only `xunit 2.9.3` Legacy; dependencies не менялись.
- `dotnet format .\Map.slnx --verify-no-changes --severity info --no-restore` — единственный
  run, exit `1`: только известный repository-wide whitespace/analyzer baseline в unrelated
  benchmark, Core, Stage 7/8, vector/composition файлах. В feature/remediation файлах новых
  formatter errors нет; gate честно `PARTIAL` и не повторялся.
- Codex Security Standard scan `c0e4c0b2-bbe9-49a8-b79c-c40f1d8267df` проверил все `12`
  изменённых production source items: `0` reportable security findings. Настроечный path
  boundary был real data-integrity finding, но не отдельной same-user security vulnerability.
- `scripts/doctor.ps1`, prompt contract checker и `git diff --check` — PASS.
- Установленный `pre-commit 4.6.2`: первоначально обнаружил mixed CRLF/LF в двух изменённых
  файлах; после узкой нормализации полный `--all-files` и commit hook прошли все gates.

## VERIFIED IN NATIVE UI

- Exact Release EXE:
  `src/Map.App/bin/Release/net10.0/Map.App.exe`, SHA-256
  `05993fe7fc6259533fa7ca14eb25b9f53570a17ad9bbbed35a209803583b65db`.
- Fresh driver evidence:
  `artifacts/stage10f-fonts-bindings/ui-smoke-20260830-190008/report.json`; result `PASS`,
  `40` checks. Три owned PID (`33960`, `13372`, `17188`) имели exact path/command line,
  `96 DPI / 100%`, bounds `1416×939`; после сценария running count `0`.
- Проверены: каталог/search/select/cycle Bahnschrift→Book Antiqua; exact undo/redo; missing
  requested family + отдельный Arial fallback; save/reopen/`scene.json`/production export;
  rejected conflict; `T`→`Alt+T`; old/new routing; suppression в text input; help/tooltips;
  restart persistence; reset-one/reset-all.
- Physical Tab прошёл от Search через все `67` command buttons к gesture field, Unassign,
  Reset, Reset all, Cancel и Save без focus trap. Keyboard-only Cancel не изменил live profile,
  Save/Reset сработали, focus вернулся на кнопку открытия settings.
- Изолированные settings/map/recovery/export находились только внутри ignored artifact directory;
  настоящий профиль пользователя не читался и не менялся.
- В original resolution просмотрены шесть numbered PNG и export: catalog/disclosure читаемы,
  conflict выделен и Save disabled, focus frame виден, long Russian text/help не обрезаны,
  persisted `Alt+T` согласован с UIA. Export содержит glyph pixels и не пуст, но существующий
  default dark text на dark map даёт низкий контраст — это прежний text-style риск вне Stage 10F.
- Accessibility Insights FastPass был запущен на target `window 'Map · 0.2.0'`; автоматическая
  часть дала описанные выше `4` baseline findings. Изменённая settings-поверхность отдельно
  доказана project-owned UIA и физической клавиатурой.

## TOOL MATRIX RESULT

- .NET build/xUnit/Git/pre-commit/doctor — `USED / PASS`; compile, regression, contract и scope.
- Targeted adversarial files — `USED / PASS`; bounds, corruption, duplicates, write failure,
  test isolation и сохранность original.
- Release EXE + project UIA + physical keys — `USED / PASS`; `40` native checks.
- Screenshot/vision + machine-readable PNG/data — `USED / PASS`; `6` screenshots и export.
- Accessibility Insights — `USED / PARTIAL`; valid whole-window FastPass выполнен, `4`
  out-of-scope baseline failures; официальный Tab Stops record не подменяет более полный
  project-owned physical-Tab trace изменённого диалога.
- Codex Security — `USED / PASS`; полный scoped scan, `0` reportable findings.
- NuGet audit — `USED / PASS`; vulnerable packages отсутствуют, deprecated baseline известен.
- Formatter — `USED / PARTIAL`; один разрешённый legacy-baseline run.
- Coverage/Stryker — `SKIPPED`; RED/GREEN и исполняемый native trace прямо доказали новые ветви;
  отдельного mutation-risk после этого не осталось.
- Benchmark/diagnostics — `SKIPPED`; Stage 10F не заявляет performance budget и не меняет hot
  path.
- Shaping/upstream research/new packages — `SKIPPED`; resolver/rendering/shaping не менялись.
- Alternate-DPI controller — `SKIPPED`; безопасного project-owned per-process scale нет;
  системный Windows scale владельца не изменялся.

## NOT VERIFIED

- Реальный Windows desktop scale `150%`/`200%` не менялся. На фактическом `100%` проверены
  DPI/bounds/minimum layout и длинные русские строки; прямое решение владельца разрешает переход.
- Набор и fallback локальных системных шрифтов на другом ПК закономерно может отличаться.
- Global formatter остаётся `PARTIAL` из-за зафиксированного legacy baseline вне Stage 10F.
- Полный ручной Accessibility Insights Tab Stops session не сохранялся; changed dialog имеет
  более строгий автоматизированный physical-Tab evidence.

## RISKS

- Existing default text color может давать низкий контраст на тёмной карте; Stage 10F не менял
  начальный Text style. Это отдельный будущий UI/accessibility finding, не причина переписывать
  font/bindings acceptance.
- Четыре FastPass findings стартового окна требуют отдельного Stage 11 accessibility baseline;
  исправление не должно смешиваться с уже принятым Stage 10F.
- Machine-local font differences и legacy formatter findings должны оставаться видимыми в
  последующих release notes/evidence.

## GIT/SCOPE

- Branch: `stage/10f-fonts-bindings`; worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- `REVIEW_BASE=766b81617874b4b8dbed2f4e781c94350305796a`.
- `FEATURE_HEAD=77dd7afa3881542894f5d959f6a52fbb09e2491f`.
- `ACCEPTED_CODE_HEAD=2e30e78b0b86a4ab070158b7c773200611c62d58`.
- Remediation commit: `fix(stage-10f): resolve final acceptance findings`, `7` files,
  `+258/-23`; без amend/rebase/history rewrite.
- `C:\Projects\Map` с owner changes не переключался, не очищался, не индексировался и не
  изменялся. Ignored `bin/`, `obj/`, `artifacts/` не индексировались.
- Push, PR, merge, tag, release, публикация и package addition не выполнялись.

## NEXT

Запустить единственный активный контракт
`docs/prompts/STAGE_11_PERFORMANCE_PACKAGING_RC.md`. Дополнительные Stage 10F
review/test/fix/commit задачи не требуются.
