# Stage 10F — Font catalog and editable bindings — author verification

## RESULT

`FACT / PARTIAL — implementation checkpoint выполнен и готов к fresh independent review точного
STAGE10F_BASE..HEAD.`

`FACT`: Map получает локальные семейства шрифтов через уже имеющийся Skia runtime, позволяет
искать, выбирать и переключать шрифт выбранного текста, сохраняет requested family отдельно от
реального fallback и проводит каждую смену через существующую undoable-команду. Формат `.mapproj`,
его `schemaVersion`, renderer и единый `TextFontResolver` не менялись.

`FACT`: прежние сочетания сведены в один registry со стабильными command IDs и реальным router;
тот же профиль формирует tooltips/help и transactional-редактор настроек. Профиль хранится вне
карты в bounded atomic JSON, отклоняет пересекающиеся конфликты, поддерживает unassign,
reset-one/reset-all и переживает restart.

`PARTIAL`: это self-check исполнителя, а не независимая приёмка. Full formatter сохраняет
известный legacy baseline. Alternate DPI, физический Tab traversal и отдельная clean-context
UX-критика не выполнены.

## CHANGED

- `docs/00_START_HERE.md` — текущий Stage 10F checkpoint и следующая независимая проверка.
- `docs/06_DEVELOPMENT_ROADMAP.md` — честный `FACT / PARTIAL` author-status с остаточными gates.
- `docs/STATE_HANDOFF.md` — точный BASE, автоматические/native evidence и единственная следующая
  задача.
- `docs/reviews/STAGE_10F_FONTS_BINDINGS_VERIFICATION.md` — этот author-report.
- `scripts/drive-stage10f-fonts-bindings.ps1` — bounded 16-step native UIA/physical-key driver с
  isolated map, recovery и settings paths.
- `src/Map.App/MainWindow.cs` — единый key router, shared profile, settings load/save, generated
  help и вызов transactional editor.
- `src/Map.App/Services/ShortcutKeyAdapter.cs` — canonical conversion Avalonia logical/physical key
  events в registry input.
- `src/Map.App/Services/TestMode.cs` — opt-in isolated settings path только для project driver.
- `src/Map.App/Views/DashboardView.cs` — dashboard tips из effective shared profile.
- `src/Map.App/Views/EditorView.cs` — локальный font catalog/search/navigation, requested/fallback
  disclosure, settings button и живые shortcut chips/tooltips/help text.
- `src/Map.App/Views/ShortcutSettingsWindow.cs` — search/group/capture/conflict/unassign/reset/save
  transactional UI.
- `src/Map.Platform.Windows/AppPaths.cs` — пользовательский путь `shortcuts.json` вне `.mapproj`.
- `src/Map.Platform.Windows/ShortcutSettingsStore.cs` — known-ID bounded load и atomic save с
  сохранением повреждённого оригинала.
- `src/Map.Presentation/ViewModels/EditorLabels.cs` — удалён второй hardcoded источник tool keys.
- `src/Map.Presentation/ViewModels/EditorShortcutSettings.cs` — definitions, parser, conflicts,
  resolve, overrides и resets единого профиля.
- `src/Map.Presentation/ViewModels/EditorViewModel.cs` — injectable local catalog, search,
  exact-one-text guards, selection/cycle и resolution state.
- `src/Map.Rendering/SystemFontCatalog.cs` — lazy Skia enumeration, trim, bounded dedupe и stable
  sort.
- `tests/Map.Persistence.Tests/Stage10FFontRoundTripTests.cs` — requested/fallback round-trip без
  schema change.
- `tests/Map.Presentation.Tests/EditorPresentationStateTests.cs` — прежняя проверка labels без
  удалённого hardcoded shortcut API.
- `tests/Map.Presentation.Tests/Stage10FFontsAndSettingsTests.cs` — fonts, undo/redo, exact-one
  selection, settings round-trip, unknown/corrupt input и backup policy.
- `tests/Map.Presentation.Tests/Stage10FShortcutRoutingTests.cs` — old/new binding и нормальный
  Select-mode font routing.
