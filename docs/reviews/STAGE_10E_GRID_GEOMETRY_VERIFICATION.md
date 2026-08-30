# Stage 10E — Grid geometry — author verification

## OWNER DECISION

`Stage 10E: FACT / OWNER ACCEPTED` на точном HEAD
`78a5b98665ed57c2a00a64335371e7f0c6132dd9`.

Это прямое решение владельца о завершении этапа для перехода к Stage 10F без дополнительного
Gate D. Оно не является утверждением о новом независимом acceptance-проходе и не меняет
исторические `PARTIAL`, `PROPOSED`, известные ограничения или отсутствующие доказательства ниже.

## RESULT

`PARTIAL — implementation checkpoint создан и готов к независимой проверке точного BASE..HEAD.`

`FACT`: в author worktree реализованы четыре самостоятельные геометрии `Square`,
`IsometricSquares`, `HexFlat`, `HexPointy`; единый чистый evaluator используется для topology,
snap и rendering; полный Grid property sheet редактирует все свойства одной undoable-командой,
сохраняет их без повышения `schemaVersion` и воспроизводит после reopen/export.

`PARTIAL`: это self-check исполнителя, а не независимая приёмка. Golden PNG остаются
`PROPOSED` до fresh review. Mutation evidence выявил большой остаток survived/timeout mutants.

## CHANGED

- `Map.slnx` — зарегистрирован dependency-free инструмент предложения/экспорта grid golden.
- `docs/00_START_HERE.md` — актуальная точка входа и следующий независимый review.
- `docs/06_DEVELOPMENT_ROADMAP.md` — честный author-checkpoint Stage 10E без объявления приёмки.
- `docs/STATE_HANDOFF.md` — точный BASE, доказательства, остатки и единственная следующая задача.
- `docs/prompts/STAGE_10D_OBJECT_INSPECTORS.md` — Stage 10D помечен historical contract.
- `docs/prompts/STAGE_10E_GRID_GEOMETRY.md` — активный контракт этой реализации.
- `docs/reviews/STAGE_10E_GRID_GEOMETRY_VERIFICATION.md` — этот author self-check.
- `scripts/drive-stage10e-grid-geometry.ps1` — bounded project-owned native UIA driver.
- `src/Map.App/Views/EditorView.cs` — стандартные radio/edit/checkbox controls полного Grid sheet,
  синхронизация, locked read-only state и доступные имена.
- `src/Map.App/Views/Ui.cs` — локально настраиваемая ширина caption для полных Grid-подписей.
- `src/Map.Core/Documents/TextNotesGrid.cs` — отдельный `IsometricSquares` и строгая валидация
  каждого persistable Grid-поля без изменения схемы.
- `src/Map.Core/Documents/GridGeometryContract.cs` — чистая bounded topology/snap-модель всех
  четырёх геометрий, offset/rotation/subdivisions и deterministic tie-break.
- `src/Map.Core/Editing/GridSnap.cs` — общий evaluator для object/node/portal snap со стабильными ID.
- `src/Map.Presentation/ViewModels/EditorLabels.cs` — точные русские labels четырёх геометрий.
- `src/Map.Presentation/ViewModels/EditorViewModel.cs` — no-op/invalid/locked guards и одна
  `ReplaceGridLayerCommand` на одно принятое свойство.
- `src/Map.Rendering/SceneRenderer.cs` — один evaluator для viewport, preview и export; export
  следует `IncludeInExport`, а не editor visibility.
- `tests/Map.Core.Tests/Editing/Stage10EGridGeometryTests.cs` — 12 topology/oracle/snap/bounds тестов.
- `tests/Map.Persistence.Tests/Stage10EGridRoundTripTests.cs` — 13 round-trip/backward/invalid тестов.
- `tests/Map.Presentation.Tests/Stage10EGridViewModelTests.cs` — 4 property/undo/validation теста.
- `tests/Map.Rendering.Tests/Stage10EGridRenderingTests.cs` — 4 read-only golden/parity/export теста.
- `tests/fixtures/grid-goldens/square.png` — `PROPOSED` Square expected.
- `tests/fixtures/grid-goldens/isometric-squares.png` — `PROPOSED` IsometricSquares expected.
- `tests/fixtures/grid-goldens/hex-flat.png` — `PROPOSED` HexFlat expected.
- `tests/fixtures/grid-goldens/hex-pointy.png` — `PROPOSED` HexPointy expected.
- `tools/Map.GridGoldenProposer/Map.GridGoldenProposer.csproj` — отдельный инструмент без пакетов.
- `tools/Map.GridGoldenProposer/Program.cs` — write-once `--propose` и экспорт сохранённого проекта.

