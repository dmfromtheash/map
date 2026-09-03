# STATE HANDOFF

## Актуально на 2026-09-03

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Полезный
production-код Stages 1–15 сохранён. Stages 12A–16 реализованы и проверены: checkpoints,
resize/crop, export, migrations, large-canvas, placement/workspace, stamp parity и финальный
offline P0/P1 workflow.
Отменена только лишняя Stage 11
программа для VM, универсальной чистой Windows, installer/updater, release candidate и
многократной независимой приёмки.

**Stage 12D: migrations и диагностика файла** выполнен по прямому запросу владельца в ветке
`stage/12d-migrations-file-diagnostics`: canonical schema v2, явная copy-only миграция v1→v2,
typed file diagnostics, unknown-object payload, async Dashboard flow и privacy-safe local log.
Исходный v1 никогда не становится save/autosave target и не меняет bytes/timestamp.

Свежая проверка Stage 12D на текущем worktree:

```text
Map.Persistence.Tests --filter FullyQualifiedName~Stage12D: PASS 8/8
Map.Presentation.Tests --filter FullyQualifiedName~Stage12D: PASS 6/6
dotnet build .\Map.slnx -c Release: PASS, 0 warnings, 0 errors
native-stage12d: PASS — real hidden Avalonia/Win32 dashboard + migration prompt; safe Cancel;
validated v2 copy opened clean; source unchanged; corruption/resource/future states distinct;
UI responsive; window closed; unique temporary profile removed
```

По прямому запросу владельца линия **Stage 13A–13D: очень большой холст и 50 000 объектов**
реализована последовательными локальными срезами и принята измеримыми functional/performance/
native gates. Roadmap, clean-room Inkarnate size/performance research, публичные pinned reference
repositories и исполненный master-contract находятся в
`docs/06_DEVELOPMENT_ROADMAP.md`, `docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md`,
`docs/research/external-sources.json` и `docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`. Public canvas
теперь 131 072 × 131 072, 50K — принятый профиль текущего ПК, 100K — только stress/save-open,
262K — зелёный test-only probe без показа в UI и без повышения public policy.

**Stage 16: финальная полнота нативного offline P0/P1** локально реализован в четырёх срезах:
dashboard/maps/assets/settings/composition; Brush/Land/layers/masks/Filters; Line/Shape/Text/Notes;
единый synthetic temporary-profile путь. `.mapproj` writer использует schema v3; v2 мигрируется
только в новую копию через adjacent registry step. Online/Marketplace/Community/P2, release,
installer и поддержка другого ПК сознательно не входят в принятый scope.

## Актуальная продуктовая база

- Рабочий worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Текущая ветка: `codex/stage-16-final-offline-parity`.
- Принятая база Stage 16: `835c94491230e793f9526dfd40dda215ff9c3200` —
  `feat(stage-15): add stamp appearance and clip-mask parity`.
- Последний product-code checkpoint: `e931c42a1495135487a18179b52f05f04faac09c` —
  `feat(assets): add original nature starter samples`.
- Утверждённый визуальный пакет зафиксирован commit
  `81c2d751bfd91c22d6785176cd8cffbbf2546bb1` —
  `feat(ui): apply approved blue cyber-fantasy redesign`.
- Последующие локальные исправления редактора перед визуальным пакетом:
  `72d707c`, `ff7cab5`, `345cdc3`, `f7c375f`.
- Stage 14 завершён `fa8efde`; Stage 15 — `835c944`.
- Последовательные Stage 13 commits:
  `1dfb3fb2864d5badbec6bd89f07c31dc24d00410`,
  `62cd841e53c565acbf023997b336a02ff79e1246`,
  `c2c09aa79182a20d8a7815205f047d643d4c39d0`,
  `3668c49e54a98972569c29cab19d5ddb526fb356`.
- Принятая стартовая база Stage 12C: `45252b944af8a059cc1a51c2dfb562a12385de22` — локальное
  принятие Stage 12B и исполняемый контракт Stage 12C поверх production commit `5863099`.
- Результат Stage 12C записан одним локальным commit
  `feat(stage-12c): add bounded image export and cancellation`; точный hash — текущий `HEAD` этой
  ветки после завершения задачи.
- База Stage 12B до изменения: `750326f967b650b402c67146ab195c2e49c7c4bc` — принятый
  результат `stage/12a-named-checkpoints`.
