# 06. Дорожная карта

## Принцип

Map развивается по пользовательским возможностям владельца. Номер этапа показывает порядок новой
работы, а не возраст подсистемы. Завершённый код предыдущих этапов не откатывается ради старого
названия незакрытой задачи.

Текущая линейная история продукта дошла до Stage 16. Поэтому бывшие отложенные пункты 9B–9E
перенумерованы в Stage 12A–12D. Исторические commit, отчёты и названия 9B–9E остаются в Git, но
новые ветки и задачи используют только номера Stage 12.

## Stage 12D — migrations и диагностика файла — `FACT`, реализован

- writer и строгий reader используют canonical `.mapproj` schema v2;
- исторический v1 классифицируется до materialization и мигрируется только в новую проверенную
  копию через registry `vN → vN+1`; source остаётся byte/timestamp-identical;
- ранний untyped Stage 2 v1 получает deterministic BaseColor и явные Object layers;
- unknown fields сохраняются в migration-copy, unknown object остаётся bounded Placeholder и
  переживает обычный save/reopen;
- Dashboard различает migration/future/not-map/corruption/resource/not-found/access/I/O,
  не блокирует UI и добавляет в Recent только destination;
- локальный diagnostics log bounded, без telemetry, абсолютных путей, raw exception/content;
- адресные persistence/presentation tests и один hidden own-process Avalonia/Win32 smoke зелёные.

## Уже реализовано

### Stages 1–8 — основа редактора

- нативное Avalonia/Skia окно, большой canvas, pan/zoom и bounded rendering;
- document model, commands, undo/redo, layers, object tree и masks;
- brush, land, stamp/assets, line, shape, text, notes, grids, filters и compositions;
- `.mapproj`, atomic save/reopen, backup и базовый export/rendering pipeline;
- dashboard, создание/открытие карты и локальная offline-работа.

### Stage 9A — autosave и recovery

- durable autosave snapshot;
- recovery journal и выбор восстановления после сбоя;
- основной `.mapproj` не перезаписывается autosave;
- save/recovery status не выдаёт несохранённое состояние за сохранённое.

### Stage 10A–10F — собственный UX и полнота редактора

- совместимые рабочие shortcuts;
- Library v2, asset sets/variants и детерминированное размещение;
- multi-selection и именованные groups;
- полноценные object inspectors;
- отдельные `Square`, `IsometricSquares`, `HexFlat`, `HexPointy`, общий snap/render и grid property
  sheet;
- локальный каталог системных шрифтов и редактируемые keyboard bindings.

Stage 10E и 10F не являются оставшимися задачами. Они находятся в линейной истории до Stage 11 и
обязаны сохраняться в каждой следующей продуктовой ветке.

### Stage 11 — что сохраняется и что отменено

Линейная история Stage 11 дошла до product HEAD
`2c0a698e15573badb22a54de7ff1687ab784a9b2`. Полезные production-изменения, включая иконку
приложения и изоляцию тестового профиля от реальных пользовательских данных, сохраняются.

Решением владельца отменена только программа распространения и универсальной приёмки:

- clean Windows, VM/Sandbox и другой ПК;
- self-contained/portable package, installer, updater и file association;
- signing, antivirus review, SBOM и release candidate;
- постоянные benchmarks, full-suite, coverage/mutation и отдельные acceptance-проходы.

Отмена этого scope не разрешает начинать новую работу от Stage 10F или другого более старого HEAD.
Процессные Stage 11 файлы удаляются поверх последнего продуктового снимка, а не вместе с полезным
кодом.

## Консолидированный checkpoint

Уборка перенесена поверх позднего Stage 11 product HEAD без потери Stage 10E, Stage 10F, app icon и
test-profile isolation. Stage 12A начат от
`e0010db268b4a2fd4cb9867e1737271be204183a` и завершён в
`750326f967b650b402c67146ab195c2e49c7c4bc`. Stage 12B начат строго от этого commit, завершён в
`5863099ce7716812b231deb8dcfe496655a9bd56` и принят владельцем 2026-08-31.

