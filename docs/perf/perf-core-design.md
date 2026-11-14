# Ядро производительности Map — инвентарь и проект

Источник кода: ветка `claude/fps-pack-2` (git show / git grep), только чтение. Числа — константы в коде (FACT).
Main-рабочая копия имеет чужое незакоммиченное изменение `src/Map.Rendering/Styles/StyleRenderer.Painter.cs` — не трогалось.

## 1. Инвентарь существующих механизмов

| # | Механизм (файл) | Что решает | Свои пороги / бюджеты | Поток | Сигнал наружу | Пересечения / конфликты |
|---|---|---|---|---|---|---|
| 1 | Выбор LOD-тира (`MapCanvasControl.cs` ~851, `RenderLodTier` Exact/Interactive/Overview) | Тир кадра по масштабу и жесту | scale < 0.125 → Overview; Performance-режим < 0.25; Interactive при fastPreview/живом превью | render (ICustomDrawOperation) | `RenderOptions.LodTier`, `LastLodTier` | Решение статическое (по масштабу), не по измеренной стоимости. Ручной `CanvasPerformanceMode` — по сути ручной governor |
| 2 | `CameraFrameCache` | Во время движения камеры рисует готовый GPU-кадр вместо сцены | 128 MiB, кадр > budget/2 отвергается | render | `CameraFramesReused`, `CapturesRejectedByBudget`; ставит `DetailTilesPending=1` как флаг «не точный» | Свой лимит памяти вне WorkingMemoryBudget; «pending» используется не по смыслу |
| 3 | Камера-settle (`CameraSettle` 120 мс + таймер 135 мс, Render priority) | Когда жест «закончился» → точный кадр | 120 мс | UI | `QueueVisualInvalidation` | Отдельное определение «покоя» от ambient (`AmbientRest`) и от journal `PerformanceGesture` |
| 4 | Детальные тайлы объектов (SceneRenderer ~3535, R05) | Сколько точных тайлов строить в кадре | `DetailTileBudgetMilliseconds = 8` | render | `DetailTilesPending++`, `RequestRefinedFrame` | Бюджет 8 мс складывается с №5, №6, №9 — без общего дедлайна |
| 5 | `ObjectOverviewCache` | Сборка/приём обзорных чанков | build 4 мс/кадр, приём ≤ 8 MiB пикс./кадр, 1024 элементов, 64 MiB; pressure-trim ≤ 2 мс | render | `CanBuildOverview`, `CanAcceptPreparedOverview`, `PressureTrims` | Зарегистрирован в WorkingMemoryBudget (Expensive) — хорошо; время — своё |
| 6 | `SceneRenderer.OverviewPreparation` | Фоновый план/палитра/варианты/растр обзора, префетч соседей | QueueLimit 64, HandoffLimit 16, LegacyRetry 250 мс; FIFO LinkedList | 1 фоновый `Task.Run(Pump)` | `ready` → RequestRefinedFrame | Свой приоритет (FIFO + Prefetch-флаг), своя очередь, своя отмена (Token на слой) |
| 7 | `CatalogStampImageSource` (BeginFrame/Lease) | Приём готовых mip-ов, вытеснение, отмена ненужных | приём 2 мс/кадр, 128 MiB декод., 16 уровней | render (BeginFrame), фон | `_derivedReady`, `ImageRequestsPending`, `UnresolvedImages` | Ещё один независимый мс-бюджет в том же кадре |
| 8 | `StampDerivedAssetStore` (+Portable, `DerivedCacheJanitor`) | Диск-кэш производных mip, header-only, тихие промахи | `_mipSlots = 8`, диск 2 GiB, portable 16 MiB, retry-backoff, janitor 25 мс шаг | ThreadPool + janitor | `ReadyPixels`, `TakeInvalidated` | 8 слотов конкурируют с №6, №10, №13 за ядра без общего лимита |
| 9 | `HexLayerRenderer` | Сборка гекс-чанков | `BuildBudgetMilliseconds = 8`; HexChunkCache 192 MiB + 64 MiB блоки + 48 MiB переходы | render | `HexChunksPending` | Свои лимиты памяти вне WorkingMemoryBudget; третий 8-мс бюджет |
| 10 | `TerrainRefinement` | Одна подготовка рельефа + одна ожидающая | 1 в работе, 1 в очереди | фон | `TerrainRefinementsPending`, `FastTerrain` | Своя мини-очередь |
| 11 | Style kept-frame / `StyleRenderer.ViewportReuse` | Переиспользует растр стиля, пока вид внутри поля | гуттер; zoom-reuse временный | render | — | Своё правило «можно ли неточно» |
| 12 | Lighting kept frame (`LightingRenderer.Follows`) | Следование светового буфера за камерой | R09: ≤ 4 мс p95 (цель в доке, не в коде) | render | `LightingFrameSample` | Своё правило неточности; LOD≠Exact → нет света |
| 13 | Растровые кэши: `TileImageCache`, `LayerImageCache`, `StrokePreviewCache`, `CarvedLandCache`, `RenderImageCache`, `CoarseCoverage` (256 шт/64 MiB), `RiverBankRenderer` (16) | Повторное использование пикселей по идентичности | у каждого свой | render | hits/misses в RenderStats | Большинство не зарегистрированы в WorkingMemoryBudget (только AssetImageCache и ObjectOverviewCache) |
| 14 | `WorkingMemoryBudget` (+Caches) | Допуск тяжёлых операций, бюджет кэшей, давление | Edit 16 MiB, Heavy 128, Small 256, bypass 64; давление 50 % ± 5 % (гистерезис!) | любой | `CachesUnderPressure`, `SetLimit`, `EnterHeavyAsync` | Единственный общий механизм — но только для памяти и части кэшей |
| 15 | `DocumentDerivedIndexes` | Индексы (identity, spatial, bound, names) после открытия в фоне | — | фон | `IsPrepared` | Нет приоритета относительно №6/№8 |
| 16 | `ObjectLayerChanges` | Дельты слоёв → индексы обновляются O(изменений) | — | любой | `TryGet(delta)` | База для O(N)-сторожей |
| 17 | `BackgroundGeneration` (DEC-099) | Снимок → фоновый расчёт → один Undo, конфликт по ревизии | — | фон | `GenerationApplyResult` | Готовый шаблон для «ввод не ждёт», но используется только генерацией/путями |
| 18 | Экспорт (`ImageExportEngine`, `PngTilePyramid`, `MbTilesWriter`) | Параллельная запись с допуском по памяти | `EnterHeavyAsync`, MaxDegreeOfParallelism, кэш 256 MiB, PngViewCache 2 GiB | ThreadPool | прогресс | Parallel.For может занять все ядра, пока UI-рендер ждёт фоновые работы |
| 19 | Ambient-кадры (41 мс таймер, Render priority) | Анимация огня/гекса | 41 мс | UI | `requestAmbientFrame` | Отдельный источник кадров |
| 20 | `PerformanceJournal` | Окна кадров, p-перцентили, 1 % low, очереди, память | 16384 сэмплов, 20 MiB, 7 файлов | render→фон | файлы perf-*.jsonl | Есть gesture только Idle/Pan/Zoom/Edit, нет функции/инструмента и нет CPU/GPU-раздела |
| 21 | GC: `SustainedLowLatency` (Program.cs:34) | Нет блокирующих gen2 | — | процесс | — | Хорошо; аллокации в кадре не контролируются |
| 22 | GPU flush замер (`MeasureGpuFlush`: Flush + Submit(sync)) | Время GPU кадра | только диагностика | render | `LastFrameGpuFlushMilliseconds` | Это готовый датчик GPU/CPU — сейчас только в perf-режиме |

