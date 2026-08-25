# Stage 01 — независимая проверка

Дата проверки: 2026-08-25.

## Итог

`BLOCKED` для перехода к Stage 2.

Стек из DEC-002 подтверждён и не требует сравнительного Qt spike: настоящее окно использует
GPU/OpenGL через ANGLE поверх Direct3D, время нашего Skia draw укладывается в бюджет, hit-test и
offscreen export имеют большой запас. Этап не принят из-за отдельного алгоритмического дефекта:
geometry chunk culling пропускает объект, который пересекает viewport, если центр объекта лежит
в соседнем chunk.

Следующий шаг — только fix-срез Opus по
`artifacts/opus-prompt-stage01-fix01.md`, затем полный Gate D. Stage 2 до этого не начинать.

## Git identity и область

| Поле | Факт |
|---|---|
| Ветка | `stage/01-technical-spike` |
| Base реализации Opus | `7f03200386f77d8e98dfbdb2074f85315cbb0587` |
| Head реализации Opus | `f80c8e995b05c0c86619e7319e953408801a9f88` |
| Verification/process baseline | `06e494fb15e99eeda87a86d000e0283fe80a224b` |
| Последующие отдельные коммиты | `746e59b` visual gate; `06e494f` удаление cyber-security gates |
| Worktree перед проверкой | чистый |
| Tracked files на baseline `06e494f` | `96` |
| Диапазон реализации `7f03200..f80c8e9` | `A=66, M=1, D=0, R=0`; 67 файлов; `+6595/-16` |
| Процессный коммит `06e494f` | 6 modified files; `+50/-28`; отдельный от кода Opus |

`git diff --check 7f03200..f80c8e9` и staged diff процессного коммита ошибок не показали.
Security/secret scans намеренно не запускались по прямому решению владельца и актуальному
`docs/11_CLAUDE_VERIFICATION_PROTOCOL.md`.

## Полный authored file inventory реализации Opus

