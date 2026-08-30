# STATE HANDOFF

## Последнее обновление

2026-08-30. `Stage 10F: FACT / OWNER ACCEPTED FOR TRANSITION`.

Точный immutable review range:
`REVIEW_BASE=766b81617874b4b8dbed2f4e781c94350305796a`,
`FEATURE_HEAD=77dd7afa3881542894f5d959f6a52fbb09e2491f`. Fresh review проверил все `22` feature files и
нашёл четыре in-scope finding: test mode мог читать production shortcut profile, font catalog не
имел family-count cap, gesture capture создавал bare-Tab focus trap, а author driver не доказывал
physical Tab/keyboard-only actions/DPI. Все четыре исправлены отдельным commit
`ACCEPTED_CODE_HEAD=2e30e78b0b86a4ab070158b7c773200611c62d58`.

`FACT` automatic: restore PASS; Release build `0` warnings/errors; Stage10F filters Presentation
`15/15`, Rendering `2/2`, Persistence `1/1`; полный suite `738/738` (Core `215`,
Persistence `129`, Rendering `32`, Presentation `229`, Assets `133`). Vulnerable packages не
найдены; deprecated — только прежний xUnit 2.9.3 Legacy. Adversarial settings/path/write tests
PASS. Scoped Codex Security scan проверил `12` production source items, reportable findings `0`.

`FACT` native: exact Release EXE SHA-256
`05993fe7fc6259533fa7ca14eb25b9f53570a17ad9bbbed35a209803583b65db`, три exact-path PID,
`96 DPI / 100%`, bounds `1416×939`, running count после сценария `0`. Driver прошёл `40/40`:
font select/cycle/undo/redo, requested/fallback/save/reopen/export, conflict/rebind/old-new route,
text suppression/help/restart/reset, physical Tab через все изменённые standard controls,
keyboard-only Save/Cancel/Reset и focus return. Evidence:
`artifacts/stage10f-fonts-bindings/ui-smoke-20260830-190008/report.json`.

`PARTIAL`: global formatter сохраняет известный unrelated legacy baseline; настоящий Windows
scale `150%/200%` не менялся; локальный font list зависит от ПК. Accessibility Insights FastPass
на стартовом окне нашёл четыре существующие baseline-проблемы вне Stage 10F. Эти ограничения не
переименованы в `FACT` и по прямому решению владельца не блокируют переход. Полный отчёт:
`docs/reviews/STAGE_10F_FONTS_BINDINGS_VERIFICATION.md`.

Следующее и единственное действие: выполнить активный
`docs/prompts/STAGE_11_PERFORMANCE_PACKAGING_RC.md`. Дополнительная Stage 10F review/test/fix
задача не нужна. Push, PR, release, tag и merge не выполнялись.

## FACT — среда

- Рабочая область: `C:\Projects\Map`.
- .NET SDK `10.0.400`, runtime `.NET 10.0.10`, Avalonia Templates `12.1.1`.
- ПК измерений: Windows `10.0.26200` x64, 16 логических ядер, machine `DM`, Server GC выключен.
- ripgrep 15.2.0 отсутствовал в `PATH` и был переустановлен из официального winget-пакета
  `BurntSushi.ripgrep.MSVC` (MIT/Unlicense, dev-only). До этого `scripts/doctor.ps1` падал.

## FACT — Stage 1

- Решение `spike/Map.Spike.slnx`: 7 проектов, чистая сборка Release без ошибок и предупреждений.
- Тесты: 55/55 проходят.
- Пакеты: добавлен только `SkiaSharp 3.119.4` (MIT) в `Map.Spike.Rendering`; остальное — шаблон
  Avalonia 12.1.1 (MIT). `AvaloniaUI.DiagnosticsSupport` из шаблона удалён как ненужный этапу.
- `Map.Spike.Core` не ссылается на Avalonia/Skia.
- Реальный renderer в окне: **GPU, GRBackend.OpenGL через ANGLE (EGL/GLES over Direct3D)**,
  загружены `av_libGLESv2.dll`, `d3d11.dll`, `dcomp.dll`. CPU-fallback не наблюдался.
- Процесс окна не слушает ни одного TCP-порта; модулей WebView/CEF/Chromium не загружено.

### Измерения в настоящем окне (2560×1183 client, GPU)

- Кадр, 300 измеряемых кадров после 60 прогревочных, сцена 10 000 объектов:
  интервал median `13,47 мс` (74,2 fps), P95 `21,22 мс` (47,1 fps), max `28,56 мс`.
- Время внутри Skia (наш `SceneRenderer`): median `1,59 мс`, P95 `2,84 мс`.
- До оптимизации (слитые `SKPath`): Skia median `17,02 мс`, интервал median `32,53 мс` (30,7 fps).
- Память окна после сессии: document tiles `4,75 MiB`, render tile cache `4,75–5,25 MiB`,
  geometry cache `~2,19 MiB` (64 чанка, 63 772 треугольника), undo `0–3,25 MiB`,
  managed heap `20–71 MiB`, working set `181–219 MiB`.

### Измерения в консольном benchmark (программный raster, не GPU)

- Hit-test по точке в сцене 10 000: median `0,006 мс`, P95 `0,012 мс`,
  в среднем `22,6` кандидата (`0,226 %` сцены).
- Atomic save: median `188,9 мс`, файл `0,71 MiB`. Reopen: median `72,3 мс`.
- Offscreen export 4096×4096 PNG: median `1155,8 мс`, файл `1,29 MiB`.
- Кадры в программном raster (справочно, не GPU): median `43,6 мс` при полном холсте.
- Отчёт: `artifacts/benchmark/benchmark-report.md`.

### Проверенные артефакты на диске

- `%LOCALAPPDATA%\Map\Spike\export-4096.png`: `1 420 315` байт, сигнатура PNG,
  IHDR `4096×4096`, bit depth 8, color type 6 (truecolour + alpha).
- `%LOCALAPPDATA%\Map\Spike\spike-session.mapspike`: `844 885` байт, 28 записей ZIP,
  `manifest.json` со `schemaVersion 1`, `sceneSha256`, `tileManifestSha256`, revision 4.
- Снимки настоящего окна: `artifacts/spike-smoke/01..13-*.png`.

## FACT — дефекты, найденные и исправленные внутри среза

1. `AtomicDocumentStore.Save` считал SHA-256 существующего файла до `try`, поэтому `IOException`
   на заблокированном файле выходил наружу необёрнутым и терял контракт `SaveFailedException`.
   Найден тестом `A_locked_target_fails_the_save_without_losing_the_previous_file`.
2. Тот же расчёт хеша обрывал сохранение до записи temp. Теперь fingerprint — best effort,
   работа пользователя доходит до durable temp, а отказ проявляется на replace.
3. `MapCanvasControl.FinishBenchmark` менял `TextBlock.Text` внутри `Render()`, что валило процесс
   с `InvalidOperationException: Visual was invalidated during the render pass`. Завершение
   вынесено в `Dispatcher.UIThread.Post`. Добавлен `Diagnostics/CrashLog`.

## FACT — независимая проверка Codex

- Проверен точный implementation diff `7f03200386f77d8e98dfbdb2074f85315cbb0587..`
  `f80c8e995b05c0c86619e7319e953408801a9f88`: `A=66`, `M=1`, `D=0`, `R=0`, всего 67 файлов.
- Свежие clean/restore/build прошли; build завершился с 0 ошибок и 0 предупреждений.
- Полный набор тестов: 55/55. Targeted suites: commands 10/10, spatial/viewport 12/12,
  save/fault 21/21, process-kill 2/2, render/export 10/10.
- `dotnet format --verify-no-changes`, vulnerable/deprecated package audits, `pre-commit` и
  `scripts/doctor.ps1` прошли. Уязвимых пакетов не найдено.
- `dotnet tool restore` из корня работает и восстанавливает 5 local tools; прежняя запись об
  отсутствии manifest была ошибочной.
- Свежий benchmark: hit-test median `0,003 мс`, P95 `0,007 мс`; save median `306,752 мс`;
  reopen `113,364 мс`; 4096x4096 export median `1571,188 мс`.
