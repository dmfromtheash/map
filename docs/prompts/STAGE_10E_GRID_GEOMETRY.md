# Stage 10E — отдельная изометрическая сетка и полный редактор свойств Grid

> Status: ACTIVE

Ты выполняешь один полный вертикальный этап Map — **Stage 10E Grid geometry**. Доведи его от доменной геометрии и undo/redo до сохранения, рендеринга, нативного интерфейса и проверяемых evidence. Не переходи к независимой приёмке или следующему roadmap-этапу в этой задаче.

## Execution profile

- Model: `gpt-5.6-sol`.
- Reasoning: `high`.
- Role: `implementation` и конечный author self-check. Это не fresh independent review.
- Autonomy: локальные read-only проверки и обратимые изменения внутри Stage 10E разрешены. Не спрашивай подтверждение для обычной реализации в установленном scope.
- Остановись перед новой dependency, изменением архитектуры или schemaVersion, новым продуктовым решением, расходами, внешней публикацией, необратимым действием или существенным расширением scope.
- Git authority: разрешён **один локальный stage-коммит** только после обязательных проверок. Push, PR, release, tag и публикация запрещены.

## Source of truth and baseline

Прочитай полностью и по порядку:

1. `AGENTS.md`;
2. `CODEX.md`;
3. `docs/00_START_HERE.md`;
4. `docs/STATE_HANDOFF.md`;
5. Stage 10 и 10E в `docs/06_DEVELOPMENT_ROADMAP.md`;
6. `docs/DECISIONS.md`, применяя все accepted-записи, особенно `DEC-003`, `DEC-004`, `DEC-005`, `DEC-007`, `DEC-009`, `DEC-010`, `DEC-011`, `DEC-012`, `DEC-015` и `DEC-016`;
7. `docs/02_FUNCTIONAL_SPEC.md`, раздел Grid;
8. `docs/03_UX_UI_SYSTEM.md`;
9. `docs/04_TECH_ARCHITECTURE.md`;
10. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`;
11. `docs/08_QA_AND_ACCEPTANCE.md`;
12. `docs/research/OBJECT_AND_GRID_PARITY.md`;
13. `docs/research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md`, только разделы, относящиеся к Grid;
14. `docs/reviews/STAGE_10D_OBJECT_INSPECTORS_VERIFICATION.md`;
15. `docs/07_CODEX_RUNBOOK.md`;
16. `docs/11_CODEX_VERIFICATION_PROTOCOL.md`;
17. этот контракт, сохранённый как `docs/prompts/STAGE_10E_GRID_GEOMETRY.md`.

Фактический принятый baseline:

```text
Accepted BASE: 942fb07f015122c640174691d908717df1c17f76
Expected branch: stage/10e-grid-geometry
Required worktree: C:\Users\Leshe\.codex\worktrees\stage10e-grid-geometry\Map
```

`C:\Projects\Map` сейчас является отдельным dirty checkout со старыми пользовательскими изменениями. Не редактируй, не очищай, не индексируй и не переключай его.

Если ветки и Stage 10E worktree ещё нет, разрешено создать `stage/10e-grid-geometry` от точного BASE в указанном отдельном worktree. Если ветка или путь уже существуют, ничего не удаляй и не пересоздавай: сначала проверь их состояние и остановись при расхождении с BASE или при неизвестных изменениях.

До первой правки выведи:

- фактические worktree, branch и полный HEAD;
- `git status --porcelain=v2 --branch`;
- подтверждение, что HEAD равен BASE;
- существующие изменения и их владельца;
- короткий план внутренних checkpoint;
- конечный verification budget из этого prompt.

В базовых документах ещё могут оставаться строки о необходимости проверить research/release checkpoint. Финальный Gate D диапазона `8de17de27546783bdd6247cb4df094d9df505711..942fb07f015122c640174691d908717df1c17f76` уже принят, и владелец прямо разрешил переход к 10E. Зафиксируй это расхождение как факт handoff, а не как блокер.

Перед production-кодом:

1. сохрани этот контракт в `docs/prompts/STAGE_10E_GRID_GEOMETRY.md`;
2. измени статус `docs/prompts/STAGE_10D_OBJECT_INSPECTORS.md` на `HISTORICAL CONTRACT` с правдивой пометкой о завершении;
3. проверь, что активным остаётся только Stage 10E;
4. выполни один раз:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-stage-prompts.ps1 -RequireActive
```