Старый checkout `C:\Projects\Map` не является базой новой функции. Повторная консолидация не нужна.

## Stage 12 — оставшиеся пользовательские функции

Срезы выполняются последовательно и не смешиваются.

### Stage 12A — именованные checkpoints — реализовано

Пользователь создаёт checkpoint с названием, временем и preview, продолжает эксперимент, сохраняет
и повторно открывает `.mapproj`, а затем безопасно восстанавливает checkpoint. Restore является
одной undoable-операцией: Undo возвращает состояние до восстановления, Redo снова применяет
checkpoint. Данные bounded, сохраняются атомарно и не содержат рекурсивных копий каталога.

Исторический контракт: `docs/STAGE_12A_NAMED_CHECKPOINTS.md`.

Принятые границы: до 20 точек и 64 MiB общего сохранённого payload, preview 320 × 180 до 1 MiB,
без auto-eviction. Проверены адресные тесты (6/6), Release build (0 warnings, 0 errors) и один
фоновый нативный UI-smoke в настоящем off-screen Avalonia/Win32 окне без системного ввода.

### Stage 12B — resize, crop и новая карта из региона — принято

- единый целочисленный Core-контракт для девяти anchor, crop/expand и настоящего snap через
  геометрию активной square/isometric/hex grid;
- производный preview старой/новой границы, object counts и одна команда Undo/Redo без изменения
  документа, history, dirty state или файла до Apply;
- безопасный crop: внешние vector/raster/mask данные остаются в исходном документе, а viewport,
  preview и thumbnail ограничены фактической границей canvas;
- sparse перенос RGBA, coverage, land и masks только по материализованным tiles, включая переход
  через tile boundaries без full-canvas allocation;
- отдельная `.mapproj` из региона текущего in-memory snapshot: новый `documentId`, переведённые
  объекты/группы/compositions/notes/grid и достаточное asset closure, без checkpoints источника;
- исходная карта, её path/history/dirty/checkpoints и прежний корректный destination сохраняются
  при cancel или ошибке записи.

Проверены адресный `Stage12B` filter (16/16), Release build (0 warnings, 0 errors) и один фоновый
нативный UI smoke в настоящих скрытых Avalonia/Win32 окнах без системного ввода: preview/cancel,
crop, clip, Undo/Redo, save/reopen и повторное открытие независимой региональной карты с raster и
mask.

### Stage 12C — полноценный export и отмена — реализовано

- заметная панель PNG/JPEG/WebP export для всей карты или точного региона;
- output size со связью/разрывом aspect ratio, прозрачный/фактический фон, grid master switch и
  `IncludeInExport` независимо от editor visibility;
- bounded preview до 960×540, checked raw-memory estimate, snapshot-safe background progress и
  фактическая отмена;
- single image до 16 384 px с sibling temp, flush, decode/read-back и атомарной заменой;
- 16K+ package path до 65 536 px: tiles 2048 px с effect bleed, manifest v1 и atomic folder rename
  без full-canvas allocation;
- ошибка/cancel сохраняет карту и существующий destination; успех не меняет session/history/path.

Исторический исполняемый контракт: `docs/STAGE_12C_FULL_EXPORT.md`. Проверены адресный `Stage12C`
filter (8/8), Release build (0 warnings, 0 errors) и один скрытый native UI smoke: bounded preview,
PNG региона 320×240, responsive multi-tile cancel, cleanup и неизменность source document/session.

### Stage 12D — migrations и диагностика файла — `FACT`, реализован

- последовательные миграции копии поддерживаемых старых схем;
- исходный файл не перезаписывается до проверки результата;
- corrupted/resource-limit/unsupported-version различаются в понятной локальной диагностике;
- пользователь получает безопасные варианты действия без потери единственной копии.