- `tests/Map.Rendering.Tests/Stage10FFontCatalogTests.cs` — local catalog и bounded resolver
  behavior.

## VERIFIED AUTOMATICALLY

- Meaningful RED: новый routing test изменил Text binding на `Alt+T` и доказал, что прежний
  hardcoded MainWindow route всё ещё выполнял `T`; после перехода на registry test стал GREEN.
- Дополнительный native-triggered RED: `font-next` не разрешался при выбранном тексте в
  Select-mode (`expected font-next / actual null`). После узкой смены контекста на Selection новый
  regression test и весь Stage10F Presentation filter прошли `10/10`.
- `dotnet restore Map.slnx --force-evaluate` — exit `0`.
- `dotnet build Map.slnx -c Release --no-restore` — exit `0`, `0` warnings, `0` errors.
- Presentation Stage10F filter — `10/10`; Rendering — `2/2`; Persistence — `1/1`.
- `dotnet test Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m` — exit `0`,
  `732/732`: Core `215`, Persistence `129`, Rendering `32`, Presentation `223`, Assets `133`.
- `dotnet format Map.slnx --verify-no-changes --severity info --no-restore` — один разрешённый run,
  exit `1`: прежние whitespace/analyzer findings в benchmark/Core/Stage7/Stage8 и legacy analyzer
  baseline; изменённые Stage10F files не получили новой whitespace error. По verification budget
  повтор не выполнялся, gate остаётся `PARTIAL`.
- NuGet vulnerable audit — exit `0`, vulnerable direct/transitive packages не найдены.
- NuGet deprecated audit — exit `0`; только прежний test-only `xunit 2.9.3` имеет Legacy status.
  Package references и dependency graph не менялись.
- Финальный affected fix gate: Presentation Stage10F `10/10`, затем Release App build — exit `0`,
  `0` warnings/errors; полный solution run не повторялся без changed-risk trigger.
- `scripts/check-stage-prompts.ps1 -RequireActive` — exit `0`, ровно один ACTIVE contract
  Stage 10F. `git diff --check` — exit `0`.
- Первый `python -m pre_commit run --all-files`: prompt/version/large-file/case/JSON/merge/
  symlink/submodule/branch/lychee gates прошли; `mixed-line-ending` указал точные 12 Windows-файлов.
  Они нормализованы штатным fixer в CRLF, после чего повторён только упавший hook — PASS. Новые
  untracked files проверяются адресно на финальном snapshot и повторно commit-hook после staging.

## VERIFIED IN NATIVE UI

- Проверен точный Release EXE
  `src/Map.App/bin/Release/net10.0/Map.App.exe`, SHA-256
  `c0e777580e6d1441158578bd02e44d00cc1b5d4c202ac434c2bfca81f4e06f65`; каждый process path и
  command line совпал, после сценария running count равен `0`.
- Project-owned driver открыл настоящее Avalonia Win32-окно и прошёл `16` шагов / `31` checks без
  координат: UI Automation для controls и физические Win32 key events только для настоящего
  routing/capture.
- Каталог показал несколько локальных семей; case-insensitive поиск `bahn` выбрал `Bahnschrift`,
  физическая `]` переключила на `Book Antiqua`, затем exact undo/redo вернули прежнее/новое
  requested состояние.
- Save/restart/reopen сохранил requested `Book Antiqua`. Missing request
  `Map Stage 7 Missing Font` остался в `scene.json`, отдельно показал `Arial` как локальный
  fallback и прошёл production export resolver.
- Shortcut editor показал default `T`; ввод `V` создал видимый overlapping conflict и disabled
  Save. Свободное `Alt+T` сохранилось в isolated JSON вне `.mapproj`; старая `T` перестала
  действовать, новая `Alt+T` выполнила реальный Text command, а фокус TextBox подавил global route.
- Tooltip/UIA HelpText и generated help прочитали `Alt+T` из того же профиля. После нового process
  override сохранился; reset-one и reset-all вернули `T`, удалили default из compact overrides и
  восстановили default routing.