## Outcome

Пользователь выбирает слой сетки в настоящем нативном окне Map, видит фактические значения всех свойств, выбирает одну из четырёх самостоятельных геометрий — квадратную, изометрическую, hex-flat или hex-pointy — точно настраивает сетку, отменяет и возвращает изменения, сохраняет карту, открывает её снова и получает тот же результат в редакторе и экспорте.

`IsometricSquares` должна быть настоящей отдельной косоугольной топологией с согласованными render и snap, а не `Square` с поворотом на `45°`.

## Current facts and gaps

- `FACT`: доменная модель уже содержит `Square`, `HexFlat`, `HexPointy`.
- `FACT`: рендеринг поддерживает квадратную и две шестиугольные геометрии.
- `FACT`: object, vector node и portal могут привязываться к существующей сетке независимо от zoom.
- `FACT`: `GridDefinition` уже хранит cell size, subdivisions, line width, color, opacity, offset X/Y, rotation, snap и include-in-export; эти значения проходят через `.mapproj`.
- `FACT`: Grid layer уже участвует в порядке слоёв, visibility/lock и layer mask.
- `PARTIAL`: текущий UI позволяет только циклически переключать три геометрии и менять флаг экспорта.
- Gap закрываемого этапа: отдельная `IsometricSquares`, полный grid property sheet, общий точный render/snap-контракт, backward-compatible persistence, golden fixtures и нативная доказательная проверка четырёх presets.

## In scope

- отдельное значение `GridGeometry.IsometricSquares`;
- одна собственная детерминированная изометрическая топология с двумя семействами линий примерно `±30°` в локальных координатах сетки;
- единый математический контракт basis/offset/rotation/subdivisions, используемый рендерингом и snap;
- snap точки, объекта, vector node и portal для всех четырёх геометрий;
- полный property sheet выбранного Grid layer:
  - geometry/preset;
  - cell size;
  - subdivisions;
  - line width;
  - color;
  - opacity;
  - offset X;
  - offset Y;
  - rotation;
  - snap enabled;
  - include in export;
- понятные русские подписи, единицы, фактические значения и стабильные `AutomationId`;
- одна завершённая пользовательская операция — одна `IEditCommand` и одна запись undo history;
- persistence новой geometry, save/reopen и совместимость со старыми `.mapproj`;
- одинаковая геометрия в viewport, preview и export;
- отсутствие сетки в export при выключенном `IncludeInExport`, независимо от её видимости в редакторе;
- валидация числовых свойств и защита renderer от неограниченного количества линий;
- адресные Core, Persistence, Rendering и Presentation-тесты;
- отдельные детерминированные golden fixtures для четырёх геометрий;
- project-owned Release UIA driver, нативные screenshots, vision-review и accessibility evidence;
- обновление stage prompt, handoff и author verification report;
- ожидаемые области: `src/Map.Core`, `src/Map.Persistence`, `src/Map.Rendering`, `src/Map.Presentation`, `src/Map.App`, соответствующие `tests`, `scripts` и stage-документы.

Список областей не является разрешением менять несвязанный код.

## Out of scope

- Stage 10F: licensed font catalog, font navigation и редактируемые bindings;
- Stage 9B–9E, Stage 11, installer, updater, packaging и release;
- исправление оставшихся UX-гейтов 10B/10C;
- новые глобальные shortcuts, кроме сохранения существующего рабочего `G` для snap;
- переработка Land/Brush `GridBlock`, scatter, composition scale или других соседних инструментов; проверить только отсутствие регрессии от нового enum;
- новая dependency, новый формат контейнера или скрытое изменение `schemaVersion`;
- копирование названий, иконок, ассетов, layout, цветов или trade dress Inkarnate;
- новое browser/account research: принятых clean-room документов достаточно;
- security/secret scan: владелец не просил security-проверку, а этап не добавляет network, account, secret, external process, dependency или новый тип непроверенного входного файла;
- unrelated cleanup, массовый рефакторинг, обновление пакетов;
- push, PR, release, tag и публикация.

Найденную соседнюю проблему опиши в отчёте, но не исправляй.

## Invariants