- Production-результат Stage 12B: `5863099ce7716812b231deb8dcfe496655a9bd56`, один commit
  `feat(stage-12b): add canvas resize and region extraction`; принят владельцем 2026-08-31.
- Консолидированный product/code checkpoint:
  `825d9df9a5fec345c30da009b8b00bb594dd20f1`.
- Основной cleanup commit:
  `889e44897acc8de25cb5f3d3690abe5a60e35fb3`.
- База до уборки:
  `2c0a698e15573badb22a54de7ff1687ab784a9b2`.

Checkpoint `825d9df` является потомком Stage 10E, Stage 10F и полезного Stage 11. Новую функцию
нельзя начинать от `1387b1b`, `2af5bd3` или другого старого checkout.

Сохранено:

- четыре geometry-сетки, включая отдельную `IsometricSquares`, общий snap/render и property sheet;
- локальный системный font catalog и редактируемые keyboard bindings;
- именованные контрольные точки с локальным временем, bounded preview, восстановлением как одной
  undoable-командой и явным удалением;
- изменение canvas от 16 до 131 072 px по каждой стороне с девятью anchor, preview, grid snap,
  object counts и одной undoable-командой;
- безопасный crop/expand: данные за canvas сохраняются, но viewport, thumbnail и общий render
  фактически clip-ятся границей документа;
- точный sparse перенос RGBA, coverage, land и layer masks по материализованным tiles;
- независимая `.mapproj` из прямоугольного региона текущего in-memory snapshot с новым id,
  объектами/группами/compositions/notes/assets и без checkpoint catalog источника;
- заметная панель «Экспорт…» с PNG/JPEG/WebP, whole-map/region, точным output size, связью
  пропорций, transparent/background/grid policy, bounded preview и честной raw-memory estimate;
- фоновый snapshot-safe export с monotonic progress и фактической отменой; операция не меняет
  document/revision/dirty/history/selection/viewport/path/checkpoints/autosave;
- атомарный single-file publish через sibling temp, flush и decode/read-back; cancel/error сохраняет
  прежний destination byte-for-byte;
- package export до 131 072 px: последовательные tiles 2048 px с вычисленным effect bleed,
  deterministic filenames, manifest v1, read-back каждого tile и публикация папки одним rename;
- отдельный production-проект `Map.Export`, использующий общий `SceneRenderer` и собственные
  bounded caches/resolver без UI-owned Skia state;
- обратная совместимость старых `.mapproj`, атомарное хранение snapshot без рекурсивного каталога,
  checksum-цепочка и лимиты 20 точек / 64 MiB;
- иконка приложения;
- изоляция тестового профиля от пользовательских Diagnostics/Recovery/Assets/recent;
- весь более ранний editor, Library, persistence, rendering и UI;
- test sources вне обычного solution для точечных проверок рискованной логики.
- виртуализированный Objects list, fine-grained/coalesced invalidation и spatial hit/marquee;
- chunked object storage, revision lookup и incremental spatial overrides для дешёвого one-object edit;
- Exact/Interactive/Overview LOD с bounded raster chunk cache, exact selected/moving overlay и
  обязательным exact export;
- dynamic fit/min zoom, sparse-only raster traversal и compact `.mapproj` JSON без повышения
  32/64/512 MiB resource budgets.
- Stage 14 canvas-first placement, transient/cancel invariants, primary autosave и workspace;
- Stage 15 точный stamp appearance/clip-mask/foreground и placement series/area;
- Stage 16 dashboard/project/settings/help, safe raster/SVG Library, Brush/Land/layer/mask/filter
  parity, явные vector/text/note workflows, composition packages и schema v3 copy-only migration.

## Что убрано

- исторический spike, benchmarks, диагностические генераторы и UI drivers;
- старые этапные prompts/reviews и generated evidence;
- VM/ISO/QEMU, packaging/release scripts, installer/update заготовки;
- pre-commit, coverage, mutation и лишние tool manifests;
- поздние Stage 10E/10F/11 process-файлы, появившиеся после исходной уборки.

`Map.slnx` содержит только восемь production-проектов из `src`, включая `Map.Export`. Общие build-настройки сохраняют
версию `0.2.0`, ограничивают восстановление текущим `win-x64` и не создают self-contained bundle.
Ненужные нативные PDB Skia/HarfBuzz удаляются из output, управляемые PDB Map остаются.