- В настоящем окне подтверждены GPU/OpenGL через ANGLE поверх Direct3D и отсутствие WebView.
  Свежий ограниченный прогон показал frame interval median `13,73 мс`, P95 `15,43 мс`,
  Skia draw median `0,11 мс`, P95 `0,27 мс` на поверхности `1400x680`.
- DEC-002 подтверждён: Qt spike не нужен. Найденный blocker алгоритмический и не связан со
  стеком Avalonia/Skia.
- Полный отчёт: `docs/reviews/STAGE_01_VERIFICATION.md`.

## FACT — fix-срез Stage 1 Fix 01

Base `fcc2d7f`. Изменены только разрешённые заданием пути.

- **Блокирующий дефект culling закрыт.** Объекты по-прежнему хранятся по чанку своего центра, а
  `VisibleChunks` теперь расширяет диапазон сканируемых ячеек на `MaxOverhang` — измеренный при
  сборке максимум, на который bounds любого чанка выходят за собственную ячейку. Точная проверка
  `WorldBounds.Intersects` осталась, поэтому набор отрисованного не расширился и дублирования нет.
- Контрпример из ревью воспроизведён тестом до исправления: object bounds
  `X=486,0588745…, Right=553,9411254…`, viewport `[480,511)`, `visible chunks 0`. После
  исправления тест проходит. Полный suite: **67/67** (было 55).
- Узкий Stryker только по `AtomicDocumentStore.cs`: 47 протестированных мутантов, 28 killed,
  17 survived, 2 timeout, 9 NoCoverage, score `55,36 %`. Мутант удаления вызова
  `ValidateReadBack` (L101, statement → `;`) теперь **Killed**; отдельно подтверждено ручной
  мутацией — тест падает. Отчёт: `artifacts/stage01-fix01/stryker/`.
- `drive-window.ps1` стал самопроверяющимся: каждое состояние читается через UI Automation
  (Avalonia публикует `x:Name` как `AutomationId`), ожидания ограничены по времени, каждый PNG
  декодируется и сверяется с размером окна.
- Свежий UI smoke прошёл целиком: **13/13 PNG по `2576x1408`**, selection доказана после 3 из 40
  ограниченных probe-кликов — `Selected Square bc832c58-5ecc-4536-b8ce-1719b1ea5b83`,
  hit-test `61,5 us, cells=1 candidates=24 precise=1`. Артефакты:
  `artifacts/stage01-fix01/ui-smoke/` и `ui-evidence.txt`.

### Дополнительное измерение интервала кадра

Дисплей **144 Гц**, период обновления `6,94 мс`. Шесть прогонов, полный разбор:
`artifacts/stage01-fix01/frame-trace/ANALYSIS.md`.

- На чистом документе interval median `12,37–13,11 мс`, P95 `14,94–15,27 мс` — **ниже** 16,67 мс.
- P95 `19,82 мс` получен один раз, на документе после brush+mask+save+export+reopen.
- Skia draw median `0,14–1,40 мс`, P95 `0,93–2,35 мс`.
- `dotnet-counters`: **ноль сборок GC** всех поколений и **нулевая пауза GC** за всё окно;
  аллокации пик `1,62 МБ/с`; CPU около 25 % одного ядра из 16.
- `dotnet-trace` (`dotnet-sampled-thread-time`): `Win32DispatcherImpl.RunLoop` 46,82 %,
  `WaitHandle.WaitOneNoCheck` 31,47 %, `WinUiCompositorConnection.RunLoop` 15,22 %;
  на всю нашу отрисовку Skia приходится менее 1 %.
- Вывод: интервал квантуется частотой обновления и равен двум периодам по `6,94 мс`; время уходит
  в ожидание цепочки dispatcher → композитор → present, а не в GC и не в наш рендер.

## FACT — независимый Gate D Fix 01

- Точный range `fcc2d7f978554f7b96578c94d9635a748a58b0d4..`
  `9e07874ec16281915e61d582d5cf35d2146427a0`: `A=0`, `M=6`, `D=0`, `R=0`, `+706/-42`.
- Red/green подтверждён изолированно: без fix оба новых culling-теста падают, с fix — 4/4.
- Fresh clean/restore/build — 0 warnings/errors; suite 67/67; targeted 45/45; format, package
  checks, pre-commit и doctor проходят.
- Coverage: 86,71% lines / 77,51% branches overall; основной `ObjectGeometryCache` —
  96,20% lines / 90% branches.
- Узкий Stryker culling: критический mutant отключения inflate killed; оставшиеся survivors
  разобраны в `docs/reviews/STAGE_01_VERIFICATION.md` и не воспроизводят исходный пропуск.
- Свежий независимый GPU-window: interval median `13,58 мс`, P95 `14,84 мс`; Skia draw median
  `0,11 мс`, P95 `0,25 мс`; listeners 0; WebView/Chromium 0.
- Все 13 PNG Opus независимо просмотрены; selection, brush/undo/redo, mask, save/export/reopen и
  benchmark соответствуют UIA evidence. Файлы имеют одинаковый размер `2576x1408`.
- PNG 4096x4096 декодируется как 8-bit TrueColorAlpha; mapspike содержит 28 ZIP entries, оба
  manifest checksum совпадают с реальными entries.

## FACT — fix-срез Stage 1 Fix 02

Base `be323ed`. Изменены только три разрешённых заданием пути.

- **Последний блокер закрыт.** `RecoveryRecord.StateValue` стал `RecoveryState?`:
  `Enum.TryParse` (без ignoreCase) плюс `Enum.IsDefined`, иначе `null`. `TryParse` одного
  недостаточно — строка `"999"` разбирается в значение, которого нет среди членов enum;
  `IsDefined` отсекает такие случаи. Неизвестное состояние никогда не бросает исключение и
  никогда не предлагается как восстанавливаемое.
- Red/green доказан на base: контрпример `FutureVersionDurable` + существующий temp падал с
  `ArgumentException: Requested value 'FutureVersionDurable' was not found`; wrong-case и
  empty/whitespace — с `ArgumentException`, JSON `null` — с `ArgumentNullException`; `"999"` и
  `"-1"` не бросали, но выдавали неопределённое число за состояние (красный через
  `Assert.Null(StateValue)`). После fix все 13 новых кейсов зелёные.
- Сырой неизвестный `State` остаётся в record; journal и temp-файл не удаляются — проверено
  assert-ами на существование обоих файлов после чтения.
- Известные состояния сохранили поведение: `TempDurable`+temp → recoverable; `TempDurable` без
  temp, `Started`, `Replaced`, `Completed` → нет. Позитивные контроли проходили и на base.
- Полный suite **80/80** (было 67, +13); targeted Fault/ProcessKill/SaveReopen 44/44; build
  0 warnings/0 errors; format, vulnerable/deprecated audits, pre-commit, doctor — exit 0.
- Узкий Stryker по `RecoveryJournal.cs`: 34 протестированных, 19 killed, 14 survived,
  1 timeout, 1 NoCoverage, 2 CompileError, score 57,14 %. Все мутанты изменённых ветвей убиты:
  в safe parse `&&`→`||` и условие→`true` — Killed; в `HasRecoverableWork` `==`→`!=` и
  `&&`→`||` — Killed; тернарные Conditional-мутации — CompileError (нетестируемы). Survivors —
  дефолты JSON-имён полей, `Flush` в `Write` и блок `Clear`, к изменённым ветвям не относятся.
  Отчёт: `artifacts/stage01-fix02/stryker-recovery/`.
- UI smoke не перезапускался: срез не касается UI/rendering (по заданию).

## FACT — сокращённый независимый Gate D Fix 02

По прямому решению владельца финальная проверка ограничена критическими точками, без повторения
уже выполненных тяжёлых прогонов.

- Точный range `be323ed20fe701025fab7c467a00bc58b285b6c2..`
  `b543896ce60fc5164a1949f8007404b474d03f4a`: изменены ровно три ожидаемых файла,
  `A=0`, `M=3`, `D=0`, `R=0`, `+171/-20`; кодовый worktree перед проверкой чистый.