- `IsometricSquares` — отдельная geometry. Запрещено реализовывать её как `Square` с `RotationDegrees = 45`.
- В локальном пространстве сетки изометрическая topology имеет собственный детерминированный basis. Offset применяется до перехода в локальное пространство, rotation — как общий внешний transform; render и snap используют один и тот же контракт.
- Snap выбирает действительный узел отображаемой lattice с детерминированным tie-break. Тесты сравнивают результат с небольшим brute-force oracle.
- `Subdivisions` одинаково и предсказуемо влияет на расстояние линий и snap для каждой geometry.
- Переключение geometry не должно молча сбрасывать остальные точные свойства.
- Доменная модель остаётся независимой от Avalonia и Skia.
- UI не является источником истины; renderer не изменяет документ.
- Каждое редактирование выполняется командой, пригодной для undo/redo и тестирования.
- Один slider gesture или один numeric commit создаёт не более одной записи истории; live preview не засоряет undo stack.
- No-op и отклонённое невалидное значение не создают запись истории.
- Locked Grid layer показывает фактические значения, но не разрешает изменение.
- `GridId`, `LayerId`, object ID, node ID и portal ID не меняются при редактировании или snap.
- Старый `.mapproj` открывается без перезаписи единственной копии.
- Новое значение geometry сохраняется явно и восстанавливается без fallback в `Square`.
- Неизвестное значение geometry не подменяется молча допустимым значением.
- `schemaVersion` не меняется без отдельного принятого решения. Если версия действительно должна измениться, остановись и выдай `BLOCKED` с технической причиной.
- Невалидные `NaN`, infinity, неположительный cell size/line width, недопустимые subdivisions/opacity и повреждённый цвет не должны доходить до бесконечного render-loop или аварийного native decode.
- При очень мелкой сетке и большой видимой области renderer применяет детерминированный bounded LOD, не создавая full-canvas bitmap и не выполняя неограниченный цикл.
- Editor, preview и export используют один scene evaluator; selection chrome и edit overlays в export не попадают.
- Golden expected никогда не перезаписывается обычным тестом или UI driver. Новый expected остаётся `PROPOSED` до отдельного просмотра.
- Приложение остаётся нативным, офлайн и без WebView.
- Новые пакеты не добавлять.

## Internal checkpoints

Это внутренние части одной задачи, а не отдельные prompts и не повод останавливаться после первого зелёного теста.

1. **Preflight и контракт:** отдельный worktree, точный BASE, один active prompt, inventory существующего Grid-кода и тестов.
2. **RED и geometry contract:** сначала добавить минимальное воспроизводимое failing-доказательство отсутствия `IsometricSquares`, затем зафиксировать собственный математический контракт basis/render/snap в тестах и коротком комментарии документации.
3. **Core и commands:** topology, validation, snap, стабильные ID, undo/redo и no-op/locked behavior.
4. **Persistence и Rendering:** roundtrip, старые fixtures, preview/export, bounded rendering и четыре proposed golden.
5. **Presentation и App:** полный property sheet, русские значения, keyboard/UIA/accessibility, одна команда на пользовательский commit.
6. **Native evidence:** новый Release-driver, четыре presets, точные свойства, undo/redo, save/reopen, snap и export.
7. **Финальный self-check:** все triggered gates один раз, отчёт, один stage-коммит, exact `BASE..HEAD` inventory и остановка.

Если один checkpoint обнаружит новый продуктовый вопрос, пометь только зависимую ветвь `BLOCKED` и продолжай независимую работу внутри scope.

## Acceptance criteria

1. В модели и UI доступны ровно четыре поддерживаемые geometry:
   - `Square` — «Квадратная»;
   - `IsometricSquares` — «Изометрическая»;
   - `HexFlat` — «Шестиугольная, плоский верх»;
   - `HexPointy` — «Шестиугольная, острый верх».

2. `IsometricSquares` визуально и математически отличается от `Square + 45°`: fixed-scene render, lattice points и snap-results не совпадают.

3. Render и snap используют один изометрический basis. Тесты покрывают:
   - известные lattice intersections;
   - точки между intersections;
   - детерминированный tie-break;
   - negative coordinates;
   - offset;
   - rotation;
   - subdivisions;
   - очень малый и очень большой zoom;
   - сравнение ближайшего узла с brute-force oracle.

4. Для всех четырёх geometry object, line/shape node и line portal snap:
   - попадают в допустимый узел сетки;
   - сохраняют исходные стабильные ID;
   - дают одинаковый world-result при разных zoom.

