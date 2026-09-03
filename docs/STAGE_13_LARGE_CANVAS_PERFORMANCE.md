# Исполняемый prompt — Stage 13A–13D: 50 000 объектов и очень большой холст

Выполни этот prompt целиком как локальную программу оптимизации Map, но работай четырьмя
последовательными проверяемыми срезами. Не заменяй реализацию новым планом или отчётом. Не начинай
следующий срез, если предыдущий не доказал корректность и измеримое улучшение.

## Статус и полномочия

Этот prompt подготовлен 31.08.2026 по прямому запросу владельца, но сам факт его наличия не
означает старт или завершение Stage 13. Выполнение начинается только когда владелец отдельно говорит
выполнить этот prompt.

Будущая команда на выполнение даёт право:

- изменить production-код Map в пределах Stage 13A–13D;
- создать один локальный stage branch от последнего принятого product HEAD;
- добавить компактный dev-only performance runner и адресные Stage 13 tests вне production
  `Map.slnx`;
- выполнить прямо запрошенные benchmark/stress-профили на текущем ПК владельца;
- создать до четырёх последовательных локальных commits после зелёных ворот каждого среза.

Она не даёт право делать push/fetch/pull, читать или менять GitHub issue/Project/PR, создавать
release/tag, работать с VM/другим ПК, добавлять installer/updater, запускать full suite, coverage,
mutation, security scan или менять scope на универсальную поставку.

## Практический результат

После Stage 13 владелец должен получить:

1. плавный обычный pan/zoom/select/drag на документе с 50 000 объектов;
2. полный обзор 50 000 логических объектов с viewport LOD не хуже 30 FPS на текущем ПК;
3. Objects panel, который не создаёт 50 000 Avalonia controls и не перестраивается на pointer move;
4. hit-test/marquee/lookup через spatial и ID indexes, а не полный обход сцены;
5. изменение одного объекта без копирования и полной валидации всех 50 000;
6. bounded chunk render cache, LOD и batching повторяющихся stamps без потери точного close zoom и
   export;
7. поддерживаемый sparse canvas до 131 072 × 131 072 только после зелёных ворот;
8. test-only sparse probe 262 144 × 262 144 без объявления его публичным максимумом;
9. 100 000 объектов как стресс/file/read/save-open профиль, но не как автоматическое обещание
   такой же плавности;
10. повторяемый локальный performance runner, который различает renderer time, UI latency,
    allocations, candidate/draw/realized-row counts и cache state.

## Обязательный вход перед изменениями

Работай только в актуальном worktree из `docs/STATE_HANDOFF.md`. На дату подготовки это:

```text
C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map
```

Но не доверяй дате prompt как текущей истине. Сначала прочитай полностью:

- `AGENTS.md`;
- `CODEX.md`;
- `docs/STATE_HANDOFF.md`;
- относящиеся разделы `docs/DECISIONS.md`, особенно DEC-001/002/003/009/015/016/017;
- `docs/04_TECH_ARCHITECTURE.md`;
- `docs/06_DEVELOPMENT_ROADMAP.md`;
- `docs/08_QA_AND_ACCEPTANCE.md`;
- `docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md`;
- `docs/research/SOURCES.md` и `docs/research/external-sources.json`.

Затем зафиксируй одним read-only проходом:

```powershell
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short
git log --all --oneline -20
git remote -v
```

Сопоставь текущий HEAD с `Последним продуктовым HEAD` handoff. На дату подготовки Stage 12C
production HEAD — `878fdedf314e70912ae5326776b16347fb8470a1`, но если handoff позже указывает принятый
потомок, используй потомка. Не начинай от `C:\Projects\Map`, старого cleanup checkout, `main` или
случайной удобной ветки.

Сохрани все существующие изменения владельца. Запрещены `reset`, `restore`, `stash`, rebase,
amend, checkout чужих файлов и переписывание истории. Документационные изменения подготовки
Stage 13 могут быть owner-dirty; не присваивай и не удаляй их. Если обнаружены неизвестные
production changes, пересекающиеся с Stage 13, остановись с `BLOCKED` и точным списком файлов.

После проверки создай локальную ветку:

```text
stage/13-large-canvas-performance
```

строго от последнего принятого product HEAD. Если ветка уже существует, сначала докажи её base,
HEAD и status; не создавай дубликат и не переписывай историю.