## VERIFIED AUTOMATICALLY

- Meaningful RED: новый Core-тест потребовал четыре enum-значения и упал `expected 4 / actual 3`;
  инфраструктура тестов при этом собрала и запустила тест нормально.
- `dotnet restore Map.slnx --force-evaluate` — exit `0`.
- `dotnet build Map.slnx -c Release --no-restore` на финальном production snapshot — exit `0`,
  `0` warnings, `0` errors.
- Core Stage10E filter — exit `0`, `12/12`; доказаны отдельная изометрическая basis с линиями
  приблизительно `±30°`, отличие от square+45°, negative/huge coordinates, tie-break, четыре zoom,
  hex rendered vertices/edges, bounded work и object/node/portal snap со стабильными ID.
- Persistence Stage10E filter — exit `0`, `13/13`; четыре геометрии и все свойства переживают
  save/reopen при `schemaVersion 1`, старые Square/Hex samples byte-unchanged, unknown/malformed
  значения дают контролируемый отказ без переписывания файла.
- Rendering Stage10E filter — exit `0`, `4/4`; четыре topology различимы, viewport/preview/export
  используют один evaluator, export policy независима от editor visibility, expected hashes не
  изменились, reference/actual совпали с `0` changed pixels. Expected PNG не регенерировались.
- Presentation Stage10E filter — exit `0`, `4/4`; точные labels/values, одна history entry на
  свойство, exact undo/redo, no-op/invalid/locked не меняют документ и историю.
- `dotnet test Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m` — exit `0`,
  `716/716`: Core `212`, Rendering `30`, Persistence `128`, Assets `133`, Presentation `213`.
- Bounded Stryker 4.16.0 запускался один раз только по `GridGeometryContract.cs` и `GridSnap.cs`:
  `411` mutants в отчёте, из них `130` killed, `195` survived, `6` timeout, `35` no coverage,
  `13` compile error, `32` ignored; console mutation score `37,16%`. Survived-классы были
  сгруппированы; после них усилены hex-vertex/edge/orientation и multi-node portal oracles.
  Повтор Stryker запрещён verification budget, поэтому gate остаётся `PARTIAL`; raw JSON:
  `artifacts/stage10e-grid-geometry/stryker/reports/mutation-report.json`.
- Полный `dotnet format --verify-no-changes --severity info --no-restore` сохранил известный
  baseline exit `2`. После исправления Stage10E whitespace узкая changed-files диагностика также
  вернула `2`, но показала `0` whitespace и `0/54` diagnostics на изменённых строках; все 54 строки
  через `git blame` принадлежат точному BASE. Gate честно `PARTIAL`, а не PASS.
- NuGet vulnerable audit — exit `0`, уязвимых direct/transitive packages нет.
- NuGet deprecated audit — exit `0`; только уже существующий baseline xUnit 2.9.3 помечен Legacy.
  Package references и зависимости этим срезом не менялись.
- `scripts/doctor.ps1` — exit `0`, все environment checks `[OK]`.
- `scripts/check-stage-prompts.ps1 -RequireActive` — exit `0`, ровно один active Stage10E contract.
- `python -m pre_commit install` и `python -m pre_commit run --all-files` — exit `0`.
- `git diff --check` — exit `0`.

## VERIFIED IN NATIVE UI

- Проверен точный Release EXE
  `src/Map.App/bin/Release/net10.0/Map.App.exe`, SHA-256
  `a909007ecd7f33315bc9bdfba06ab20dd7a631f5d5fdb4f60fa4329fe29e8cef`; process path совпал.
- Project-owned UIA driver открыл настоящее Avalonia-окно `2500×1460` при фактических
  `96 DPI / 100%`, создал карту `1600×1000` и выполнил `108` checks.
- Проверены четыре `RadioButton`, восемь `Edit`, два `CheckBox`: роли, полные русские accessible
  names, enabled/selected/checked state, focus и locked readable/read-only behavior.
- Для всех четырёх геометрий выбор подтверждён через UIA и `scene.json`; pairwise screenshot RMSE:
  `0,0240931`, `0,0268824`, `0,0293989`.
- Введены и видимы exact values: `96.5`, `4`, `2.75`, `#CC224466`, `65%`, `13.5`, `-17.25`,
  `27°`, snap on, include-in-export on. Поворот undo `27→0` и redo `0→27` — одна команда.