- В original/high resolution просмотрены `01-font-catalog.png`,
  `02-missing-font-fallback.png`, `03-shortcut-conflict.png`, `04-rebound-help.png`,
  `05-persisted-restart.png` и production export: hierarchy/readability/conflict disabled state/
  fallback disclosure/active Text `Alt+T` не имеют blocking clipping или overlap.
- Machine-readable evidence:
  `artifacts/stage10f-fonts-bindings/ui-smoke-20260830-170950/report.json`; карта, isolated settings,
  backup, export и numbered PNG лежат в том же ignored artifact directory.

## TOOL MATRIX RESULT

- Presentation/xUnit — `USED`; registry, parsing, conflict contexts, old/new resolve, settings
  reliability, exact-one text, font order и undo/redo.
- Rendering/Persistence xUnit — `USED`; system catalog bounds, requested/fallback resolver и
  `.mapproj` round-trip без schema change.
- Full solution build/tests — `USED ONCE`; shared-project regression boundary `732/732`.
- Project-owned native UIA + physical key injection — `USED`; exact executable, real commands,
  dialog capture/conflict, restart/reset, map/settings data and process cleanup.
- Visual inspection — `USED`; все пять итоговых screenshots и export открыты отдельно.
- NuGet audits — `USED`; новых/vulnerable packages нет, deprecated baseline зафиксирован.
- Formatter — `USED / PARTIAL`; один legacy-baseline run, без запрещённого повторения.
- Codex Security / web research — `SKIPPED`; нет новой зависимости, network/trust boundary,
  secret, executable parser или нового внешнего источника.
- Stryker, coverage, benchmark, shaping probe, Accessibility Insights — `SKIPPED`; stage contract
  прямо запрещает их без нового changed-risk trigger.
- External desktop controllers / coordinate input — `SKIPPED`; project-owned driver дал
  достаточное native evidence без координат.

## NOT VERIFIED

- Fresh independent Codex review точного `STAGE10F_BASE..HEAD` ещё не выполнен.
- Настоящие Windows scale `150%` и `200%` не применялись.
- Физическая Tab-последовательность и отдельный Accessibility Insights FastPass не выполнялись;
  standard UIA roles/names/state/focus проверены только адресно.
- Каталог закономерно зависит от шрифтов текущего пользователя Windows; другие машины не
  обязаны иметь тот же список.
- Отдельная clean-context UX-критика не выполнялась.

## RISKS

- До independent review остаётся риск, что авторский тест и реализация разделяют одну ошибочную
  интерпретацию контекста или persistence boundary.
- Alternate DPI/keyboard traversal могут обнаружить layout/focus проблему, не видимую в текущем
  native run.
- Legacy formatter baseline затрудняет доказательство глобально чистого format gate; текущий
  Stage10F diff не следует объявлять formatter-PASS.
- Machine-local font availability неизбежно меняет resolved fallback между ПК, при этом requested
  family сохраняется намеренно и не переписывается.

## GIT / SCOPE

- Branch: `stage/10f-fonts-bindings`.
- `STAGE10F_BASE`: `766b81617874b4b8dbed2f4e781c94350305796a`.
- HEAD до feature commit совпадал с BASE; итоговый HEAD — единственный локальный commit
  `feat(stage-10f): implement font catalog and editable bindings`, его hash фиксируется финальным
  handoff после commit без amend/rebase.
- Worktree: `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Плановый inventory: `A=10`, `M=12`, `D=0`, `R=0`, всего `22` файла; полный список — раздел
  `CHANGED`. `bin/`, `obj/`, `artifacts/` и временные native settings/maps не индексируются.
- Dirty owner checkout `C:\Projects\Map` не переключался, не очищался, не индексировался и не
  изменялся. `.mapproj` schema, existing resolver/render semantics и package references не менялись.
- Push, PR, release, tag и merge не выполнялись.

## NEXT

- Только свежий Codex `gpt-5.6-sol`, reasoning `xhigh`, независимо проверяет точный диапазон
  `766b81617874b4b8dbed2f4e781c94350305796a..HEAD`, включая authored-file inventory, dangerous
  settings corruption path, real keyboard old/new route, font requested/fallback data и native
  evidence. Stage 11 не начинать в этой задаче.