Главный конфликт: в одном кадре независимо тратятся 2 мс (№7) + 4 мс (№5) + 8 мс (№4) + 8 мс (№9) + 2 мс (trim) = до 24 мс при бюджете 16.7 (или 6.9 на 144 Гц). Ни один не знает о других и о том, сколько уже съела отрисовка сцены. Фоновые очереди №6, №8 (8 слотов), №10, №15, №18 конкурируют за ядра 4-ядерного i5/i7 без общего приоритета. «Неточно, дорисуй» выражено пятью разными pending-полями + камерным хаком.

## 2. Целевое ядро (одно, из существующих частей)

Пять общих служб; существующие механизмы становятся их клиентами.

1. **FrameBudget** (новое, маленькое) — один дедлайн кадра. `Begin(refreshInterval)`; каждый клиент спрашивает `TryTake(kind, estimateMs)`; оценки — EWMA реальных замеров клиента.
2. **WorkScheduler** (обобщение OverviewPreparation.Pump) — фоновые задачи с приоритетом `VisibleNow / Soon / Eventually` и ключом отмены по виду/ревизии; общий лимит параллелизма = ядра − 1 (одно ядро всегда UI+render).
3. **WorkingMemoryBudget** (есть) — все кэши через `RegisterCache` (Camera, Hex, Coarse, Tile, Layer…).
4. **Refinement** — одно поле `FrameCompleteness {Exact, Pending(reasons)}` вместо пяти флагов; одна точка `RequestRefinedFrame`.
5. **QualityGovernor** (новое) + **PerformanceJournal** (есть, расширяется полями функции, CPU/GPU, решений governor).