## Инварианты, которые нельзя нарушать

- Map остаётся нативным offline Windows x64 приложением без WebView, локального server или cloud.
- `MapDocument` остаётся immutable source of truth.
- Renderer не меняет document; UI/session state не становится сохранённой сценой без отдельного
  решения.
- Каждое завершённое редактирование остаётся одной undoable command; Undo/Redo точны.
- Сохранение остаётся атомарным, bounded и совместимым с текущими `.mapproj`.
- Core не зависит от Avalonia, Skia, Presentation или внешнего renderer.
- Viewport, preview и export используют одну семантику scene evaluation; editor chrome не попадает
  в export.
- Sparse raster `TileMap.TileSize=256` и structural sharing не заменяются full-canvas bitmap.
- Close zoom и export не теряют геометрию/текст/effects из-за LOD.
- Missing assets остаются честными placeholders.
- Нельзя повысить public limit, если save/reopen, viewport, region, thumbnail и tile export не
  согласованы с ним.
- Новая зависимость не добавляется только потому, что она известна или рекомендована LLM.

## Clean-room граница Inkarnate

Наблюдаемая конкурентная граница зафиксирована в research:

- editor quality 1K/2K/3K/4K;
- custom grid 2..256 columns/rows;
- платный export menu до 16K;
- конкретная 2K test map использовала два canvas 2048×1536 и отдельный fixed UI;
- производительность при 50K не была безопасно измерена и не является FACT.

Не заходи снова в платный аккаунт для реализации, не создавай там stress map и не извлекай
внутренности. Не открывай network/storage/bundles/DevTools и не копируй code, assets, UI graphics,
branding, trade dress или закрытые данные. Используй только собственные synthetic fixtures и
публичные clean-room источники.

«Лучше Inkarnate» означает только измеримые свойства Map: native offline, логический sparse canvas
значительно больше 4K, точный 16K+/tile export, 50K editable acceptance и доказанная latency. Не
пиши рекламное утверждение, что Map быстрее Inkarnate, пока нет одинакового публично
воспроизводимого cross-product benchmark.

## Внешние репозитории и найденный код

Используй `docs/research/external-sources.json` как allowlist/pin для isolated research. Не клонируй
все проекты. Открывай только минимальный источник, если конкретное решение невозможно уверенно
оценить по текущему коду и официальной документации.

Приоритет:

1. текущий production-код Map и его Git history;
2. исторический Map spike/benchmarks из commits `2c0a698`/`e821c45` только как источник идей;
3. Avalonia core `VirtualizingStackPanel` и tests;
4. уже используемый SkiaSharp: `SKPictureRecorder`, `DrawAtlas`, `SKVertices`;
5. Mapsui как MIT reference для viewport/vector/raster cache и benchmark patterns;
6. NetTopologySuite STRtree/quadtree только как isolated comparison;
7. BenchmarkDotNet как dev-only candidate;
8. Godot batching/MultiMesh только как concept cross-check.

Для каждого фактически переносимого фрагмента:

- подтвердить upstream URL, exact pinned commit, license и notice;
- убедиться, что файл действительно находится под указанной лицензией;
- проверить API/version и тесты upstream;
- записать происхождение в `docs/research/SOURCES.md` или существующий licensing registry;
- не переносить большой код, если clean-room собственная реализация механизма проще;
- отклонить код без первичного источника или с неясной лицензией;
- рекомендацию LLM считать `PROPOSED` до локального benchmark и review текущих инвариантов.

Не добавляй Mapsui, Godot или NetTopologySuite как production dependency без сравнительного probe,
где они измеримо лучше собственного маленького решения по query, update, allocation, memory и
сложности. Avalonia и SkiaSharp уже входят в stack; сначала используй их существующие возможности.

## HISTORICAL baseline, который надо перепроверить

На snapshot `878fded` временный current-source audit на текущем ПК показал для простых объектов:

| Профиль | 50K | 100K |
|---|---:|---:|
| small viewport CPU render median | 2,662 ms | 3,955 ms |
| full overview CPU render median | 47,072 ms | 83,665 ms |
| cold spatial index | 23,311 ms | 27,226 ms |
| empty-point hit-test median | 3,051 ms | 5,052 ms |
| object-tree model median | 41,488 ms / 15,85 MiB alloc | 100,647 ms / 31,7 MiB alloc |
| one-object edit median | 18,745 ms / 6,72 MiB alloc | 53,604 ms / 13,75 MiB alloc |