5. Property sheet показывает фактические значения всех параметров из `GridDefinition`. Пользователь может точно ввести число, выбрать geometry, цвет и toggle-параметры без циклического перебора скрытых вариантов.

6. Все изменённые standard controls имеют:
   - понятную русскую подпись;
   - единицу измерения там, где она нужна;
   - `AutomationId`;
   - accessible name и корректный role/control type;
   - enabled/disabled, selected/checked state;
   - видимый focus и рабочий Tab-порядок;
   - отсутствие обрезания длинных русских подписей в поддерживаемом минимальном окне.

7. Каждое изменение одного свойства является одной undoable-командой. Undo возвращает полное предыдущее значение, redo — точное новое. No-op, невалидный ввод и попытка изменения locked layer историю не меняют.

8. Все свойства, включая `IsometricSquares`, переживают save/reopen с семантическим равенством. Старые проекты с `Square`, `HexFlat` и `HexPointy` открываются без изменения исходного файла и с прежними значениями/defaults.

9. Невалидные file/UI значения отклоняются предсказуемо:
   - приложение не падает;
   - документ остаётся неизменным;
   - история не пополняется;
   - повреждённый файл не выдаётся за успешно открытый;
   - UI показывает понятное состояние или сообщение.

10. Viewport, preview и export рисуют одну geometry при одинаковом document state. `IncludeInExport = false` исключает сетку из export, а `true` включает её. Visibility редактора не подменяет export policy.

11. Grid layer сохраняет существующие mask, layer ordering, visibility/lock и topmost-visible snap semantics без регрессии.

12. Rendering не выполняет неограниченное количество линий при extreme coordinates, огромном viewport, малом cell size или больших subdivisions. Это доказывается адресным тестом bounded work/output, без объявления нового performance-бюджета Stage 11.

13. Есть четыре маленьких детерминированных rendering golden fixtures. Обычный тест только читает expected, создаёт actual/diff отдельно и никогда не обновляет expected автоматически.

14. Новый project-owned native driver запускает точный Release EXE, проверяет property sheet, четыре geometry, один numeric edit, один toggle, undo/redo, snap, save/reopen и export on/off, сохраняет machine-readable report и завершает все процессы.

15. Каждый нативный PNG открыт в original resolution и письменно проверен на hierarchy, clipping, overlap, alignment, focus, читаемость значений и фактическую геометрию canvas.

16. Full stage gates проходят без необъяснённых файлов, новых build warnings и новых format diagnostics. Любой недоказанный пункт остаётся `PARTIAL` или `BLOCKED`.

## Failure and adversarial scenarios

Обязательно покрыть:

- `NaN`, positive/negative infinity и неположительный cell size;
- subdivisions `0`, отрицательное и чрезмерное значение;
- line width `0`, отрицательное, бесконечное или чрезмерное;
- opacity вне `0..1`;
- повреждённую/пустую строку цвета;
- большие положительные и отрицательные offsets;
- отрицательный и очень большой rotation с детерминированной нормализацией результата;
- extreme world coordinates и zoom;
- точку на равном расстоянии от нескольких изометрических узлов;
- locked grid layer;
- hidden grid и несколько grid layers с сохранением существующего выбора active snap grid;
- переключение geometry → undo → redo → save → reopen;
- старый `.mapproj` без `IsometricSquares`;
- неизвестную geometry в `.mapproj`;
- export с включённой и выключенной сеткой;
- отсутствие автоматической перезаписи golden;
- отсутствие изменений поведения Land/Brush GridBlock и соседних Stage 10 функций.

## Risk and tool matrix