- Object centre, первый vector node и первый portal привязаны к реальной IsometricSquares lattice;
  residual не хуже `1,43e-14`, object/node/portal и все line node IDs сохранены.
- После save/close/reopen те же значения, geometry и toggles прочитаны из UI и `scene.json`.
- Экспорты `1600×1000` с сеткой и без неё различаются с RMSE `0,0321348`; содержимое объекта
  сохраняется в обоих. После сценария `Map.App` process count равен `0`.
- Визуально просмотрены все восемь финальных PNG: topology различимы; property sheet не обрезан;
  focus видим; reopened state совпадает; grid присутствует/отсутствует согласно export policy.
- Evidence: `artifacts/stage10e-grid-geometry/ui-smoke-20260830-021038/report.json` и
  `01-square.png` … `08-export-without-grid.png` в том же каталоге.

## TOOL MATRIX RESULT

- Core/xUnit — `USED`; deterministic topology, independent oracle, snap, invalid/bounds evidence.
- Persistence/xUnit — `USED`; round-trip, schema/backward compatibility и strict failure evidence.
- Rendering/golden — `USED`; write-once proposal tool, read-only comparison и export parity.
- Stryker — `USED / PARTIAL`; один bounded двухфайловый run, raw counts и усиление oracles.
- Release project-owned UIA — `USED`; exact EXE/process, controls, values, reopen/export, cleanup.
- Visual inspection — `USED`; отдельно просмотрены восемь UI/export и reference/actual golden PNG.
- Accessibility — `USED / PARTIAL`; UIA доказал standard roles/names/state/focus. Accessibility
  Insights установлен и подписан Microsoft, но GUI-only FastPass не запускался внешним desktop
  controller: Stage contract запрещает такой controller, а безопасного background CLI у приложения нет.
- NuGet/security boundary — `USED` package vulnerability audit; отдельный Codex Security scan
  `SKIPPED`, потому что нет новой зависимости, network/trust boundary, secret или executable parser.
- Browser/web research — `SKIPPED`; acceptance основан на принятых clean-room документах и локальном
  приложении, нового source admission не требовалось.
- External desktop controllers — `SKIPPED`; прямо запрещены Stage 10E contract, использован только
  project-owned UIA driver.

## NOT VERIFIED

- Fresh independent Codex review точного `BASE..HEAD` ещё не выполнен.
- `PROPOSED` golden fixtures ещё не приняты независимым reviewer.
- Настоящий Accessibility Insights FastPass не выполнен; UIA-проверка не называется FastPass.
- Физическая Tab-последовательность не вводилась; доказан программный focus и standard roles.
- Реальные Windows scale `150%` и `200%` не применялись; наблюдался только фактический `100%`.
- Runtime GPU/software backend не раскрывается текущим Map.App; доказан только SkiaSharp lease path.

## RISKS

- Низкий mutation score, `195` survived и `6` timeout означают, что исчерпывающая устойчивость
  topology/snap к мутациям не доказана, несмотря на усиленные acceptance-oracles.
- `PROPOSED` expected PNG могут закрепить ошибочную форму, пока fresh reviewer независимо не
  сопоставит их с contract и исходной математикой.
- Alternate DPI и физический keyboard traversal могут выявить layout/focus дефект, не видимый при
  текущих `96 DPI / 100%`.
- Renderer backend неизвестен во время evidence run; функциональный bitmap результат доказан,
  конкретный GPU path — нет.

## GIT / SCOPE

- Branch: `stage/10e-grid-geometry`.
- BASE: `942fb07f015122c640174691d908717df1c17f76`.
- HEAD: единственный локальный commit, содержащий этот отчёт; точный hash фиксируется финальным
  handoff после commit без amend/rebase.
- Worktree: `C:\Users\Leshe\.codex\worktrees\stage10e-grid-geometry\Map`.
- Плановый inventory: `A=14`, `M=12`, `D=0`, `R=0`, всего `26` файлов; полный список — раздел
  `CHANGED`. `bin/`, `obj/`, `artifacts/`, temp и external Accessibility Insights не индексируются.
- Dirty checkout `C:\Projects\Map` не переключался, не очищался, не индексировался и не изменялся
  этой задачей. Push, PR, release и tag не выполнялись.

## NEXT

- Только свежий Codex `gpt-5.6-sol`, reasoning `xhigh`, независимо проверяет точный Stage 10E
  `942fb07f015122c640174691d908717df1c17f76..HEAD`. Stage 10F не выполнять в этой задаче.