Это `HISTORICAL`, а не текущая acceptance. Audit не создавал 50K настоящих Avalonia controls и не
измерял native input-to-present. В начале Stage 13 создай повторяемый runner и сними новый baseline
на фактическом base HEAD. Если результаты расходятся, сохрани оба с fingerprint и объясни причину;
не подгоняй fixture под старые числа.

## Stage 13A — baseline, виртуализация и интерактивный spatial path

### A1. Компактный performance runner

Добавь один dev-only проект вне production `Map.slnx`, например `tools/Map.Performance`, либо
докажи более подходящее существующему layout место. Он может использовать BenchmarkDotNet только
как dev dependency; если встроенный deterministic runner даёт более прозрачный native/scenario
контракт, не добавляй пакет ради названия.

Runner обязан:

- работать Release x64 на текущем runtime;
- принимать seed/profile/object-count/iterations/output path;
- выполнять warmup отдельно от measured iterations;
- сохранять P50/P95/max, allocations, Gen0/1/2, working set before/after;
- записывать root, branch, HEAD, dirty status, runtime, OS, CPU count, renderer/backend и fixture
  hash;
- считать total objects, visible candidates, exact drawn objects, draw calls/cache hits/misses,
  realized UI rows и event counters там, где они применимы;
- экспортировать machine-readable JSON во временную/ignored папку;
- возвращать non-zero только по крупным зафиксированным gates, а не из-за микрошумов;
- не входить в production solution и не запускаться обычной сборкой приложения.

Generated results не коммить. В репозитории остаются runner source, fixture contract и короткая
инструкция запуска.

### A2. Детерминированные fixtures

Минимальные profiles:

- `simple-10k`, `simple-50k`, `simple-100k`: rectangles/stamps равномерно по canvas;
- `mixed-50k`: зафиксированная доля stamps, text, lines, shapes, groups/compositions, sublayers,
  masks и дорогих effects; точные counts записать в fixture manifest;
- `dense-50k`: все bounds попадают в full overview и плотный участок;
- `sparse-raster`: несколько materialized raster/land/mask tiles в далёких координатах;
- `ui-tree-50k`: groups/search/filter/selection без 50K созданных controls;
- `edit-50k`: hit-test, select, drag preview, one-object commit, Undo, Redo, marquee;
- `persistence-50k` и `persistence-100k`: synthetic assets-free save/reopen с semantic equality;
- `export-large-sparse`: region/tile package без giant allocation.

Fixture не использует пользовательские карты, каталоги, пути или ассеты. Seed фиксирован, z-order
и candidate counts детерминированы.

### A3. Виртуальный Objects list

Текущий `ObjectTreeRows.Build` и `EditorView.SyncObjectList` не должны создавать модель/control для
каждого объекта при обычном показе панели.

- Перейди на `ItemsSource` и `VirtualizingStackPanel` либо измеренно эквивалентный Avalonia
  механизм без ручного full `StackPanel.Children`.
- Сохрани search, type filters, groups, breadcrumb/open group, selection, hide/lock и keyboard/UIA.
- Не вычисляй `GroupMembers` повторным полным обходом для каждой строки; используй derived group
  index.
- Scrolling создаёт только visible+overscan containers и переиспользует их корректно.
- Selection update меняет состояние реализованных строк, а не создаёт новый список 50K.
- Search может использовать background/indexed filter с cancellation, но публикация результата
  выполняется только для актуального query/revision.

### A4. Разделить события и coalesce invalidation

Не используй один `SceneChanged` как повод обновить status, весь inspector, весь Objects list и
canvas на каждом pointer move.

Введи минимальную понятную модель change reasons, сохраняя текущий стиль:

- document structure;
- object properties/geometry;
- selection/session;
- viewport;
- canvas invalidation;
- catalog/side-panel data только при фактическом изменении.

Несколько canvas invalidations до render tick объединяются. Drag/brush preview обновляют canvas и
нужный inspector, но не перестраивают object tree. Не добавляй System.Reactive только ради
throttle; достаточно lifecycle/Dispatcher механизма Avalonia, если он проходит тест.

### A5. Hit-test и marquee через spatial broad phase