| Risk / claim | Tool | Required evidence | Required? | Valid skip condition |
|---|---|---|---:|---|
| compile and regression | `dotnet restore/build/test/format` | exit codes, test counts, Release 0 warnings/errors; format-result отделён от старого debt | yes | none for this production stage |
| repository boundary | Git inventory + установленный `pre-commit` | точный BASE, worktree, A/M/D/R, полный список файлов, hook PASS, clean committed snapshot | yes | none before the authorized commit |
| dependency graph | NuGet vulnerable/deprecated audit + package diff | отсутствие новых packages/advisories; прежний deprecated debt назван отдельно | yes | не пропускать в финальном full-stage gate |
| critical branch assertions | один targeted Stryker run по изменённой Core geometry/snap-логике | mutated file/span, killed/survived/no-coverage и разбор каждого значимого survivor | yes | только tool failure, тогда `BLOCKED`; coverage отдельно не запускать без нового distinct claim |
| file/import/archive boundary | targeted `.mapproj` fixtures и semantic roundtrip | old/new/unknown geometry, malformed numeric values, исходный старый файл не изменён | yes | archive/security suites вне изменённого Grid DTO path не запускаются |
| performance budget | bounded geometry/render tests | доказательство конечного числа примитивов и завершения extreme-case | no full benchmark | нет нового speed/memory claim; Stage 11 benchmark/trace/counters пропустить |
| native behavior | точный Release EXE + новый project-owned UIA driver | process identity, actions/checks, `.mapproj`, export, evidence JSON, clean shutdown | yes | none: UI и canvas меняются |
| visual fidelity | четыре frozen golden + screenshots + vision + ImageMagick actual/diff | dimensions, renderer/state, RMSE/diff, ручной разбор каждого изображения | yes | новый expected остаётся `PROPOSED` до fresh review, но сравнение и review автора всё равно обязательны |
| accessibility | UIA + один bounded проход Accessibility Insights | names, roles, focus, Tab, checked/selected, clipping/contrast | yes | если инструмент объективно недоступен, один раз зафиксировать `PARTIAL`, не искать обходной бесконечный цикл |
| security/supply chain | Codex Security | не запускать; зафиксировать отсутствие нового trust boundary/dependency/tool | no | owner не просил security scan, а changed-risk trigger отсутствует |
| browser/public research | browser | не требуется | no | accepted clean-room sources уже достаточны; не открывать аккаунт конкурента |
| external desktop control | сторонние MCP/plugins | не использовать | no | project-owned UIA и встроенная работа Codex с локальным окном достаточны |

Не удаляй строки матрицы. В итоговом отчёте для каждого инструмента укажи `used` или `skipped` и конкретную причину.

## Verification budget and stop rules

- На одном неизменившемся snapshot и в роли автора каждый успешно пройденный command/gate запускается не более одного раза.
- До production-реализации получить один минимальный targeted RED, который действительно доказывает отсутствие отдельной `IsometricSquares`, а не просто ошибку тестовой инфраструктуры.
- После относящегося изменения выполнить один targeted GREEN.
- После стабилизации выполнить один раз Stage10E-фильтры Core, Rendering, Persistence и Presentation, затем один раз полный solution suite, потому что меняется production-код нескольких проектов.
- Выполнить один bounded Stryker run только по реально изменённой чистой Core geometry/snap-логике. Coverage дополнительно не запускать: отдельного claim для него нет.
- UIA/native driver выполнить один раз после стабильной Release-сборки. Повтор допустим только после конкретного изменения, которое делает прошлый результат устаревшим.
- Golden comparison выполняется один раз на финальном fixed state. Expected не регенерировать при падении сравнения.
- Flake-серию заранее не объявлять: геометрические и golden-тесты должны быть детерминированными и не повторяются серией.
- Если одна проверка падает, исправь относящуюся причину и повтори только её. Если два последовательных запуска показывают ту же причину, не делай слепой третий запуск: смени диагностический метод или зафиксируй `PARTIAL/BLOCKED`.
- Настоящий OS DPI `100/150/200%` проверяется максимум по одному разу на scale, только если среда действительно меняет Windows/Avalonia scale. Если среда не применяет scale, зафиксируй `PARTIAL`; не имитируй успех одинаковыми screenshots.
- Не повторяй уже принятые Stage 10D security/native/full-suite gates без changed-risk trigger.
- Stop condition: acceptance-specific evidence, один regression boundary, все triggered gates, Git/scope inventory и один commit готовы. После этого прекрати проверки, сообщи `BASE/HEAD` и остановись для отдельной fresh review-задачи.

## Required automatic verification

Создай отдельные тестовые классы с устойчивыми фильтрами:

```text
Stage10EGridGeometryTests
Stage10EGridRenderingTests
Stage10EGridRoundTripTests
Stage10EGridViewModelTests
```

Сначала зафиксируй один meaningful RED. После реализации выполни один раз:

```powershell
dotnet restore .\Map.slnx --force-evaluate
dotnet build .\Map.slnx -c Release --no-restore

dotnet test .\tests\Map.Core.Tests\Map.Core.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Stage10EGridGeometryTests"

dotnet test .\tests\Map.Rendering.Tests\Map.Rendering.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Stage10EGridRenderingTests"

dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Stage10EGridRoundTripTests"

dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Stage10EGridViewModelTests"

dotnet test .\Map.slnx -c Release --no-build `
  --blame-hang --blame-hang-timeout 2m

dotnet format .\Map.slnx `
  --verify-no-changes --severity info --no-restore

dotnet package list --project .\Map.slnx `
  --vulnerable --include-transitive --no-restore

dotnet package list --project .\Map.slnx `
  --deprecated --include-transitive --no-restore

powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\doctor.ps1

powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\check-stage-prompts.ps1 -RequireActive

python -m pre_commit install
python -m pre_commit run --all-files

git diff --check
```

Если `dotnet format` сохраняет уже известный baseline exit `2`, не перезапускай его. Сравни diagnostics с точным BASE, докажи отсутствие новых diagnostics в изменённых C#-строках и отрази gate как `PARTIAL`, а не как PASS.

После того как Core geometry стабилизирована, выполни один targeted Stryker run. До запуска перечисли точные изменённые geometry/snap-файлы и ограничь `--mutate` только ими. Базовая команда:

```powershell
dotnet stryker `
  --project .\src\Map.Core\Map.Core.csproj `
  --test-project .\tests\Map.Core.Tests\Map.Core.Tests.csproj `
  --configuration Release `
  --mutate "**/GridSnap.cs" `
  --reporter Progress `
  --reporter Json `
  --output .\artifacts\stage10e-grid-geometry\stryker
```

Если чистая topology вынесена в новый Core-файл, добавь его точным вторым `--mutate`. Не расширяй mutation на весь `Map.Core`. Один раз разбери все survivors изменённой логики; значимый survivor исправь тестом или честно оставь `PARTIAL`.

Перед коммитом и после него собери Git-evidence:

```powershell
git status --porcelain=v2 --branch
git diff --name-status
git diff --stat
git diff --check
git ls-files
dotnet sln .\Map.slnx list

Get-ChildItem .\src,.\tests,.\benchmarks,.\tools -Recurse -Filter *.csproj |
  ForEach-Object { dotnet list $_.FullName reference }
