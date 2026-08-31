# 06. Дорожная карта

## Принцип

Map развивается по пользовательским возможностям владельца. Номер этапа показывает порядок новой
работы, а не возраст подсистемы. Завершённый код предыдущих этапов не откатывается ради старого
названия незакрытой задачи.

Текущая линейная история продукта дошла до Stage 11. Поэтому бывшие отложенные пункты 9B–9E
перенумерованы в Stage 12A–12D. Исторические commit, отчёты и названия 9B–9E остаются в Git, но
новые ветки и задачи используют только номера Stage 12.

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

### Stage 12D — migrations и диагностика файла

- последовательные миграции копии поддерживаемых старых схем;
- исходный файл не перезаписывается до проверки результата;
- corrupted/resource-limit/unsupported-version различаются в понятной локальной диагностике;
- пользователь получает безопасные варианты действия без потери единственной копии.

После 12D новые UX-функции добавляются только по реальным неудобствам владельца, замеченным в его
работе на текущем ПК.

## Проверка каждого среза

- один `dotnet build .\Map.slnx -c Release` после законченного изменения;
- один адресный test-project/filter только для изменённой рискованной логики;
- один короткий smoke в настоящем окне только для видимого сценария;
- без повторения зелёных прогонов, полного suite и больших evidence-папок.

GitHub не участвует в текущем цикле: владелец 2026-08-31 запретил любые remote-действия до нового
отдельного разрешения. Актуальное состояние определяют локальный Git, спецификация и
`STATE_HANDOFF`.
