# STATE HANDOFF

## Последнее обновление

2026-08-25. Fix 02 принят после сокращённого независимого Gate D Codex, разрешённого владельцем:
проверены точный diff, изменённая ветвь recovery, свежая Release-сборка и 44 целевых теста.
Неизвестное значение `state` recovery-журнала не роняет чтение и не предлагается как
восстанавливаемое. Кодовых `BLOCKED` у Stage 1 нет; можно начинать первый ограниченный срез Stage 2.

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

## Следующий безопасный шаг

Codex проверяет точный diff среза Stage 2A и ключевые тесты. Следующий срез после приёмки —
первый UI foundation slice поверх этой основы (минимальное окно Avalonia c dashboard
recent/new/open, использующее `MapProjectStore`), отдельным prompt-файлом. Opus его не начинал.

## Проверка среды

Из PowerShell в корне проекта:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```