```

После коммита замени working-tree diff на точный `BASE..HEAD`, пересчитай `A/M/D/R`, проверь полный список файлов и случайные `bin`, `obj`, temporary или runtime artifacts. После коммита build/tests повторно не запускай: commit не изменяет уже проверенные байты.

## Required native UI verification

Создай:

```text
scripts/drive-stage10e-grid-geometry.ps1
```

Driver должен запускать точный:

```text
src\Map.App\bin\Release\net10.0\Map.App.exe
```

и сохранять новый timestamped набор в:

```text
artifacts\stage10e-grid-geometry\
```

Обязательный нативный сценарий:

1. проверить точную identity Release-процесса;
2. открыть детерминированную тестовую карту или создать её через поддерживаемый тестовый режим;
3. выбрать Grid layer и проверить наличие полного property sheet;
4. по очереди выбрать Square, IsometricSquares, HexFlat и HexPointy;
5. для каждого preset проверить выбранное значение через UIA и видимый canvas-result;
6. задать нестандартные cell size, subdivisions, line width/color/opacity, offsets и rotation;
7. переключить snap и include-in-export;
8. выполнить один undo и redo изменения свойства;
9. привязать object, vector node и portal и проверить фактические координаты/ID;
10. сохранить `.mapproj`, закрыть и открыть снова;
11. проверить значения напрямую в сохранённом `scene.json`, а не только по тексту UI;
12. создать export с сеткой и без сетки и проверить реальные PNG dimensions/pixels;
13. завершить окно и доказать отсутствие оставшихся процессов `Map.App`.

Минимальный набор numbered PNG:

```text
01-square.png
02-isometric-squares.png
03-hex-flat.png
04-hex-pointy.png
05-custom-properties.png
06-undo-redo-reopened.png
07-export-with-grid.png
08-export-without-grid.png
```

Driver должен возвращать non-zero при failed action/check и сохранить `report.json` с:

- exact EXE path/hash или другим устойчивым identity;
- branch/HEAD;
- размером окна, DPI/scale и renderer;
- последовательностью действий;
- всеми проверками и фактическими значениями;
- путями `.mapproj`, exports, screenshots, actual/diff;
- результатом save/reopen;
- process cleanup;
- массивами `partial` и `blocked`.

Открой каждый PNG в original resolution и отдельно опиши:

- видны ли все controls и фактические значения;
- нет ли clipping, overlap и неясной hierarchy;
- виден ли focus;
- различаются ли четыре geometry;
- является ли `IsometricSquares` косоугольной сеткой примерно `±30°`, а не квадратной сеткой под `45°`;
- совпадает ли reopened state;
- присутствует/отсутствует ли сетка в соответствующем export.

Golden/reference и actual должны иметь одинаковые dimensions, renderer, seed, zoom, viewport, offset, rotation, subdivisions и document state. Сохраняй actual/diff отдельно. Нормальный тест и driver не имеют права обновлять expected.

Для standard controls выполни один bounded UIA/Accessibility Insights проход. Если настоящий keyboard или Windows scaling нельзя безопасно проверить в текущей интерактивной среде, пометь конкретный пункт `PARTIAL`; не подменяй его screenshot existence или headless-тестом.

## Documentation and evidence

Обнови только фактические stage-документы:

- `docs/prompts/STAGE_10E_GRID_GEOMETRY.md` — этот active contract;
- `docs/prompts/STAGE_10D_OBJECT_INSPECTORS.md` — historical status;
- `docs/reviews/STAGE_10E_GRID_GEOMETRY_VERIFICATION.md` — author self-check, не независимая приёмка;
- `docs/STATE_HANDOFF.md` — точный checkpoint и следующий fresh review;
- при необходимости `docs/00_START_HERE.md` и запись 10E в roadmap — только с честным статусом.

Не объявляй Stage 10E окончательным `FACT` в авторской задаче. Даже при полностью зелёном self-check итог этапа до fresh review:

```text
PARTIAL — implementation checkpoint создан и готов к независимой проверке точного BASE..HEAD.
```

Отдельные непосредственно доказанные свойства можно помечать `FACT`, но author self-check не называется независимым.

## Git and stop condition

- Работай только в отдельном `stage/10e-grid-geometry` worktree.
- Не изменяй dirty checkout `C:\Projects\Map`.
- Не используй `reset`, `restore`, `checkout --`, `clean`, stash, `--amend`, rebase или history rewrite для сокрытия изменений.
- Индексируй только проверенный Stage 10E inventory, не используй бездумно `git add -A`.
- После всех обязательных gates разрешён ровно один коммит:

```text
feat(stage-10e): implement grid geometry and property sheet
```

- После коммита:
  - сообщи полный `BASE` и `HEAD`;
  - покажи точный `BASE..HEAD` file inventory и A/M/D/R;
  - покажи status worktree;
  - не исправляй новые findings в этом же коммите;
  - не запускай независимую проверку в этом же контексте;
  - не начинай Stage 10F;
  - остановись.

Следующая задача после остановки — свежий Codex `gpt-5.6-sol`, reasoning `xhigh`, который независимо проверит точный `BASE..HEAD`. Не создавай и не выполняй её самовольно.

## Required report

Используй точную структуру `docs/07_CODEX_RUNBOOK.md`:

```markdown
## RESULT
FACT / PARTIAL / PROPOSED / BLOCKED — наблюдаемый итог и граница author self-check.

## CHANGED
- путь — назначение и связь с acceptance.

## VERIFIED AUTOMATICALLY
- команда — exit code, test count и доказанный критерий.

## VERIFIED IN NATIVE UI
- Release EXE, действия, checks, screenshots, save/reopen/export и process cleanup.

## TOOL MATRIX RESULT
- каждый инструмент — used/skipped, evidence или точная причина.

## NOT VERIFIED
- что не доказано и почему.

## RISKS
- конкретный остаточный риск и влияние.

## GIT / SCOPE
- branch, BASE, HEAD, worktree, A/M/D/R, полный file inventory и отсутствие out-of-scope изменений.

## NEXT
- только fresh independent review точного Stage 10E BASE..HEAD; Stage 10F не выполнять.
```

Нельзя писать «готово», если хотя бы один обязательный acceptance-критерий не доказан. Не запускай дополнительные проверки после достижения stop condition и не превращай задачу в цикл повторения уже зелёных gates.