```text
M docs/STATE_HANDOFF.md
A spike/Map.Spike.slnx
A spike/README.md
A spike/src/Map.Spike.App/App.axaml
A spike/src/Map.Spike.App/App.axaml.cs
A spike/src/Map.Spike.App/Canvas/MapCanvasControl.cs
A spike/src/Map.Spike.App/Canvas/RendererProbe.cs
A spike/src/Map.Spike.App/Diagnostics/CrashLog.cs
A spike/src/Map.Spike.App/MainWindow.axaml
A spike/src/Map.Spike.App/MainWindow.axaml.cs
A spike/src/Map.Spike.App/Map.Spike.App.csproj
A spike/src/Map.Spike.App/Program.cs
A spike/src/Map.Spike.App/Session/SpikeSession.cs
A spike/src/Map.Spike.App/app.manifest
A spike/src/Map.Spike.Benchmarks/CrashDuringSave.cs
A spike/src/Map.Spike.Benchmarks/Map.Spike.Benchmarks.csproj
A spike/src/Map.Spike.Benchmarks/Measure.cs
A spike/src/Map.Spike.Benchmarks/Program.cs
A spike/src/Map.Spike.Core/Commands/BrushSettings.cs
A spike/src/Map.Spike.Core/Commands/BrushStrokeCommand.cs
A spike/src/Map.Spike.Core/Commands/BrushStrokeSession.cs
A spike/src/Map.Spike.Core/Commands/CommandHistory.cs
A spike/src/Map.Spike.Core/Commands/IEditCommand.cs
A spike/src/Map.Spike.Core/Commands/MaskStrokeCommand.cs
A spike/src/Map.Spike.Core/Commands/MaskStrokeSession.cs
A spike/src/Map.Spike.Core/Commands/TileStrokeRecorder.cs
A spike/src/Map.Spike.Core/Documents/DocumentRehydrator.cs
A spike/src/Map.Spike.Core/Documents/Ids.cs
A spike/src/Map.Spike.Core/Documents/MaskLayer.cs
A spike/src/Map.Spike.Core/Documents/ObjectLayer.cs
A spike/src/Map.Spike.Core/Documents/RasterLayer.cs
A spike/src/Map.Spike.Core/Documents/SpikeDocument.cs
A spike/src/Map.Spike.Core/Documents/SpikeObject.cs
A spike/src/Map.Spike.Core/Documents/TileKey.cs
A spike/src/Map.Spike.Core/Fixtures/DeterministicRandom.cs
A spike/src/Map.Spike.Core/Fixtures/SpikeFixtures.cs
A spike/src/Map.Spike.Core/Geometry/Rect2.cs
A spike/src/Map.Spike.Core/Geometry/Vec2.cs
A spike/src/Map.Spike.Core/Geometry/Viewport.cs
A spike/src/Map.Spike.Core/Map.Spike.Core.csproj
A spike/src/Map.Spike.Core/Spatial/SpatialQueryStats.cs
A spike/src/Map.Spike.Core/Spatial/UniformGridIndex.cs
A spike/src/Map.Spike.Export/Map.Spike.Export.csproj
A spike/src/Map.Spike.Export/OffscreenPngExporter.cs
A spike/src/Map.Spike.Persistence/Atomic/AtomicDocumentStore.cs
A spike/src/Map.Spike.Persistence/Atomic/SavePhase.cs
A spike/src/Map.Spike.Persistence/Container/ManifestDto.cs
A spike/src/Map.Spike.Persistence/Container/SceneDto.cs
A spike/src/Map.Spike.Persistence/Container/SpikeDocumentSerializer.cs
A spike/src/Map.Spike.Persistence/Map.Spike.Persistence.csproj
A spike/src/Map.Spike.Persistence/Recovery/RecoveryJournal.cs
A spike/src/Map.Spike.Persistence/SpikeFormat.cs
A spike/src/Map.Spike.Rendering/HitTester.cs
A spike/src/Map.Spike.Rendering/Map.Spike.Rendering.csproj
A spike/src/Map.Spike.Rendering/ObjectGeometryCache.cs
A spike/src/Map.Spike.Rendering/SceneRenderer.cs
A spike/src/Map.Spike.Rendering/TileImageCache.cs
A spike/tests/Map.Spike.Tests/Core/BrushAndMaskCommandTests.cs
A spike/tests/Map.Spike.Tests/Core/SpatialHitTestTests.cs
A spike/tests/Map.Spike.Tests/Core/ViewportTests.cs
A spike/tests/Map.Spike.Tests/Map.Spike.Tests.csproj
A spike/tests/Map.Spike.Tests/Persistence/FaultInjectionTests.cs
A spike/tests/Map.Spike.Tests/Persistence/ProcessKillRecoveryTests.cs
A spike/tests/Map.Spike.Tests/Persistence/SaveReopenTests.cs
A spike/tests/Map.Spike.Tests/Rendering/RenderAndExportTests.cs
A spike/tests/Map.Spike.Tests/Support/TempWorkspace.cs
A spike/tools/drive-window.ps1
```

Все 7 `.csproj` входят в `spike/Map.Spike.slnx`. Проверка ссылок по каждому `.csproj` прошла;
`Map.Spike.Core` не имеет ProjectReference и package reference. Документированная команда
`dotnet list spike/Map.Spike.slnx reference` сама по себе неверна для `.slnx` в текущем SDK и
возвращает exit 1 — это отдельный документационный finding.

## Acceptance matrix исходного задания

| № | Критерий | Статус | Доказательство |
|---:|---|---|---|
| 1 | `doctor.ps1` проходит | `FACT` | exit 0 после процессного коммита |
| 2 | clean restore/build/test | `FACT` | clean/restore/build/test exit 0; build 0 warnings/errors; 55/55 |
| 3 | native Windows process без listener | `FACT / PARTIAL` | GPU native window и ANGLE наблюдались; прежний полный driver показал TCP listeners 0; свежий полный повтор остановлен потерей focus |
| 4 | 10 000 объектов, pan/zoom, culling и измерения | `BLOCKED` | 10 000 и pan/zoom видны, но edge-culling probe воспроизводит пропуск пересекающегося объекта |
| 5 | spatial hit-test и ожидаемый stable ID | `FACT` | targeted 12/12; benchmark median 0,003 мс, P95 0,007 мс, 22,6 кандидата |
| 6 | brush tiles и одна undo/redo command | `FACT` | targeted 10/10; предыдущие реальные PNG 04–06 показывают stroke/undo/redo |
| 7 | mask изолирована, overlay не в export | `FACT` | render/export 10/10; реальные PNG 07–09; pixel test export без overlay |
| 8 | save/reopen semantic equality | `FACT` | persistence targeted 21/21; свежий `.mapspike` открыт и manifest проверен |
| 9 | fault/kill сохраняет last-good и recoverable work | `FACT / PARTIAL` | 2/2 реальных process-kill и fault suite зелёные; validate/torn-journal test strength требует усиления |
| 10 | настоящий PNG 4096×4096 с alpha | `FACT` | свежий decode: PNG signature, 4096×4096, bit depth 8, colour type 6, 1 353 694 B |
| 11 | все тесты и ручной native smoke | `PARTIAL` | тесты зелёные; полный предыдущий smoke есть, свежий driver остановлен после 2 кадров и второй PNG невалиден |
| 12 | нет production/out-of-scope features | `FACT` | код изолирован в `spike/`; нет dashboard, `.mapproj`, WebView/server/cloud |