- Codex прочитал production diff и новые regression tests. Ветка использует nullable safe parse
  через `TryParse` + `IsDefined`; сырой `State` сохраняется, неизвестные/числовые/wrong-case/null
  значения не считаются recoverable, известный `TempDurable` сохранил прежний контракт.
- Red исходного дефекта ранее воспроизведён Codex на base отдельным probe:
  `FutureVersionDurable` приводил к `ArgumentException` при чтении `HasRecoverableWork`.
- На новом head свежая `Release --no-restore` сборка прошла с 0 warnings/errors; целевой набор
  `FaultInjectionTests|ProcessKillRecoveryTests|SaveReopenTests` прошёл **44/44**.
- По решению владельца Codex не повторял full 80/80, clean/restore, UI smoke, coverage,
  benchmark, Stryker и package audits. Эти результаты остаются evidence self-check Opus, а не
  повторной независимой проверкой.
- Итог: исправление и Stage 1 приняты в согласованном сокращённом режиме; существующие `PARTIAL`
  ниже не превращаются в доказанные `FACT` и переносятся дальше как известные ограничения.

## PARTIAL

- Сколько именно из двух периодов обновления можно вернуть — не доказано. Нужен эксперимент со
  способом запроса кадра и `Win32CompositionMode`, это изменение конфигурации приложения и в
  fix-срез не входило. Числа на 60-герцовом экране не измерялись.
- Сглаживание объектов отключено (`DrawVertices` без MSAA) — визуальный размен ради бюджета.
- Fault injection для «во время `File.Replace`» проверяет наблюдаемый результат, а не разрыв
  внутри самой операции: `ReplaceFile` атомарна на уровне NTFS и не делится из managed-кода.
- В узком Stryker остаются 17 survived и 9 NoCoverage мутантов. Значимые: удаление
  `SpikeDocumentSerializer.Read` внутри `IsReadable` и замена его `return false` на `true`
  (ослабляют признак `TargetStillIntact`), удаление `stream.Flush(flushToDisk: true)`, а также
  три непокрытых сообщения несоответствия внутри `ValidateReadBack`.
- xUnit `2.9.3` и его transitives помечены `Legacy` в package audit.
- Self-contained `win-x64` publish и запуск на чистой VM не выполнялись (вне scope Stage 1).

## BLOCKED

- Ручные аккаунтные сценарии Inkarnate требуют входа владельца.
- Автоматическое исследование закрытого UI не выполняется из-за условий использования.
- Точные визуальные токены и производственные ассеты отложены до материалов владельца.

## FACT — срез Stage 2A: production foundation и минимальный `.mapproj` v1

Ветка `stage/02-foundation-persistence` от base `a1f8796`. Изменения только в allowlist.

- Создано production-решение `Map.slnx` ровно с четырьмя проектами: `src/Map.Core`,
  `src/Map.Persistence`, `tests/Map.Core.Tests`, `tests/Map.Persistence.Tests`. На `spike/**`
  ссылок нет; runtime NuGet-пакетов нет (тестовые проекты — стандартный шаблон xUnit).
- `Map.Core`: immutable-модель — `MapDocument` (id, title, createdUtc, canvas, слои),
  `CanvasDefinition` (width/height/unit/background), `Layer` (id, name, visible, locked, opacity,
  упорядоченные objects), `SceneObject` (id, kind Rectangle/Ellipse, name, x/y/width/height/
  rotationDegrees, fill/stroke/strokeWidth, visible, locked); типизированные `DocumentId`/
  `LayerId`/`ObjectId` с канонической строкой «D»; дубли ID отвергаются конструктором;
  сохранённые коллекции не алиасят списки вызывающего. Ссылок и пакетов у Core — ноль.
- `Map.Persistence` (зависит только от Core): `.mapproj` v1 — ZIP ровно с четырьмя entries:
  `manifest.json` (format/schemaVersion=1/appVersion/documentId/title/createdUtc/modifiedUtc/
  canvas/preset/sceneSha256/assetManifestSha256), `scene.json`, `asset-manifest.json` (пустой
  каталог), `preview.png` (bytes от вызывающего, persistence не рисует). `SaveMetadata`
  (appVersion, modifiedUtc) передаётся явно — clock-DI нет, сериализация детерминирована
  (фиксированный порядок полей, фиксированные ZIP-timestamps). Save: sibling temp → flush
  (flushToDisk) → полный read-back с проверкой entries/format/schema/checksums/cross-check id →
  единственная fault-граница `AfterDurableTempBeforeReplace` → `File.Replace`/`Move`. При отказе
  temp не удаляется, target не тронут, бросается `MapProjectSaveException` с путями. Open
  оборачивает сырые ZIP/JSON-ошибки в `MapProjectCorruptedException` с именем entry;
  `schemaVersion=2` → `UnsupportedSchemaVersionException`, файл не изменяется.
- **Red → green доказан**: тесты написаны до реализации; на стабах все 12 persistence-тестов
  красные (`NotImplementedException` в round-trip и в atomic failure path), после реализации —
  зелёные. Итог **23/23** (10 Core + 13 Persistence).
- Проверки: restore/build Release **0 warnings/0 errors**; `dotnet format --verify-no-changes`
  exit 0; `.deps.json` обоих production-проектов не содержат Avalonia/Skia/spike; pre-commit и
  doctor — exit 0.
- Не входило и не делалось: UI/dashboard/wizard, viewport/rendering, command stack/undo,
  thumbnail-генерация, recovery journal, process-kill harness, migrations, export.

## FACT — сокращённая независимая проверка Stage 2A

По решению владельца Codex проверил только критические точки, без coverage, mutation, benchmark,
package audits и повторного pre-commit/doctor.

- Точный range `a1f879651eeefb5c2ee73d4edcbf0d95a813542a..`
  `6faa5c9bfcc6c686f0bf36aecf3de1cc09029bc6`: `A=21`, `M=1`, `D=0`, `R=0`, 22 файла,
  `+1422/-5`; список совпадает с allowlist, `git diff --check` чистый, worktree чистый.
- Codex прочитал production solution, доменную модель, `MapProjectStore`, DTO и набор тестов.
  `Map.Core` не имеет project/package references; `Map.Persistence` ссылается только на Core;
  зависимостей от Avalonia/Skia/spike нет.
- Свежая Release-сборка прошла с 0 warnings/errors; все тесты нового solution прошли **23/23**
  (10 Core + 13 Persistence).
- Основные риски не скрыты: настоящий process-kill, recovery journal, bounded backup, UI и
  валидация preview ещё отсутствуют. Более широкое ужесточение чтения повреждённого, но формально
  валидного JSON также переносится в следующий крупный блок.
- Итог: Stage 2A принят как foundation. Это не закрытие всего Stage 2.

## FACT — срез Stage 2B: вся первая сквозная вертикаль

Ветка `stage/02-complete-vertical` от base `bacebe6`. Изменения только в allowlist задания.

- **Проекты.** В `Map.slnx` добавлены `Map.Rendering` (Core + SkiaSharp 3.119.4),
  `Map.Presentation` (view-models без зависимости от Avalonia; INPC написан руками —
  CommunityToolkit.Mvvm не добавлялся), `Map.Platform.Windows` (пути, bounded diagnostics log с
  ротацией, bounded recent-индекс на 10 записей), `Map.App` (Avalonia WinExe, весь XAML-слой в
  code-behind с явными UIA-именами) и тесты `Map.Rendering.Tests`, `Map.Presentation.Tests`,
  `Map.Persistence.KillHost`. Новых runtime-пакетов ноль сверх принятых Avalonia 12.1.1 и
  SkiaSharp 3.119.4.
- **Core.** `Viewport` (zoom-at-cursor с clamp, fit), `EditingSession` (immutable snapshot,
  selection, revision/savedRevision, dirty; редактирование только командами),
  `AddObjectCommand`/`TransformObjectCommand` над снапшотами, `SceneGeometry.HitTest` с учётом
  поворота, `SelectionHandles` — математика gizmo: 8 ручек растяжения + ручка поворота, resize с
  пином противоположной стороны в мировых координатах при любом повороте, clamp на MinSize без
  инверсии, нормализация угла в (-180,180].