- Расширь текущий `ObjectSpatialIndex` point/small-envelope query или адаптируй clean-room идею
  исторического `UniformGridIndex.QueryPoint`.
- Сохрани точный reverse draw-order и sublayer semantics.
- Point query возвращает только candidates; `SceneGeometry` выполняет exact geometry hit.
- Marquee запрашивает intersecting bounds, затем применяет текущий exact containment/intersection
  contract.
- Удали двойной полный hit-test одного клика: вычисленный hit передаётся следующему шагу либо
  безопасно кэшируется на одну interaction revision.
- Spatial index не сериализуется и не меняет document.

### Stage 13A gates

- `ui-tree-50k`: число realized controls не больше видимых строк + bounded overscan и всегда меньше
  500 в штатном окне;
- selection/drag counters: ноль full ObjectTree rebuild на pointer move;
- warm point hit-test 50K P95 ≤ 2 ms;
- small-region marquee 50K P95 ≤ 4 ms;
- small viewport simple-50K render P95 ≤ 8 ms;
- functional order/selection/group/search tests зелёные;
- native hidden-window scroll/select/drag остаётся отзывчивым и не использует системный ввод.

После зелёных ворот один локальный commit:

```text
perf(stage-13a): virtualize large-scene interactions
```

Если gate не пройден, не создавай commit успеха и не переходи к Stage 13B; сообщи `PARTIAL` с raw
результатом и узким следующим исправлением.

## Stage 13B — revision indexes и дешёвая immutable edit

### B1. Производный индекс документа

Добавь revision-local derived index в Core или рядом с Core без зависимости на UI/Skia:

- `ObjectId -> SceneObject`;
- `ObjectId -> ObjectLayer/position`;
- `GroupId -> ordered members`;
- при необходимости composition/vector lookup.

Индекс строится один раз для immutable revision, имеет понятное владение/освобождение и не
попадает в equality/serialization. Удали повторные линейные `FindObject`/`FindLayerOf` в hot path.

### B2. Persistent/chunked ObjectLayer

Сначала измерь минимум два варианта на identical fixture:

1. собственная chunked immutable collection с блоками разумного размера;
2. минимальное улучшение существующего массива/слоя через shared segments и derived lookup.

`ImmutableList<T>` или внешняя структура не принимается без сравнения iteration/edit allocation.
Выбери форму, которая:

- сохраняет быстрый последовательный render traversal;
- заменяет один объект за `O(log chunks)`/bounded copied chunk, а не копирует 50K;
- поддерживает ordered insert/remove/range replacement;
- даёт structural sharing для Undo snapshots;
- сериализуется текущим порядком без schema change либо имеет изолированный DTO adapter;
- не раскрывает mutable array наружу;
- не требует полной document validation на каждую preview/update.

Если переход требует изменения public constructors, сделай совместимый adapter и минимальную
миграцию source API. `.mapproj` schema не меняй в этом stage.

### B3. Инкрементальная валидация и spatial update

- Полную проверку document invariants оставь на construction/load/debug/test boundary, но command
  edit валидирует затронутый delta и неизменённые revision invariants не пересчитывает.
- Замена объекта удаляет старые и добавляет новые spatial cells/chunks; остальные entries shared.
- Стабильный draw order сохраняется после insert/remove/sublayer changes.
- Preview drag не создаёт полный MapDocument на каждый pointer event, если текущая модель это
  делает; завершение gesture создаёт одну immutable command revision.
- Undo/Redo возвращают exact old/new object, group/composition relations и index view.

### Stage 13B gates

- one-object edit на 50K: P95 ≤ 8 ms и managed allocation ≤ 512 KiB;
- selection последнего object 50K: P95 ≤ 2 ms;
- warm viewport query 50K: P95 ≤ 1 ms;
- cold index build 100K: ≤ 50 ms на текущем ПК либо измеренно не хуже baseline при существенно
  лучшем incremental update;
- repeated drag preview не создаёт 50K-sized lists/revisions;
- Undo/Redo, save/reopen и deterministic order проходят адресные tests;
- 100K stress edit завершается без resource-limit/crash; время записывается, но не объявляется
  60-FPS обещанием.

После зелёных ворот один локальный commit:

```text
perf(stage-13b): make large-scene edits incremental
```

## Stage 13C — chunk cache, LOD и batching

### C1. Сначала измерить draw-call проблему