Свежая проверка Stage 16 и визуального пакета на текущем product-code checkpoint:

```text
Stage16A: PASS — Presentation 6/6; Assets 6/6
Stage16B: PASS — Core 2/2; Presentation 3/3; Rendering 2/2; исправленный узкий regression 1/1
Stage16C: PASS — Presentation 6/6
dotnet build .\Map.slnx -c Release --no-restore: PASS — 0 warnings, 0 errors
native-stage16: PASS — dashboard/assets/edit/save-export/settings/help/responsive;
hidden own-process Avalonia window, ShowActivated=false, ShowInTaskbar=false, no system input;
1416×939 и 960×640; только synthetic temporary profile
```

Full suite, VM, package/release, benchmark, coverage, mutation, security scan и другой ПК не
проверялись: они прямо исключены Stage 16 и правилами владельца.

Свежая проверка Stage 13A–13D на Stage 13 HEAD:

```text
stage13d deterministic profile, seed 13013, warmup 1 + measured 5
PASS — 131K mixed-50K overview P50/P95/max 21.190/22.966/22.966 ms;
normal exact 12.580/13.952/13.952 ms; interaction P95 0.165 ms;
mixed-50K save/reopen P95 1346.963 ms; simple-100K save/reopen P95 1489.276 ms;
262K test-only overview P95 19.363 ms, far sparse selection/export exact, public max unchanged

native-stage13d, настоящее off-screen Avalonia/Skia окно без системного ввода
PASS — overview input-to-render P95 13.956 ms; scene render P95 2.453 ms;
drag P95 11.886 ms; max realized rows 5; pointer list rebuilds 0;
marquee/export/commit/Undo/Redo exact; cache 180 items / 1 095 120 bytes -> 0 after dispose

Stage13D targeted functional tests
PASS — Core 4; Rendering/Stage8/Stage13C/Stage13D 9; Persistence 1; Presentation/export/wizard 3

dotnet build .\Map.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors
```

Свежая проверка Stage 12C до Stage 13:

```text
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release
  --filter "FullyQualifiedName~Stage12C"
PASS — 8 passed, 0 failed, 0 skipped

dotnet build .\Map.slnx -c Release
PASS — 0 warnings, 0 errors

background native UI smoke на текущем Windows ПК
PASS — настоящее скрытое Avalonia/Win32 окно (`ShowActivated=false`, `ShowInTaskbar=false`) без
системного ввода: открыть «Экспорт…» на отдельной vector/raster/mask/asset/grid карте; получить
bounded preview; создать и decode/read-back проверить PNG региона 320×240; запустить multi-tile
операцию, подтвердить отзывчивость UI, отменить, проверить отсутствие опубликованной папки/temp и
неизменность source document/session
```

Полный suite, VM, packaging, benchmark, coverage и повтор зелёных адресных проверок не запускались:
они не нужны для Stage 12C и прямо исключены текущими правилами владельца.

## Другие checkout

`C:\Projects\Map` теперь содержит чистый локальный commit `9729764` — источник исходной уборки, но
не актуальную продуктовую базу. Текущий Stage 16 находится в worktree, указанном выше.

Worktree `fix/app-icon` остаётся owner-dirty и не изменялся. Утверждённая иконка уже находится в
консолидированной продуктовой истории.

## GitHub

- Владелец 2026-09-03 дал новое прямое разрешение сделать все накопленные commits и push текущей
  продуктовой ветки; `codex/stage-16-final-offline-parity` синхронизирована с `origin` до
  product-code checkpoint `e931c42` и следующим за ним handoff commit.
- Это разовое разрешение не открывает будущие PR, issue/comment/close, GitHub Project, auth/scope,
  tag/release и публикацию: для них по-прежнему требуется новое прямое решение владельца.
- Локальные ветки и commits допустимы только в рамках прямо запрошенной локальной работы.
- Подробная замороженная граница записана в `docs/GITHUB_PROJECTS.md`.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему продукту.

## Следующее единственное действие

Использовать реализованный Stage 16 на реальной карте владельца. Нового номерного функционального
этапа нет. Online/P2, release и другой ПК не трогать без нового прямого запроса. Зелёные Stage 16
gates без изменения входов не повторять.