Stage 12D завершён commit `68a8982`: canonical schema v2, copy-only v1→v2 migration, typed file
diagnostics и bounded privacy-safe local log. Source старой схемы не изменяется.

## Stage 13 — очень большой холст и 50 000 объектов — `FACT`, реализован

Владелец 31.08.2026 прямо запросил отдельную программу оптимизации холста и объектов со
стресс-проверками. Stage 13A–13D выполнены четырьмя последовательными локальными commits от
принятого Stage 12C HEAD. В commit-графе Stage 13 предшествует отдельно завершённому Stage 12D;
актуальная линейная история содержит оба результата.

Наблюдаемая граница платного Inkarnate на дату исследования: editor 1K–4K, export до 16K и
custom grid 2..256 по каждой стороне. Map не копирует его реализацию, код, ассеты или visual
language. Независимая цель Map — нативный offline sparse canvas, размер которого не равен размеру
единого bitmap, и измеримо отзывчивое редактирование 50 000 логических объектов.

Плановые продуктовые границы имеют разные статусы:

| Граница | Статус до Stage 13 | Условие принятия |
|---|---|---|
| 65 536 × 65 536 и 50 000 объектов | `FACT` | покрывается более высокой принятой границей 131K |
| 100 000 объектов | `FACT` только stress/save-open | открывается, индексируется и save/reopen проходит; обещания равной UI-плавности нет |
| 131 072 × 131 072 и 50 000 объектов | `FACT` public supported | functional, CPU и native gates зелёные на текущем ПК |
| 262 144 × 262 144 | `FACT` test-only, не public | sparse math/selection/export/overview зелёные; UI/validator остаются 131K до отдельного решения |

### Stage 13A — `FACT`: измеримость, виртуальный список и интерактивный поиск

- один компактный performance runner с deterministic fixtures, warmup, P50/P95/max, allocations,
  machine/branch/HEAD fingerprint и JSON вне production solution;
- отдельные профили 10K/50K/100K, small viewport/full overview, simple/mixed scene, object list,
  hit-test, marquee, selection/drag и cold/warm spatial index;
- виртуализированный Objects list: количество Avalonia controls зависит от видимых строк и
  overscan, а не от полного числа объектов;
- события document structure, selection, viewport и canvas invalidation разделены; pointer move
  не перестраивает боковые панели, invalidation объединяется до одного запроса на кадр;
- hit-test и marquee используют spatial broad phase с точной проверкой только кандидатов.

### Stage 13B — `FACT`: дешёвое изменение одного объекта

- производные индексы `ObjectId -> object/layer/index` и `GroupId -> members` привязаны к immutable
  revision и не попадают в `.mapproj`;
- изменение одного объекта не копирует и не валидирует скрытно все 50 000 объектов;
- object storage получает измеренную chunked/persistent форму со structural sharing либо другой
  доказанно более дешёвый эквивалент без нарушения immutable source of truth и Undo/Redo;
- spatial index обновляет только затронутые cells/chunks, сохраняя стабильный draw order.

### Stage 13C — `FACT`: LOD, batching и chunk render cache

- viewport получает screen-space LOD; export и близкий zoom сохраняют полный quality;
- неизменившиеся spatial chunks могут хранить bounded `SKPicture`, atlas batch или иной измеренно
  лучший Skia primitive; движущийся объект рисуется отдельным dynamic overlay;
- повторяющиеся stamps объединяются через существующий SkiaSharp `DrawAtlas`/совместимый batch
  только после сравнительного benchmark;
- субпиксельные детали, текст, тени и дорогие эффекты получают честный overview policy;
- compositions и сложные paths получают собственный culling/cache, а изменение одного chunk не
  сбрасывает весь scene cache.

### Stage 13D — `FACT`: расширение логического размера после зелёных ворот