Добавь counters для candidate count, exact draw calls, repeated asset instances, path/text/effect
cost и cache hit/miss. Не реализуй atlas/vertices/picture одновременно.

Сделай изолированные probes:

- current per-object draw;
- chunk `SKPicture` recorded once and replayed;
- `SKPictureRecorder.BeginRecording(bounds, useRTree:true)` если текущая SkiaSharp версия реально
  поддерживает этот overload и он быстрее;
- `DrawAtlas` для повторяющихся bitmap stamps с одинаковым compatible paint/effect policy;
- `SKVertices` только для действительно triangulated/static geometry;
- rasterized chunk image только для overview bucket, не для close edit/export.

Выбери по одинаковому render output, P95, allocations, native memory и invalidation cost. Не
комбинируй механизмы, не доказав отдельную пользу каждого.

### C2. Chunk ownership и invalidation

- Chunk key выводится из spatial cell/zoom bucket/revision-relevant style/effect state.
- Static content cache отделён от moving/selected/hovered overlay.
- Изменение объекта инвалидирует старые и новые chunks, не весь layer/document.
- Asset decode/style/filter change инвалидирует только зависимые chunks.
- Cache имеет item+byte budgets, LRU, deterministic disposal и diagnostics counters.
- Cache не живёт в Core и не используется между несовместимыми threads/Skia contexts.

### C3. Screen-space LOD

Зафиксируй не менее трёх tiers:

- `Exact`: close/normal zoom, полная geometry/text/shadow/filter;
- `Interactive`: во время pan/zoom можно использовать упрощённые эффекты, затем точный idle pass;
- `Overview`: subpixel objects агрегируются/упрощаются, но остаются видимыми в правильном месте и
  примерном цвете/плотности.

Правила:

- LOD основан на projected screen bounds, а не только world zoom;
- selection/hit-test остаются по точной geometry и не зависят от визуального aggregate;
- selected/hovered objects рисуются точно поверх overview;
- text не превращается в нечитаемую кашу: ниже threshold используется marker/coverage policy или
  пропуск с измеримым idle/detail transition;
- z-order/layer masks/opacity сохраняются настолько, насколько tier заявляет; любые сознательные
  упрощения задокументированы;
- export/preview с export policy всегда Exact и не берут viewport LOD cache;
- после остановки viewport exact pass не блокирует UI и не публикует результат старой revision.

### C4. Compositions и сложные primitives

- Composition items получают spatial bounds/query вместо полного внутреннего обхода, если это
  показано counters.
- Paths/text layouts/shape tessellation кэшируются по immutable geometry/style/font key.
- Sublayer order предварительно представлен стабильным ordered key; не сортируй 50K на каждый
  кадр, если revision не изменилась.
- Antialias/filter quality может снижаться только в Interactive/Overview и обязана возвращаться в
  Exact; не меняй export quality.

### Stage 13C gates

- simple-50K full overview viewport P95 ≤ 33,3 ms после warmup;
- mixed-50K full overview LOD P95 ≤ 33,3 ms либо `PARTIAL` с точным типом, который остаётся
  bottleneck; нельзя объявлять Stage 13C готовым по simple-only сцене;
- normal-zoom mixed-50K P95 ≤ 16,7 ms для renderer и native input-to-present P95 ≤ 33,3 ms;
- после warmup нет повторяющихся stalls >100 ms при pan/zoom; max и причины записать;
- изменение одного объекта не сбрасывает >bounded affected chunks;
- close zoom и exact offscreen render совпадают с pre-LOD reference по существующей tolerance;
- export result не зависит от viewport LOD state;
- native resources возвращаются к bounded plateau после eviction/document close.

После зелёных ворот один локальный commit:

```text
perf(stage-13c): add bounded chunked lod rendering
```

## Stage 13D — 131K supported canvas и 262K test-only probe

Не начинай этот срез, пока Stage 13A–13C не зелёные. Больший пустой canvas не исправляет lag и не
является заменой предыдущим stages.

### D1. 131 072 × 131 072

- Подними `CanvasDimensionPolicy.Maximum` до 131 072 только вместе со всеми dependent validators,
  wizard/resize/region/thumbnail/viewport/export contracts.
- Пересмотри fit/min zoom так, чтобы целый canvas можно было показать в реальном editor viewport;
  используй dynamic fit или обоснованный новый MinScale без потери точности zoom-to-cursor.
