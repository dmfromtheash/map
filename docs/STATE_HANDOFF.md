# STATE HANDOFF

## Последнее обновление

2026-08-25. Codex завершил независимую проверку Stage 1 по
`docs/11_CLAUDE_VERIFICATION_PROTOCOL.md`. Итог: `BLOCKED` для перехода к Stage 2 из-за
воспроизводимого дефекта geometry chunk culling. Точный fix-prompt для Opus:
`artifacts/opus-prompt-stage01-fix01.md`.

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
- Узкий Stryker только по `AtomicDocumentStore.cs`: 47 протестированных мутантов, 29 killed,
  16 survived, 2 timeout, 9 NoCoverage, score `55,36 %`. Мутант удаления вызова
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

## PARTIAL

- Сколько именно из двух периодов обновления можно вернуть — не доказано. Нужен эксперимент со
  способом запроса кадра и `Win32CompositionMode`, это изменение конфигурации приложения и в
  fix-срез не входило. Числа на 60-герцовом экране не измерялись.
- Сглаживание объектов отключено (`DrawVertices` без MSAA) — визуальный размен ради бюджета.
- Fault injection для «во время `File.Replace`» проверяет наблюдаемый результат, а не разрыв
  внутри самой операции: `ReplaceFile` атомарна на уровне NTFS и не делится из managed-кода.
- В узком Stryker остаются 16 survived и 9 NoCoverage мутантов. Значимые: удаление
  `SpikeDocumentSerializer.Read` внутри `IsReadable` и замена его `return false` на `true`
  (ослабляют признак `TargetStillIntact`), удаление `stream.Flush(flushToDisk: true)`, а также
  три непокрытых сообщения несоответствия внутри `ValidateReadBack`.
- xUnit `2.9.3` и его transitives помечены `Legacy` в package audit.
- Self-contained `win-x64` publish и запуск на чистой VM не выполнялись (вне scope Stage 1).

## BLOCKED

- `RecoveryRecord.StateValue` использует `Enum.Parse` и бросает `ArgumentException` на журнале,
  который является валидным JSON, но содержит незнакомое значение `state` (например, записанное
  более новой версией). Из-за этого `HasRecoverableWork` падает вместо ответа «предлагать нечего».
  Тест намеренно не добавлен: единственное корректное исправление лежит в
  `spike/src/Map.Spike.Persistence/Recovery/RecoveryJournal.cs`, а этот файл **не входит** в
  список путей, разрешённых fix-срезом. Требуется решение владельца на точечную правку
  (`Enum.TryParse` с трактовкой неизвестного состояния как невосстановимого).
- Ручные аккаунтные сценарии Inkarnate требуют входа владельца.
- Автоматическое исследование закрытого UI не выполняется из-за условий использования.
- Точные визуальные токены и производственные ассеты отложены до материалов владельца.

## Следующий безопасный шаг

Fix-срез по `artifacts/opus-prompt-stage01-fix01.md` выполнен и закоммичен отдельным коммитом от
base `fcc2d7f`. Opus остановился. Дальше Codex повторяет Gate D на новом `BASE..HEAD`:
воспроизводит исходный culling-дефект, подтверждает red/green нового regression-теста, прогоняет
весь Gate A и Gate B и относящийся к этапу Gate C. Stage 2 и prompt для Stage 2 не начинать до
`FACT` по Gate D.

Отдельно требуется решение владельца по единственному `BLOCKED`-пункту выше: разрешить ли точечную
правку `RecoveryJournal.cs` вне списка путей fix-среза.

## Проверка среды

Из PowerShell в корне проекта:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```