- **Rendering.** `SceneRenderer` (страница, слои с opacity, Rectangle/Ellipse с поворотом,
  selection-gizmo constant-screen-size только по opt-in) и `ThumbnailRenderer` 320×180 PNG через
  тот же evaluator, без overlay.
- **Persistence gate.** Bounded `.bak` при replace; production `RecoveryJournal`
  (Started/TempDurable/Replaced, durable запись, unknown-state урок Fix 02);
  `RecoveryCandidates` — Find (кандидат обязан валидироваться полным reader-ом), Restore (только
  чтение, target не трогается), Discard (удаляет ровно записанные temp+journal). Hardening
  чтения: дубликаты entries, неканонические/абсолютные/`..` пути, лимиты
  entries/размеров/суммарного разжатия, PNG-подпись+IHDR-размеры preview, null-shape JSON,
  unknown object kind — всё типизированные ошибки. **Настоящий process-kill тест**: child
  (`Map.Persistence.KillHost`) сигналит после durable temp, убивается до replace; старый target
  открывается и равен старой версии, journal читается, кандидат открывается и равен новой,
  recovery API отдаёт новую версию не трогая старую.
- **UI.** Dashboard (recent-карточки с настоящими миниатюрами из контейнера, honest «Файл не
  найден», recovery-баннер Восстановить/Отбросить), мастер новой карты с валидацией размеров
  16–65536 и сменой ориентации, editor: canvas (select/move/**resize/rotate ручками gizmo** с
  курсорами-подсказками, pan средней кнопкой, wheel-zoom вокруг курсора), inspector
  X/Y/W/H/поворот с commit одним command, undo/redo, статусы
  Сохранено/Сохранение…/Не сохранено/Ошибка сохранения, фоновое сохранение с
  semaphore-guard и thumbnail из снапшота, dirty-close Сохранить/Не сохранять/Отмена, Ctrl+N/O/S/Z/Y,
  диагностический лог без содержимого документов.
- **Тесты: 103/103** (33 Core + 37 Persistence + 5 Rendering + 28 Presentation), Release
  0 warnings/0 errors, `dotnet format` чист.
- **UI smoke 12/12** по Release EXE (`scripts/drive-stage02.ps1`, самопроверяющийся: UIA-état,
  bounded waits, все PNG полного размера `2576×1408`): dashboard → wizard → добавление →
  selection по клику с GUID → drag-move → числовой resize/rotate → undo/redo по значениям →
  pan/zoom по статусу → Save As через системный диалог → «Сохранено» → чистое закрытие
  (exit 0) → новый процесс → recent-карточка с миниатюрой → reopen → выбор объекта: **тот же
  GUID** и та же геометрия `X=766.96 Y=427.97 W=400 H=260 R=30`. Артефакты:
  `artifacts/stage02b/ui-smoke/`. TCP-listeners 0; WebView/Chromium-модулей нет; загружены
  `av_libGLESv2.dll`, `d3d11.dll`, `dcomp.dll` (GPU-путь как в Stage 1).
- Recovery в настоящем окне: прерванное сохранение (KillHost в production-журнале) даёт на
  dashboard баннер «Несохранённая копия … Восстановить/Отбросить» — снимок
  `artifacts/stage02b/recovery-demo/dashboard-with-recovery.png`.
- Найденные и решённые в срезе проблемы драйвера: `InvokePattern` недетерминирован рядом с
  модальными диалогами (заменён физическими кликами по UIA-прямоугольникам); owned-окна Avalonia
  не видны как дети UIA-Root (искать в поддереве главного окна); системный диалог сохранения
  ловится по foreground `#32770`, путь вставляется через буфер обмена.

## FACT — сокращённая независимая проверка Stage 2B

- Точный range `bacebe6a755cfc2d328b61774f91f14a5ae8da1f..`
  `cda582ffd54b0724afe04043d249c7ef726c19cd`: `A=45`, `M=9`, `D=0`, `R=0`, 54 файла,
  `+6125/-25`; allowlist соблюдён, tracked artifacts/bin/obj отсутствуют, diff/worktree чистые.
- Codex проверил production project graph, renderer/viewport, настоящий child-process kill test,
  UI evidence и ключевые screenshots dashboard/editor/reopen/recovery.
- Свежая Release-сборка прошла с 0 warnings/errors; тесты **103/103** (33 Core, 37 Persistence,
  5 Rendering, 28 Presentation).
- Полный UI driver повторно не запускался по решению владельца экономить проверочный ресурс;
  сохранённые 12 кадров имеют `2576×1408`, evidence согласован с видимыми состояниями и файлом.
- Stage 2 принят. UI функциональный, но визуальный стиль остаётся `PROPOSED`; автоматизация
  recovery-баннера, UIA-peer canvas и shortcut smoke остаются `PARTIAL`, не блокерами.
- Large-map audit перед Stage 3: canvas рисуется прямо в поверхность окна без full-canvas bitmap,
  но `SceneRenderer` пока линейно перебирает все objects. До массовых слоёв/кисти/ассетов обязательны
  spatial culling, tiled raster и LOD согласно DEC-009.

## FACT — Stages 3–5: large-map foundation, assets и Land

- Stage 3 (`1afdf35`): полиморфные слои, tiled brush/masks, spatial culling, checksummed tile
  persistence, UIA-холст и вкладки слоёв/объектов. Профиль 50 000 объектов возвращает 28
  кандидатов; render median 0,748 мс, P95 1,001 мс.
- Stage 4 (`577abdf`): локальный каталог ассетов, SHA-256 identity/dedupe, lazy decode/cache,
  embedded/linked roundtrip, stamp/replace/scatter/placeholder. 10 000 metadata records грузятся
  за 141,947 мс; warm search P95 1,344 мс; originals decoded при открытии каталога: 0.
- Stage 5: несколько независимых Land layers, add/subtract brush, circle/rect/ellipse/polygon/
  grid-block/bounded-fill, bounded move-area, masks, blend и небейкнутые edge styles. Изменение
  стиля не меняет coverage bytes; save/reopen сохраняет геометрию, стиль, mask и blend.
- Deep zoom доступен в диапазоне 0,5–6400%; мастер позволяет создать целевой canvas
  65 536×65 536. Интерактивный Land preview median 0,656 мс; exact idle median 75,094 мс.
- `scripts/drive-stages03-05.ps1` прошёл в настоящем Release-окне: карта 65 536×65 536,
  brush/mask/land gestures, land shape/style/move, blend, undo/redo, UIA Pane холста и отсутствие
  TCP listeners. Снимок просмотрен; переполнение toolbar исправлено переносом на вторую строку.
- Во время smoke исправлены два пользовательских blocker: pointer release теперь завершает
  raster gesture, а wizard больше не отсекает целевой large-map profile.
- Сырые числа и методика: `docs/benchmarks/STAGES_03_05.json` и
  `docs/benchmarks/STAGES_03_05.md`. Артефакты окна лежат в игнорируемом
  `artifacts/stages03-05/ui-smoke/`.
- **PARTIAL:** native smoke проверяет наличие Stage 4 controls, но повторно не проходит системный
  file-dialog импорта. Полный visual/coverage/mutation/package аудит пропущен по решению владельца.

## FACT — Stages 6–8, Editor UX и Stage 9A

- Stages 6–8 находятся в отдельных feature-коммитах: `b5cf81c` (Line/Shape), `2562645`
  (Text/Notes/Grid), `d657dcf` (Filters/Compositions). Их функциональная основа и общий native
  regression driver проверены; отдельного формального review-документа для каждого этапа нет.
- Editor UX Rebuild и последующие fixes приняты коммитом `2e3315a`; отчёт:
  `docs/reviews/EDITOR_UX_REBUILD_VERIFICATION.md`.
- Clean-room контракт полного object inspector и grid property sheet принят как `DEC-010` и
  записан в `docs/research/OBJECT_AND_GRID_PARITY.md`. Текущий движок уже поддерживает три grid
  topology и большую часть stamp effects, но полный прямой UI относится к Stage 10.
- Stage 9 разбит контрактом `f29bbfe` на 9A–9E. Код 9A зафиксирован коммитом `03372d7`: две
  bounded autosave generations, atomic metadata pointer, fallback на предыдущую валидную копию,
  startup offer/restore, отдельный autosave status и единый gate с manual save.
- Свежая Release-сборка 9A: 0 warnings/0 errors; тесты **195/195**
  (63 Core + 49 Persistence + 16 Rendering + 6 Assets + 61 Presentation); analyzers, NuGet
  vulnerable/deprecated audits, pre-commit и doctor прошли.
- Нативный аварийный smoke `scripts/drive-stage09-autosave.ps1` прошёл на изолированном recovery
  root: dirty → autosave → kill owned process → новый процесс → dashboard offer → restore dirty;
  0 TCP listeners, 3/3 PNG `2576×1408` просмотрены.
- Полный отчёт 9A: `docs/reviews/STAGE_09A_AUTOSAVE_VERIFICATION.md`.

## Следующий безопасный шаг

Один отдельный issue-sized срез Stage 9B: именованные checkpoints и безопасный возврат к ним.
Не смешивать с 9C resize/crop/new-from-region, 9D export/cancellation, 9E migrations/diagnostics
или Stage 10 object/grid parity. Основание и точные gates 9A:
`docs/reviews/STAGE_09A_AUTOSAVE_VERIFICATION.md`.

## FACT / PARTIAL — Stage 10 input parity, 26.08.2026

- На branch `stage/10-input-parity` начат отдельный owner-requested срез поверх `03372d7`.
- Добавлены работающие defaults: `V/M/B/O/R/L/T/N`, `K/?`, `/`, `G`, `Tab` на canvas,
  `Ctrl+Z/Ctrl+Shift+Z`, Space-pan, select move/size/rotate/opacity/sublayer/lock/delete/copy,
  Brush/Mask/Text adjustments и modifier-wheel. `E` больше не конфликтует с отдельным Land key.
- `G` теперь действительно переключает `GridDefinition.SnapEnabled` одной undoable command, а не
  выполняет одноразовый snap выбранного объекта.
- Clean-room account walkthrough, поправка отдельной isometric topology (DEC-011), default keyboard
  profile (DEC-012) и два planned diagnostic assets записаны в `docs/research/`.
- Release build: 0 warnings/errors; полный suite: **199/199**.
- Native UIA подтвердил B/L/R/V, O/N, G и Ctrl+Z/Ctrl+Shift+Z в настоящем окне; доказательный кадр:
  `artifacts/stage10-input-parity/ui-smoke/15-keyboard-only.png`.
- Полный regression-driver после успешного keyboard gate остановился на старом minimum-window
  ожидании: запрошено client `960×640`, наблюдалось `1200×800`. Это `PARTIAL`, не исправлялось в
  input-срезе и требует отдельной DPI/window-size диагностики.
- Ещё не реализованы: Ctrl+A/Ctrl+click multi-selection и group/ungroup; stamp Area/Density/set
  navigation; text font cycling; snap rotation; отдельная `IsometricSquares`; полные inspectors и
  сами два diagnostic PNG.

## FACT / PROPOSED — продолжение clean-room анализа и контракт Stage 10B, 26.08.2026

- В test map через обычный UI подтверждены: Ctrl+A по stamp filter, создание именованной группы
  из 8 объектов, Group Options/Enter Group; Brush pen dynamics/size+opacity jitter/spacing/scatter;
  Stamp Auto/Auto Select; Line Portals instructions для Wall/Room; каталог search/sort/group/
  filters/recent/favorite и logical set с badge, inline expansion, `N/total`, previous/next и
  random-after-placement.
- Не подтверждены additive Ctrl+click, физические pen pressure curves, точная portal width и
  надёжное `]/[` font cycling; они остаются `PARTIAL`.
- Во время разрешённого walkthrough тестовая cloud map изменена: создана группа `AnalysisGroup`,
  добавлен один diagnostic brush stroke и один наблюдаемый set отмечен favorite. Ничего не
  удалялось; чужие assets/screenshots не входят в Git.
- Сопоставление кода: catalog уже имеет SHA/dedupe/provenance/atomic index/10K metadata search,
  а document сохраняет конкретный variant SHA. Отсутствуют schema v2, collections/sets/variants,
  migration, rich virtualized Library, recent/set favorite и multi-SHA placement command.
- Владелец принял DEC-013 и DEC-014: полностью локальная Library и logical asset sets с десятками
  variants; `.mapproj` первого foundation-среза остаётся на прежней schema и сохраняет конкретные
  размещённые hashes.
- Clean-room evidence/model: `docs/research/ASSET_CATALOG_AND_VARIANT_SETS.md`.
- Готов один self-contained крупный prompt Opus:
  `docs/prompts/STAGE_10B_ASSET_LIBRARY_VARIANT_SETS.md`.
- Diagnostic scope уточнён до двух logical fixtures: 12-variant stamp set + singleton brush tip.
- Это подготовка/контракт, не реализация: код Stage 10B, новые PNG и его gates ещё `PROPOSED`.

## FACT / PARTIAL — Stage 10B выполнен, 26.08.2026

Branch `stage/10-asset-library-sets`, BASE `70148c5`, один commit Opus. Ожидает независимой
проверки Codex; Stage 10 в целом остаётся `PARTIAL`.

- Library schema v2: версионированный `catalog.json` с `records/collections/variants/sets/recent`.
  Миграция плоского v1-массива создаёт singleton sets с id, выведенными из SHA-256, сохраняет все
  поля записи, пишет `catalog.v1.<timestamp>.json` и идемпотентна при повторном открытии.
- Повреждённый индекс переводит каталог в read-only `Damaged`, показывается в панели и **не**
  перезаписывается пустым каталогом; индекс новой schema сообщает о себе, а не деградирует тихо.
- Query по логическим карточкам: text/collection/tag/untagged/favorite/recent/used-in-map/
  media type/imported range, 4 сортировки, группировка по коллекции и тегу, дерево коллекций
  со счётчиками и bounded окно видимых плиток.
- Профиль 10 000 логических sets: load `< 2 s`, warm query P95 `≤ 100 ms`, каталог `originals`
  вообще не создаётся — ни один оригинал не декодируется.
- `StampPlacement` — собственная измеримая семантика: Area = радиус в world units, Density =
  штампов на 10 000 кв. единиц, `count = clamp(round(density*PI*r^2/10000), 1, 5000)`, равномерная
  выборка в круге и собственный SplitMix64 PRNG (framework `Random` не гарантирует стабильность).
- `PlaceAssetObjectsCommand` принимает несколько `MapAssetReference`: один gesture = одна запись
  истории; undo снимает только введённые этой командой и больше не используемые ссылки; redo
  возвращает тот же план без повторного розыгрыша.
- Настоящее окно (`scripts/drive-stage10b-asset-library.ps1`, 15 PNG): импорт singleton и набора
  из 12 вариантов, дерево/breadcrumbs/поиск/фильтры/favorite/recent, `Вариант 1/12` мышью и
  `Alt+←/Alt+→`, `Q/A` и `Alt+Q/Alt+A`, One и Area, undo/redo, save и reopen без каталога,
  missing/hash-mismatch и damaged index. Сохранённая карта: **323 штампа, 12 разных SHA**.
- Два собственных diagnostic fixtures в `tests/fixtures/assets/`: 12 PNG 512×512 RGBA + один
  256×256 brush tip, генератор `tools/Map.DiagnosticAssets` воспроизводит их byte-identical.

`PARTIAL` этого среза:

- High-DPI: окно рендерится одинаково при 100 % и 200 % (normalized RMSE `0,0034`);
  `AVALONIA_SCREEN_SCALE_FACTORS` в этой среде не применяется. Доказан только запуск и разрешение
  контролов, не реальное масштабирование.
- Нативный файловый диалог Windows в этой среде не открывается и не возвращается — проверено и на
  прежнем пути одиночного импорта. Поэтому driver передаёт пути через `MAP_UI_TEST_*`, а не через
  OS-диалог; сам диалог остаётся непроверенным автоматически.
- Вне allowlist изменён один файл: `src/Map.Presentation/ViewModels/EditorLabels.cs` — три
  additive ветки для нового `EditorTool.Stamp`, которых требует существующий тест «у каждого
  инструмента есть имя, клавиша и подсказка». Требует подтверждения владельца.
- Найдено, но не исправлено (соседняя область): в статус-баре длинный текст состояния вплотную
  примыкает к «Масштаб NN %» без отступа.

Следующий owner-approved шаг: независимая проверка Codex `BASE..HEAD`, затем отдельный срез
10C multi-selection/groups. Не смешивать с 10D inspectors, 10E `IsometricSquares`, 10F fonts
или Stage 9B.

## PROPOSED — Stage 10B Fix 01 выполнен, 26.08.2026

Branch `stage/10-asset-library-sets`, BASE `8ef4e4f`, один новый commit Opus поверх него.
Исправляющий срез по независимому Codex-review Stage 10B. **Ожидает независимой проверки Codex**;
до неё ничего из перечисленного не является `FACT`. Stage 10 в целом остаётся `PARTIAL`.

Что закрыто из findings Codex:

- Группировка больше не декоративная: `Без групп / По коллекции / По тегу` меняют одну и ту же
  виртуализированную поверхность. `AssetLibraryRow` — плоская последовательность header/card, один
  `ListBox` реализует только видимые контейнеры; header никогда не становится выбранным набором и
  не блокирует навигацию с клавиатуры.
- Панель фильтров получила недостающие контролы: exact tag, media type, imported from/to, сброс
  всех фильтров и строку активных фильтров словами. Icon-only тумблеры заменены подписанными.
- Управление набором: переименование (пустое имя отклоняется), перемещение текущего варианта
  раньше/позже, назначение обложкой. Set ID и variant IDs не меняются, изменения переживают
  перезапуск каталога.
- Явные состояния содержимого: `available / checking / missing / hash mismatch / unsupported`
  видны на карточке, в полосе вариантов и в UIA-имени. Проверка ограничена видимым окном и
  выбранным набором, идёт вне UI-потока, устаревший результат отбрасывается по generation.
- One/Area gesture стал атомарным: сначала неизменяемый plan, затем разрешение каждого уникального
  SHA ровно один раз, и только потом одна команда. Любой недоступный SHA отменяет весь жест;
  документ, assets, история, selection, recent, текущий вариант и dirty-состояние не меняются, а
  сообщение называет конкретный вариант и причину.
- Статус успешного One называет фактически размещённый вариант, а не следующий.
- Seed жеста больше не зависит от randomized string hash: `GestureSeed.From` — фиксированный
  FNV-1a по байтам кортежа, закреплён golden-значениями и pin-тестом на месте вызова.
- `used-in-current-map` учитывает stamps, line stroke, shape stroke, shape texture fill, вложенные
  composition items и filter textures через существующий `CompositionTransfer.UsedAssetHashes`.
- `AssetLibraryStore.Validate` проверяет полную схему v2 за линейное время: null-списки, уникальные
  SHA/variant/set/collection ID, ссылки variant→record, set→variant/collection, обложка внутри
  набора, отсутствие self-parent и циклов коллекций, согласованность recent и current variant.
  Семантически повреждённый индекс открывается как `Damaged`, остаётся read-only, сохраняет байты
  и не доходит до `CollectionTree`.
- `ReportRealizedLibraryRows` считает только реально созданные контейнеры, а не все строки
  результата.
- Устранены 7 analyzer-findings, введённых Stage 10B (6 × `CA1859`, 1 × `IDE0042`).

Отдельное решение владельца внутри этого среза: **scatter размножает вариант, а не крутит его**.
`Случайный угол` и `Случайное отражение` теперь выключены по умолчанию и остаются явными
тумблерами.

Проверки этого среза (все выполнены свежими прогонами):

- Release build: 0 warnings / 0 errors; полный suite **333/333** (Core 82, Rendering 16,
  Persistence 52, Assets 65, Presentation 118).
- `dotnet format --verify-no-changes --severity info` по-прежнему возвращает **exit 2** из-за
  legacy-базы репозитория: 263 → 256 findings, **ни одного нового**. Утверждать «format успешен»
  нельзя.
- NuGet: уязвимых пакетов нет; из устаревших — только прежний `xunit 2.9.3` (Legacy).
- `pre-commit run --all-files` и `scripts/doctor.ps1`: PASS.
- Профиль 10 000 логических наборов: load `436,7 мс`, warm query median `1,034 мс`,
  P95 `3,202 мс` (30 измерений после прогрева), originals не читаются.
- Жест 5000 штампов / 12 SHA: resolver вызван ровно **12** раз; median `32,8 мс` по 5 прогонам.
- Targeted mutation testing (local Stryker.NET 4.16.0): atomic placement + resolver seam
  50,00 % → **75,00 %**; `AssetLibraryDocument` 75,85 % → **79,23 %**. Каждый оставшийся
  `Survived`/`NoCoverage` разобран: константы `image/jpeg`/`image/webp` (нет JPEG/WebP fixtures),
  `ArgumentNullException.ThrowIfNull` guard-clauses, недостижимые catch-ветви гонки
  «файл исчез после проверки», и message-строки правил, существовавших до этого среза.
- Настоящее окно: `scripts/drive-stage10b-asset-library.ps1` прошёл **три раза подряд** на свежей
  Release-сборке без ручного вмешательства (один полный прогон с DPI-секцией, два с `-SkipDpi`),
  23 + 20 + 20 PNG, 98 UIA-проверок за прогон. Сохранённая карта: **323 штампа, 12 разных SHA**,
  все объекты в файле без поворота и зеркала. После прогонов менялись только тестовые файлы и
  переносы строк.

`PARTIAL` / `BLOCKED` этого среза:

- **High-DPI остаётся `PARTIAL`.** Окно рендерится одинаково при 100 % и 200 %
  (normalized RMSE `0,00188`); `AVALONIA_SCREEN_SCALE_FACTORS` эта среда не применяет. Доказаны
  только запуск и разрешение контролов, не реальное масштабирование.
- **Нативный файловый диалог Windows остаётся `BLOCKED`.** Драйвер передаёт пути через
  `MAP_UI_TEST_*`; сам OS-диалог автоматически не проверен.
- Тег для проверки exact-tag фильтра и tag-группировки вписан в индекс напрямую: UI для
  редактирования тегов в этой сборке нет.
- Соседний дефект статус-бара (текст вплотную к «Масштаб NN %») не исправлялся: вне среза.

Stage 10C multi-selection/groups **не начат**. Следующий шаг — независимая проверка Codex точного
диапазона `8ef4e4f..HEAD`.

## PROPOSED — Stage 10B Fix 02 и UX-аудит Library, 27.08.2026

BASE `eb2e23b`, branch `stage/10-asset-library-sets`. В текущем авторском контексте выполнен
отдельный Fix 02 по двум findings независимой проверки Fix 01; до clean-context review это
`PROPOSED`, а не независимый `FACT`.

- `FuncDataTemplate` карточек больше не включает recycling для визуалов, которые захватывают
  конкретный `AssetLibraryRow`. После scroll/filter строка, UIA/selection, заголовок, миниатюра и
  badge остаются одним ассетом.
- Состояния видимых карточек хранятся как список на SHA, а не одна запись: набор с двумя тегами
  получает missing/hash-mismatch badge во всех реализованных tag-группах. Отсоединённые визуалы
  удаляются из списка, поэтому длительная прокрутка не превращает его в историю всех карточек.
- Driver создаёт 62 валидных logical sets, прокручивает список, фильтрует до одной строки и
  сравнивает видимый текст с UIA/selection row; отдельно проверяет две tag-копии одного набора и
  реальный фильтр «На карте» после размещения.
- Добавлен `-Background`: окно остаётся за пределами видимого рабочего стола, не активируется и
  снимается через `PrintWindow`. Горячие клавиши и DPI честно помечаются `PARTIAL` в таком прогоне
  и остаются для одного короткого foreground gate.
- Свежий background Release-smoke: PASS, 21 PNG `2000×1250`, 323 stamps, 12 SHA, large-list
  visual match, used-in-map, two-tag state fan-out, undo/redo, reopen, missing/mismatch, atomic
  refusal и damaged index. Артефакты:
  `artifacts/stage10b-fix02-green-background2/` (игнорируются Git).

UX-аудит свежих снимков подтвердил отдельную проблему: узкая Library-панель одновременно
показывает поиск, sort/group, семь фильтров, коллекции, карточки, управление набором и размещение.
Владелец явно запросил упрощение. Следующий отдельный commit обязан сохранить все функции, но
оставить видимым основной путь «найти ассет → выбрать вариант → разместить», а редкие фильтры и
редактирование набора спрятать в понятные раскрываемые секции. Stage 10C всё ещё не начинается.

## PROPOSED — Stage 10B UX-срез упрощённой Library-панели, 27.08.2026

BASE `dfc2d1d`, branch `stage/10-asset-library-sets`, один отдельный commit авторского контекста.
Это self-check Opus, а не независимая приёмка; до clean-context review Codex ничего из
перечисленного не является `FACT`.

- Default-путь Library сокращён до основного сценария: breadcrumbs, текстовые кнопки
  `Добавить файл / Добавить набор / Связать файл`, поиск, быстрые фильтры
  `Избранное / Недавние / На карте`, счётчик `Ассеты · N` и карточки. Сортировка, группировка,
  `Без тегов`, точный тег, формат и диапазон дат живут в свёрнутом Expander `Фильтры и порядок`;
  дерево коллекций — в свёрнутом `Коллекции`; переименование/порядок/обложка — в свёрнутом
  `Настроить набор`. Ошибка даты автоматически раскрывает секцию фильтров, подсказка даты видна
  только при ошибке.
- Строка активных фильтров и `Сбросить` показываются только при активных фильтрах. Блоки
  `Выбранный ассет` и `Как разместить` не существуют в UI, пока набор не выбран. `Один штамп`
  скрывает радиус/плотность/итог и секцию `Разброс и поворот`; `Россыпь` показывает их и
  отдельный всегда видимый toggle `Смешивать варианты`. Availability copy на панели короткая
  (`Готов к размещению` / `Файл варианта не найден` / `Файл изменён после импорта`), а UIA
  Name/tooltip начинаются с видимого текста и сохраняют точную диагностику в скобках.
- Оба файла среза (`EditorView.cs`, `drive-stage10b-asset-library.ps1`) заново сохранены как
  UTF-8 with BOM + CRLF: переданный черновик потерял BOM, из-за чего Windows PowerShell 5.1
  читал кириллицу скрипта как ANSI-mojibake и падал на парсинге.
- Свежий фоновый Release-прогон `scripts/drive-stage10b-asset-library.ps1 -Background -SkipDpi`
  на `artifacts/stage10b-fix02-ux-final-background/`: **PASS, 21 PNG 2500×1460, 96 записанных
  проверок** (94 `ok`-assertions в логе), `report.json` там же. Повторно доказаны: empty,
  import singleton + 12 вариантов, collapsed defaults, breadcrumbs, поиск, sort/group/filter,
  выбранный набор, `Настроить набор`, One без scatter-контролов, Scatter с area/density/итогом,
  rename/reorder/cover + restart, каталог 62 sets выше окна, scroll/filter visual-to-row match,
  реальная tag-группировка и две tag-копии, One placement, used-in-map, Area с 12 SHA,
  undo/redo, сохранение **323 штампов / 12 distinct SHA** без поворота и зеркала, reopen без
  каталога, missing/hash mismatch, atomic refusal (`Ничего не размещено`), damaged index без
  перезаписи. Окно не активировалось; правая панель подтверждена внутри захвата.
- Все 21 финальных PNG открыты через vision и разобраны письменно: hierarchy, подписи,
  выравнивание, expanded/collapsed, отсутствие пустых блоков. Найденные наблюдения: прежний
  соседний дефект статус-бара (текст вплотную к «Масштаб NN %», вне среза) и усечение длинного
  имени карточки многоточием при появлении badge состояния (пользовательский контент; полное имя
  остаётся в UIA/tooltip).
- Свежие проверки: Release build 0 warnings/0 errors; полный suite **333/333** (Core 82,
  Persistence 52, Rendering 16, Assets 65, Presentation 118); doctor PASS; NuGet vulnerable — нет,
  deprecated — только прежний `xunit 2.9.3` (Legacy); `python -m pre_commit run --all-files` PASS.
  `dotnet format --verify-no-changes --severity info` по-прежнему **exit 2** (legacy-база):
  106 уникальных file:line diagnostics по solution, **0 внутри изменённых строк среза**;
  24 finding в `EditorView.cs` — прежние `CA1859` нетронутых методов Build*.
- Посторонних процессов Map.App после прогонов нет; изменены только два разрешённых файла.

`PARTIAL` / `BLOCKED` этого среза: горячие клавиши и High-DPI не доказаны фоновым режимом и
остаются `PARTIAL`; нативный файловый диалог Windows остаётся `BLOCKED`; независимая проверка
Codex выполнена и описана следующим разделом. Stage 10 и 10B остаются `PARTIAL`; Stage 10C не
начат.

## FACT / PARTIAL — Codex-review Stage 10B и фоновые ComboBox-фильтры, 27.08.2026

- Точный Claude-диапазон `dfc2d1d..172f1b4` пересчитан: `A=0`, `M=5`, `D=0`, `R=0`, tracked
  files `262`; solution и project references валидны, блокирующих production-findings нет.
- Два найденных пробела относились к доказательствам, а не к production-логике: background-driver
  пропускал выбор media type и exact tag. Условия сняты; оба ComboBox выбираются через UI
  Automation без активации окна, exact-tag состояние сохраняется отдельным PNG.
- Свежий background Release-driver: PASS, `22` PNG `2500x1460`, `99` checks, сохранённый
  `.mapproj` содержит `323` stamps / `12` distinct SHA. Повторно прошли undo/redo, reopen без
  каталога, missing/mismatch, atomic refusal и damaged index.
- Свежие gates: doctor PASS; restore PASS; Release build `0` warnings / `0` errors; tests
  `333/333`; vulnerable packages — нет; deprecated — только прежний `xunit 2.9.3` Legacy.
  `dotnet format` сохраняет legacy failure и не затрагивает изменённый PowerShell-driver.
- Подробный артефакт: `docs/reviews/STAGE_10B_ASSET_LIBRARY_VERIFICATION.md`; runtime-артефакты:
  `artifacts/stage10b-codex-filter-fix-final/` (игнорируются Git).
- `PARTIAL`: keyboard-only и реальный Windows scale 100/150/200% намеренно не проверялись в
  фоне. `BLOCKED`: native file picker под automation host и независимая clean-context UX-критика.
- Следующий функциональный срез дорожной карты — 10C multi-selection/groups. Не смешивать его с
  10D inspectors, 10E grid geometry, 10F fonts или Stage 9B–9E.

## PROPOSED / PARTIAL — Stage 10C multi-selection/groups выполнен, 27.08.2026

Branch `stage/10c-multi-selection-groups`, BASE `58bef7a`, один commit Opus. Это self-check
авторского контекста, не независимая приёмка; до clean-context review Codex ничего из
перечисленного не является `FACT`. Stage 10 в целом остаётся `PARTIAL`.

- `EditingSession` держит упорядоченное множественное выделение без дублей с primary,
  restore предыдущего набора (`` ` ``/`~`) и автоочисткой удалённых ID в execute/undo/redo;
  прежний `SelectedObjectId` сохраняет контракт «заменить всё выделение».
- Именованные группы поверх существующего `GroupId`: nullable `GroupName` у членов, инвариант
  модели «один GroupId — одно имя, имя только с GroupId», `SetGroupCommand.Group(ids, имя)`
  (минимум два несгруппированных, имя обрезается, ≤80), `Ungroup`, `Rename`; redo возвращает
  тот же GroupId/GroupName. `DeleteObjectsCommand` удаляет через несколько Object layers одной
  записью истории и в той же транзакции распускает singleton-остаток группы.
- `.mapproj`: `groupName` пишется только когда задан (старые файлы байт-в-байт); файл с разными
  именами одного GroupId или именем без GroupId открывается как corruption. schemaVersion не
  поднимался.
- Новый Core-файл `MultiSelectionGeometry`: объединённые bounds, translate/scale-от-центра/
  rotate-вокруг-центра c нормализацией углов, ручки общей рамки, marquee-подбор по видимым
  незаблокированным объектам видимых незаблокированных слоёв.
- `RenderOptions.CoSelectedObjectIds` реально подключён; добавлены `SelectionFrame` (общая
  рамка с ручками постоянного экранного размера) и `Marquee`; export/thumbnail-путь их не
  получает — проверено pixel-тестом.
- `EditorViewModel`: клик по члену группы выбирает всю группу, внутри открытой группы — ребёнка;
  Ctrl+click/Ctrl+marquee — additive; Esc-каскад (выход из группы → снятие выделения); `Ctrl+A`
  по поиску и типовому фильтру панели объектов (скрытые/заблокированные не входят); мульти
  move/resize/rotate/sublayer/opacity/lock/visibility/delete одной командой; геометрия
  отклоняется целиком при locked/hidden члене; no-op не пишет историю; `ObjectTreeRows` —
  тестируемая модель строк дерева (group row / breadcrumb / дети с разными стабильными UIA-id).
- UI: фильтр типов и кнопка «Выбрать всё (Ctrl+A)» в панели «Объекты», group row одной строкой
  с количеством детей, breadcrumb «← Все объекты», мульти-панель свойств
  («Выбрано N…»/«Группа «Имя» · N…», имя группы, group/ungroup/enter, честное «Разные
  значения»), список объектов сообщает UIA multi-select через собственный automation peer.
  MainWindow: Ctrl+A/Ctrl+G/Ctrl+Shift+G/Esc/Delete/`` ` `` подключены с прежним guard
  «не срабатывать при вводе текста».
- Найденный и исправленный внутри среза дефект: `WithGeometry`/`CopyObject` view-model теряли
  `GroupName` при живом `GroupId` — любое редактирование члена группы падало на инварианте
  модели. Пойман фоновым драйвером, доказан red/green тестом
  `Edits_over_grouped_members_keep_the_group_name`.
- Проверки свежими прогонами: Release build 0 warnings/0 errors; полный suite **430/430**
  (Core 137, Persistence 57, Rendering 20, Assets 65, Presentation 151); doctor, restore,
  pre-commit — PASS; vulnerable нет; deprecated — прежний `xunit 2.9.3` Legacy;
  `dotnet format --verify-no-changes --severity info` — прежний legacy exit 2, **ни одного
  прироста диагностик** относительно зафиксированного baseline (256 → 241 по file:line).
- Targeted coverage (Core-тесты): `MultiSelectionGeometry` 96%/82.5%, `SetGroupCommand`
  92.7%/83.3%, `DeleteObjectsCommand` 92.3%/94.4%, `EditingSession` 91.3%/81.5%.
- Targeted Stryker (три итерации + финальный прогон): `MultiSelectionGeometry` 104 killed /
  14 survived (85.25%); валидация групп `MapDocument` — 0 survivors. Каждый оставшийся
  survivor разобран: guard-clauses `ThrowIfNull`, недостижимая защита вырожденных размеров,
  эквивалентные мутанты (`First→FirstOrDefault`, primary-fallback), message-строки и прежний
  долг Revision/PendingChanges вне среза. Ручные мутанты убиты red/green: пропуск записи
  `groupName` в persistence и частичное изменение при locked-члене.
- Рискованные group-transform тесты прогнаны 5 раз подряд — стабильно зелёные.
- Фоновый driver `scripts/drive-stage10c-multi-selection-groups.ps1` (по умолчанию
  `-Background`): изолированные `artifacts/stage10c-*`, окно вне рабочего стола, PrintWindow,
  свежий прогон **PASS: 16 PNG 2500×1460, 36 проверок**; сценарий: 8 объектов 4 видов на двух
  слоях, одиночный выбор в дереве, фильтры, «ровно три» по поиску+фильтру, группа
  «Тестовая группа», undo/redo с тем же именем, вход/выход по breadcrumb, save→reopen с
  проверкой одного GroupId и одного GroupName прямо в scene.json, ungroup+undo/redo,
  «Разные значения» при locked-ребёнке, доступность заблокированного через дерево, распуск
  singleton, delete через оба слоя одной записью. Все 16 PNG открыты в vision и разобраны.
  Важно для будущих драйверов: свежезапущенное окно и каждый модальный диалог забирают
  foreground, и клавиши владельца утекали в скрытое окно — драйвер теперь минимизирует окно
  (возврат активации), восстанавливает без активации и запрещает нативный ввод
  (`EnableWindow(false)`); UIA-паттерны продолжают работать.

`PARTIAL` этого среза: canvas marquee, Ctrl+click, Esc и клавиатурные move/scale/rotate группы
не выполняются фоновым драйвером (не перехватываем клавиатуру владельца) — доказаны адресными
unit-тестами, pixel-тестом marquee-chrome и routing-review MainWindow. Прежние `PARTIAL`/
`BLOCKED` 10B сохраняются: keyboard-only gate, High-DPI, нативный file picker, clean-context
UX-критика. Соседний дефект статус-бара (текст вплотную к «Масштаб NN %») по-прежнему не
исправлялся. Следующий шаг — независимый Codex-review точного `BASE..HEAD`; 10D/10E/10F и
Stage 9B–9E не начинать.

## FACT / PARTIAL — независимый Codex-review и Fix Stage 10C, 27.08.2026

- Точный Opus-диапазон `58bef7a..5e87cf5` пересчитан: branch
  `stage/10c-multi-selection-groups`, `A=11`, `M=19`, `D=0`, `R=0`, tracked `274`; перед
  исправлением дерево было чистым.
- Независимо найдены два production-дефекта. `SelectAllByFilter` обходил отдельные объекты всего
  документа, поэтому поиск по имени закрытой группы выбирал `0`, типовой фильтр разрывал группу,
  а внутри открытой группы Ctrl+A захватывал outsiders. Проверка геометрической доступности
  учитывала флаги объекта, но не скрытие/блокировку его слоя.
- Отдельный fix строит Ctrl+A из текущих `ObjectTree` rows: закрытая группа является атомарной,
  открытая ограничивает выбор детьми, а группа с недоступным участником пропускается целиком.
  Единый линейно построенный набор доступных ObjectId также запрещает move/scale/rotate/sublayer/
  opacity для всей выборки, если хотя бы один объект или его слой скрыт/заблокирован.
- Red/green: до production-правки пять адресных cases падали на исходных симптомах; после правки
  проходят. Добавлен шестой guard-case для недоступного участника закрытой группы. Весь класс
  Stage 10C — `33/33`, полный suite — `436/436`.
- Background Release-driver расширен реальными UIA-проверками поиска по имени группы и Ctrl+A
  внутри breadcrumb. Свежий прогон: `PASS`, `16` PNG `2500x1460`, `39` checks, `3` честных
  `PARTIAL`; отчёт `artifacts/stage10c-fix-codex-commit-20260827/report.json`. Окно не активировалось,
  клавиатура владельца не использовалась, процессов `Map.App` после прогона нет.
- Свежие gates: doctor/locked restore/Release build/pre-commit — PASS; build `0` warnings /
  `0` errors; vulnerable packages — нет; deprecated — только прежний `xunit 2.9.3` Legacy.
  `dotnet format --severity info` сохраняет legacy exit `2`; в изменённых строках C# новых
  diagnostics нет.
- Статус: production-логика Stage 10C — `FACT`. Native keyboard/pointer gestures остаются
  `PARTIAL` по безопасному background-режиму; clean-context UX-review этим исправлением не
  подменяется. Следующий функциональный срез — отдельный 10D Object inspectors.

## Проверка среды

Из PowerShell в корне проекта:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```