- Проведи checked integer/long/double audit tile keys, resize translations, grid geometry,
  world/screen transform, clipping и effect bleed.
- Sparse raster операции проходят только материализованные source/destination tiles.
- Single-image export остаётся максимум 16 384 по стороне.
- Tile-package export получает общий output/canvas предел 131 072, но каждый surface остаётся tile
  edge + bounded bleed. Manifest counts/coordinates используют checked arithmetic.
- Project budgets 256 MiB tiles/512 MiB total не повышай автоматически: огромная карта допустима
  как sparse, а resource-limit остаётся честным.

### D2. 262 144 × 262 144 test-only

Не показывай этот предел в обычном UI. Добавь synthetic tests/probe, которые доказывают:

- создание Core canvas definition через отдельный experimental/test policy;
- sparse object/tile на ближнем и дальнем краю;
- pan/zoom/fit math без NaN/overflow/precision collapse;
- point hit-test и small region export в дальней координате;
- tile planning без прохода по площади и без full bitmap;
- понятный отказ production policy, пока владелец отдельно не принимает новый максимум.

Если 262K требует скрытого `O(width*height)`, не обходи gate повышением памяти — оставь 131K
поддерживаемым, 262K `REJECTED` с точным bottleneck.

### Stage 13D gates

- 131K sparse map с 50K mixed objects проходит create/fit/pan/zoom/select/drag/Undo/Redo;
- save/reopen сохраняет document/objects/groups/compositions/sparse tiles/limits точно;
- 16K single export и >16K tile package работают без full-canvas allocation;
- native 50K normal/overview gates Stage 13C не регрессируют после повышения размера;
- 100K stress profile открывается/индексируется/save-reopen без crash и с записанным временем;
- 262K test-only probe либо зелёный, либо честно `REJECTED`; public max остаётся 131K.

После зелёных ворот один локальный commit:

```text
feat(stage-13d): extend the supported sparse canvas
```

## Stress-проверки: обязательная матрица

Не запускай full suite. Выполни только относящиеся Stage 13 tests/runner и один native smoke.

| Профиль | 10K | 50K | 100K |
|---|---|---|---|
| cold/warm index + small/full query | smoke | acceptance | stress |
| small viewport simple/mixed render | regression | acceptance | record-only stress |
| full overview simple/mixed + LOD | regression | acceptance | record-only stress |
| hit-test/marquee/select/drag | regression | acceptance | stress no-crash |
| object list open/scroll/search/group | regression | acceptance | stress bounded rows |
| one-object edit/Undo/Redo | regression | acceptance | stress no-crash |
| save/reopen semantic equality | optional smoke | acceptance | stress |
| sparse tile region/export | smoke | acceptance | stress only after 131K |

Для каждого performance profile:

- один warmup series и не менее пяти measured samples;
- median/P95/max, allocations и counters;
- одинаковый seed и fixture hash before/after;
- не смешивать cold cache с warm values;
- не выполнять бесконечные повторы «для уверенности»;
- если variance >15%, один раз найти внешнюю причину/увеличить sample duration; после повторной
  нестабильности сообщить `PARTIAL`, а не выбирать лучший run;
- не закрывать/останавливать пользовательские процессы и не менять системные performance settings.

## Native input-to-present smoke

Один собственный скрытый Avalonia/Win32 window:

- `ShowActivated=false`, `ShowInTaskbar=false`;
- никаких `SendInput`, foreground activation, кликов по чужим окнам или системного переключения;
- отдельный временный local profile, без пользовательских recent/recovery/assets/diagnostics;
- загрузить synthetic mixed-50K;
- открыть/прокрутить/search Objects list;
- pan/zoom и Fit Screen;
- select точечным hit-test, marquee небольшую область, drag object и завершить одну command;
- проверить Undo/Redo и отсутствие full list rebuild на pointer events;
- дождаться LOD idle refinement;
- выполнить маленький exact region export/read-back;
- закрыть window и проверить dispose caches/native resources.

Измеряй от routed input/interaction start до фактического render/present completion собственным
instrumentation hook. Вызов одного `SceneRenderer.Render` не является input-to-present.

## Функциональные regression tests