- сначала поднять поддерживаемый предел до 131 072 × 131 072 и согласовать dynamic fit/min zoom;
- сохранить sparse raster, checked coordinate arithmetic и отсутствие full-canvas allocation;
- расширить tile-package export до нового поддерживаемого размера, не повышая single-image 16K;
- 262 144 × 262 144 проверить только как разреженный test fixture; не показывать в обычном UI как
  поддерживаемый максимум без отдельного принятия владельцем;
- dense raster полного огромного холста не является поддерживаемым сценарием: бюджеты относятся к
  материализованным tiles, assets, objects и caches, а не к теоретической площади.

### Обязательный результат Stage 13

- 50 000 объектов — основной поддерживаемый профиль, включая настоящий native UI;
- 100 000 — stress/read/save-open профиль, пока без обещания одинаковой плавности;
- normal zoom стремится к 60 FPS, full overview 50K обязан удерживать не хуже 30 FPS на текущем ПК;
- pointer/drag не выполняют работу, пропорциональную всем объектам, и не создают 50 000 UI controls;
- caches имеют явные byte/item budgets, cancel/dispose и revision-local invalidation;
- регрессия результата render/export, Undo/Redo, save/reopen и selection недопустима.

Полный исполняемый контракт находится в `docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`. Фактический
результат зафиксирован commits `1dfb3fb`, `62cd841`, `c2c09aa`, `3668c49`; численные текущие gates
и ограничения записаны в `docs/STATE_HANDOFF.md`.

## Stages 14–15 — размещение, workspace и stamp parity — `FACT`, реализованы

- Stage 14: canvas-first placement с transient ghost и одной commit-командой, primary autosave
  после первого явного Save, версии и упрощённый workspace «Слои / Объекты»;
- Stage 15: компактный stamp-каталог, точные appearance controls, clip-mask, foreground surface,
  series/area placement, Undo/Redo и save/reopen/export parity;
- принятые commits: Stage 14 заканчивается `fa8efde`, Stage 15 — `835c944`.

## Stage 16 — финальная полнота нативного offline P0/P1 — `FACT`, локально реализован

- 16A: dashboard search/sort, независимые clone/template, безопасный raster/SVG import, missing
  asset repair/remove, starter pack, локальные settings/help и composition packages;
- 16B: Brush material/presets и preview=commit, точный Land, layer opacity/blend/effects,
  независимые masks, sublayers и Filters; `.mapproj` schema v3 и copy-only v2→v3 migration;
- 16C: пользовательские Line/Shape gestures, явный node/portal/boolean selection, texture fill,
  Text placement с inheritance gate и безопасный Markdown Notes preview;
- 16D: связный synthetic temporary-profile сценарий, save/reopen/export, settings restart и
  hidden own-process UI на 1416×939 и 960×640 без системного ввода;
- адресные Stage16A/16B/16C tests, Release build и combined native smoke зелёные;
- Stage 16, последующие исправления редактора, утверждённый визуальный пакет и оригинальные
  тестовые nature assets зафиксированы в `codex/stage-16-final-offline-parity`; последний
  product-code checkpoint — `e931c42`.

Stage 16 завершает принятый нативный offline P0/P1 scope. Marketplace, Community, account/cloud,
совместное редактирование и другие online/P2 возможности не являются дефектами этой версии и не
начинаются без нового прямого решения владельца.

## Проверка каждого среза

- один `dotnet build .\Map.slnx -c Release` после законченного изменения;
- один адресный test-project/filter только для изменённой рискованной логики;
- один короткий smoke в настоящем окне только для видимого сценария;
- без повторения зелёных прогонов, полного suite и больших evidence-папок.

Владелец 2026-09-03 дал отдельное разовое разрешение зафиксировать и отправить накопленный
результат текущей продуктовой ветки. После исполнения этой синхронизации для новых remote-действий
снова требуется прямое разрешение. Актуальное состояние определяют Git, спецификация и
`STATE_HANDOFF`.