Замены констант: 2/4/8/8/2 мс → доли общего бюджета (`FrameBudget` раздаёт остаток после сцены по приоритету: видимые тайлы > приём mip > обзор > hex-префетч > trim). Лимиты 128/192/64/48 MiB → `requestedBytes` в RegisterCache, реальный лимит выдаёт `Rebalance`. `_mipSlots=8`, QueueLimit 64 → общий лимит WorkScheduler. Пороги тира 0.125/0.25 → стартовые значения уровня governor.

## 3. Семейства функций → какие части ядра их защищают

(perf-functions.md второго агента на момент записи не существовал; список семейств взят из поручения координатора — сверить, когда файл появится.)
FB = FrameBudget, WS = WorkScheduler, MEM = WorkingMemoryBudget, GOV = QualityGovernor, REF = единый Refinement, BG = BackgroundGeneration, J = журнал, O(N) = сторож.

| Семейство | Части ядра |
|---|---|
| Навигация (pan/zoom) | CameraFrameCache под MEM; GOV (тир, детальные тайлы); FB (приём mip/обзор); REF после settle; J |
| Реки, дороги (жесты, расчёт берегов/развязок) | превью через LayerImageCache/overrides в FB; тяжёлый пересчёт → BG + WS(VisibleNow); O(N) по ObjectLayerChanges |
| Суша/уровни, маски, кисти | StrokePreviewCache + TileImageCache под MEM; FB на выгрузку тайлов; CarvedLand под MEM; GOV (Interactive-тир) |
| Слои, объекты, штампы (размещение, выделение, перемещение) | DocumentDerivedIndexes в WS(Soon после открытия); O(N) на клик/перемещение; ввод-не-ждёт (>8 мс → BG) |
| Фильтры/стили | kept-frame ViewportReuse; GOV-ручка StyleScale (GPU-bound); точный кадр после settle |
| Освещение | Lighting Follows; GOV-ручки Shadows/свет (GPU-bound); цель R09 ≤ 4 мс p95 в J |
| Генерация (все виды) | BG (снимок → фон → один Undo) через WS(Soon); превью-ступени в FB |
| Текст/подписи | GOV-ручка Labels (CPU-bound); кэш шрифтов под MEM |
| Гексы | HexLayer build в FB (вместо своих 8 мс); HexChunkCache под MEM; ambient-кадры считаются в J |
| Экспорт и его превью | forceFull-режим (без FB/GOV, как MapLibre forceFullPlacement); EnterHeavyAsync в MEM; слоты WS(Eventually во время жеста) |
| Каталог, новые ассеты | StampDerivedAssetStore/mip через WS (вместо 8 своих слотов); приём в FB; janitor — WS(Eventually) |

Защита новой функции по умолчанию: (а) любая отрисовка идёт через SceneRenderer → автоматически внутри FrameBudget и журнала (раздел по `RenderStats`); (б) команды редактора исполняются через одну точку `EditingSession.Execute` → обёртка меряет время; > 8 мс → журнал помечает команду и тест-сторож падает, пока команда не переведена на `BackgroundGeneration`; (в) кэш без `RegisterCache` не проходит архитектурный тест (рефлексия: все `*Cache` типы в Map.Rendering имеют account); (г) тест-сторожа O(N) — счётчик посещённых объектов в `RenderStats`/индексах, правило «посещено ≤ k·(видимых+изменённых)» на 10k и 1M объектов.