Создай адресные filters `Stage13A`, `Stage13B`, `Stage13C`, `Stage13D` в существующих подходящих
test projects. Каждый срез проверяет только изменённую логику. Обязательно покрыть:

- stable z/sublayer order после spatial query;
- точный rotated/curved/text hit после broad phase;
- marquee/group/search/selection semantics;
- virtual list realization/recycling и keyboard/UIA selection;
- no sidebar rebuild on drag/selection-only event;
- persistent chunk insert/remove/replace и structural sharing;
- Undo/Redo/save/reopen equality;
- cache invalidation только affected chunks и deterministic disposal;
- LOD tier switching/stale result rejection/exact export;
- 131K dimension/fit/coordinate/tile manifest overflow boundaries;
- public rejection test для 262K до отдельного решения.

Performance thresholds не смешивай с pixel correctness в одном хрупком тесте. Functional tests
детерминированы; timing gates живут в runner/native profile.

## Проверки и build budget

После каждого законченного среза:

1. один относящийся test project/filter `Stage13X`;
2. один сопоставимый performance profile before/after для изменённого hot path;
3. один `dotnet build .\Map.slnx -c Release` после последнего изменения production inputs этого
   среза;
4. native smoke один раз в конце Stage 13A для UI virtualization/events и один объединённый раз в
   конце Stage 13C/13D, если UI/render/canvas изменились после первого.

Не повторяй зелёный build/test без изменения входов. Не запускай весь каталог `tests`, full suite,
VM, coverage, mutation, security scan, SBOM, packaging или independent acceptance.

## Documentation и handoff

После каждого принятого среза минимально обнови:

- `docs/STATE_HANDOFF.md` — exact local commit, фактические gates и remaining Stage 13 slice;
- `docs/06_DEVELOPMENT_ROADMAP.md` — `PROPOSED/PARTIAL/FACT` без преждевременного повышения
  лимитов;
- `docs/04_TECH_ARCHITECTURE.md` — только реально выбранная структура/cache/LOD policy;
- `docs/08_QA_AND_ACCEPTANCE.md` — только если фактический runner contract изменился;
- `docs/research/SOURCES.md`/licensing registry — только если реально использован внешний код.

Не создавай evidence tree, отдельный review prompt, screenshots archive, PR text или GitHub issue.
Generated benchmark JSON/traces остаются ignored/temp; в handoff записываются короткие численные
результаты и exact command/profile.

## Stop-gates

Остановись без разрушительных действий и сообщи `BLOCKED`, если:

- latest accepted product HEAD невозможно однозначно определить;
- пересекающиеся owner production changes нельзя сохранить;
- корректная оптимизация требует смены Avalonia/Skia/native offline architecture;
- выбранное решение ломает immutable source of truth, Undo/Redo или atomic save;
- 50K gate можно пройти только скрытием/потерей объектов в close zoom/export;
- 131K требует full-canvas allocation;
- требуются cloud service, Inkarnate internals, чужие assets/code с неясной лицензией;
- требуется schema migration, которую нельзя сделать совместимо в Stage 13 и которая пересекается
  с не начатым Stage 12D;
- требуется GitHub/remote action.

Сообщи `PARTIAL`, а не `BLOCKED`, если один performance gate пока не достигнут, но существует
безопасный локальный следующий эксперимент. Не переходи к повышению canvas limit при `PARTIAL`
Stage 13A–13C.

## Итоговый отчёт

После фактической работы дай короткий русский отчёт с точными статусами:

1. `FACT`: что изменено и для какого branch/HEAD;
2. `FACT`: какие functional/performance/native gates выполнены и точные P50/P95/max/allocations;
3. `PARTIAL/REJECTED`: какие профили не прошли и почему;
4. `NOT CHECKED`: что сознательно не запускалось;
5. exact local commits и status worktree;
6. подтверждение, что GitHub и Inkarnate не изменялись;
7. текущие public limits: canvas, supported 50K и stress 100K без смешения статусов.

Закончи обязательным разделом:

```text
## ИТОГИ

Сделано: простыми словами, что теперь получает владелец.
Что дальше: один понятный следующий шаг или отсутствие обязательных действий.
Реализация проекта: N% — округлённая оценка относительно текущего roadmap и одно короткое
объяснение, почему это оценка, а не процент тестов.
```

Не называй Stage 13, 50K/100K или 131K/262K готовыми, если соответствующие gates не прошли.