Поскольку критерий 4 имеет `BLOCKED`, писать `готово` и выдавать Stage 2 нельзя.

## Свежие автоматические проверки

| Проверка | Exit / результат |
|---|---|
| `python -m pre_commit install` | 0, hook установлен |
| `python -m pre_commit run --all-files` | 0 при фактическом `lychee 0.24.2` в PATH |
| `scripts/doctor.ps1` | 0 |
| `dotnet clean ... -c Release` | 0 |
| `dotnet restore ... --force-evaluate` | 0 |
| `dotnet build ... -c Release --no-restore` | 0; 0 warnings, 0 errors |
| `dotnet test ... --no-build --blame-hang` | 0; 55/55 |
| `dotnet format ... --verify-no-changes` | 0 |
| NuGet vulnerable | 0; уязвимых пакетов не найдено в 7 проектах |
| NuGet deprecated | 0; `xunit 2.9.3` и транзитивные xUnit 2 packages помечены `Legacy` |
| targeted command tests | 0; 10/10 |
| targeted spatial/viewport | 0; 12/12 |
| targeted save/fault | 0; 21/21 |
| targeted real process kill | 0; 2/2 |
| targeted render/export | 0; 10/10 |
| `dotnet-coverage` | 0; 55/55; 85,68% lines, 76,37% branches overall instrumented set |
| узкий Stryker persistence | 0; score 50,56%; 45 killed, 33 survived, 11 NoCoverage, 4 CompileError |
| `dotnet tool restore` | 0; root `dotnet-tools.json` найден и 5 tools восстановлены |
| edge-culling probe | 0 означает дефект воспроизведён: intersects true, visible chunks 0 |

### Прямые packages и лицензии из восстановленных `.nuspec`

- Avalonia 12.1.1, Avalonia.Desktop, Fonts.Inter, Themes.Fluent — MIT;
- SkiaSharp 3.119.4 — MIT;
- coverlet.collector 6.0.4 и Microsoft.NET.Test.Sdk 17.14.1 — MIT;
- xunit 2.9.3 и xunit.runner.visualstudio 3.1.4 — Apache-2.0.

Новых packages сверх среза Opus не добавлено.

## Свежий benchmark и реальные файлы

Отчёт: `artifacts/codex-stage01-final/benchmark/benchmark-report.md`.

- hit-test: median `0,003 мс`, P95 `0,007 мс`, 22,6 кандидата из 10 000;
- geometry cache: 64 chunks, 63 772 triangles, ~2,19 MiB;
- atomic save: median `306,752 мс`, файл `745 841` байт;
- reopen: median `113,364 мс`;
- 4K export: median `1571,188 мс`, `1 353 694` байта;
- CPU-raster frame numbers на текущей загрузке заметно медленнее прежних и не являются GPU gate.

Свежий `benchmark.mapspike`: SHA-256
`FE2932741EFF9A759F6B3B930AB23E0F198B52B47046B475686B3E96BD343253`, 18 ZIP entries,
`format=map-spike`, `schemaVersion=1`, revision 112, manifest checksum fields присутствуют.

Свежий export: SHA-256
`E7E3F1104C534DC2474DBF4DC01532910FA591064D1A1065BD9E2B12100EEF39`; визуально декодирован,
selection/mask edit overlay отсутствуют.

## Настоящее окно и visual evidence

Проверены существующие независимые PNG под `artifacts/audit-native-20260825/`: startup, zoom,
brush, undo/redo, mask overlay on/off, mask disabled, save, export, reopen и benchmark действительно
видны. Renderer line показывает GPU/OpenGL + ANGLE + d3d11 + DirectComposition.

Свежий запуск через targetable window подтвердил:

- настоящий `Map.Spike.App.exe` и GPU/OpenGL;
- 10 000 объектов;
- встроенный benchmark: interval median `13,73 мс`, P95 `15,43 мс`; Skia draw median
  `0,11 мс`, P95 `0,27 мс` при surface 1400×680.

Полный свежий project-owned driver не принят как доказательство: после первого валидного
`2576×1408` startup PNG окно потеряло focus; второй файл получился `160×28`, driver завершился
exit 1. Windows automation затем остановлена, чтобы не перехватывать ввод владельца.

Прежний `03-selection.png` также показывает `Nothing under the cursor`, то есть visual selection
не доказана этим кадром. Fix-prompt требует bounded UIA-проверку non-null selection и полный размер
каждого screenshot.

UI P95 менялся между прогонами (в сохранённом screenshot есть P95 `26,42 мс`, в handoff — другой
прогон `21,22 мс`, свежий targetable window — `15,43 мс`). Наш Skia draw во всех наблюдениях
существенно ниже бюджета; причина внешней вариативности compositor/present/scheduling не разложена
trace-ом и остаётся `PARTIAL`.

## Findings

### [P1] Edge-crossing object пропускается geometry chunk culling

`spike/src/Map.Spike.Rendering/ObjectGeometryCache.cs:90` перебирает только chunk-координаты
viewport, хотя chunks сгруппированы по центрам объектов. Контрпример с центром `(520,256)` и
viewport `[480,511)` имеет пересекающиеся bounds, но `VisibleChunks` возвращает 0. Пользователь
может видеть исчезновение фигур у края при pan/zoom. Требуется fix и red/green regression.

### [P2] Persistence tests не убивают важные мутации read-back/recovery

Узкий Stryker оставил 33 survivors. В частности, удаление вызова `ValidateReadBack` в
`AtomicDocumentStore.cs:101` переживает suite; ветка torn journal не покрыта. Это не доказывает
новый runtime-дефект текущего кода, но ослабляет доказательство критериев 8–9. Fix-prompt требует
corrupt-durable-temp и corrupt-journal tests и повтор узкого Stryker.

### [P2] Selection и полный свежий UI smoke недоказаны

Сохранённый selection screenshot содержит `Nothing under the cursor`; свежий driver получил
невалидный `160×28` PNG после потери focus. Нужен детерминированный UIA-confirmed selection и
проверка одинаковых полных dimensions всех 13 PNG.

### [P3] Документированная reference-команда не работает с `.slnx`

`spike/README.md:23` и `docs/11_CLAUDE_VERIFICATION_PROTOCOL.md:75` предлагают
`dotnet list spike/Map.Spike.slnx reference`; SDK 10 возвращает exit 1, `Проект ... недопустим`.
Фактическая проверка выполнена отдельно для каждого `.csproj`. README разрешён к исправлению Opus;
протокол можно скорректировать отдельным docs-коммитом после Gate D.

### [P3] xUnit 2.9.3 помечен Legacy

Runtime Stage 1 это не блокирует. Upgrade не смешивать с fix; решить при создании production test
foundation в Stage 2.

## FACT / PARTIAL / BLOCKED

### FACT

- .NET 10 + Avalonia 12.1 + Skia запускаются нативно на GPU без WebView/server;
- Core не зависит от Avalonia/Skia;
- 55/55 и targeted suites зелёные;
- command/undo, raster/mask isolation, atomic save, kill recovery, semantic reopen и 4K alpha PNG
  работают в проверенных сценариях;
- числа поддерживают DEC-002; Qt spike не нужен.

### PARTIAL

- внешний UI frame interval вариативен и не разложен trace-ом;
- текущая версия рисует без MSAA;
- raster stroke может выходить за canvas;
- `DuringReplace` fault point проверяет результат до атомарного Win32 replace, а не разрыв внутри;
- self-contained `win-x64` и clean VM вне Stage 1;
- mutation strength и свежий full visual smoke требуют усиления;
- xUnit 2.9.3 Legacy.

### BLOCKED

- Stage 1 не закрыт, пока edge-culling defect не исправлен отдельным коммитом Opus и не пройден
  полный Gate D.

## Точный следующий prompt

`artifacts/opus-prompt-stage01-fix01.md`; точный рабочий base записан внутри prompt после
документационных коммитов проверки.

После отчёта и fix-коммита Opus обязан остановиться. Codex повторяет red/green, Gate A, весь Gate B,
относящийся Gate C, пересчитывает A/M/D/R и только затем создаёт Stage 2 prompt с новым точным base.
