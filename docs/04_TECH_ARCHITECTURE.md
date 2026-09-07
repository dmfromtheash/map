# 04. Техническая архитектура

## Stage 18 — локальное заполнение многоугольника (2026-09-06–07)

`PlacementArea` хранит входы, простую замкнутую границу, permanent exclusions и persistent
`ImmutableDictionary<cell, ImmutableArray<member>>`. Слот `(cellX, cellY, ordinal)` адресуется
в мировой сетке 100×100. ObjectId — SHA-256 от ID области и слота. Renderer и экспорт читают
материализованные SceneObject и не меняют документ.

LayoutVersion=4 строит локально вычисляемые опорные позиции (`PlacementAreaLayout`), а не
случайные точки с последующим отбором по маске. Гряда имеет связную основную линию, смещённые
боковые группы и отроги; размеры гряды привязаны к уровню подножия, шаг отрога сужается
к концу. Позиции материала с силуэтом рассчитываются отдельным правилом, прежняя версия 4
без силуэтов сохраняется. Массивы — искривлённая решётка с локальными смещениями, крупными
ядрами и мелкими краями. «Группы» оставляют более широкие промежутки; «Вокруг просветов»
резервирует внутренние пустоты и проходы. Приоритеты основы/окружения/края задают ацикличное
разрешение конфликтов между соседями. Плотность 0–100 выбирает из одного полного рисунка,
не влияя на разрешение конфликтов. Seed, CompositionOrigin/Length/Angle фиксируются при
создании; правка сохранённой границы их не пересчитывает.

В sampler остаётся LRU до 16 384 объектов-кандидатов. Скалярные позиции версии 4 кэшируются
в пределах 128 ячеек и 32 768 записей (одна более крупная ячейка хранится отдельно от остальных);
в допуске памяти учтены ещё 4 MiB для этих позиций. Оба кэша принадлежат одной операции, не истории. Нет растровой
маски размером с карту, копий пикселей на каждый штамп или сетевого расчёта. Count потоковый,
без полного списка объектов. Preview версии 4 выбирает реальные позиции в максимум 256
распределённых ячейках, до 16 проб на ячейку и до 64 итоговых штампов: консервативная верхняя
граница числа ordinal не используется как фактическая плотность позиций.

`StampContentMeasurer` один раз сканирует alpha через PeekPixels общего декодированного
изображения. В документ сохраняются нормализованные границы и силуэт 32×32 (32 строки uint,
128 байт на исходник); UI-кэш ограничен 256 записями. Bounds берут любую ненулевую alpha
с полем в один исходный пиксель, силуэт — alpha не ниже 32/255. Копий bitmap нет.
`PlacementAreaSilhouette` преобразует общую маску аффинно с учётом положения, размера,
отражений и поворота. В паре ограничивается покрытие заднего силуэта (60%) и его верхних
30% по высоте (32% заполненных клеток); порядок совпадает с фактическим depth order.
Это попарная дискретная оценка, не гарантия суммарной видимости каждого пикселя при любом
числе соседей. Без силуэтов либо с blur/shadow/clip-mask сохраняется консервативная проверка
скруглённых центров и AABB (55% меньшей площади). Без смыкания границы не пересекаются. Посторонние
объекты того же слоя/подуровня всегда блокируют видимый прямоугольник; `OccupiedSlots`
отличаются от постоянных ручных `Exclusions`.

`PlacementAreaDrawOrder` хранит производный persistent sorted set по нижнему видимому краю
объекта. Новые объекты со смыканием вставляются между сохранившимися; `SceneObjectList.InsertMany`
разделяет незатронутые chunks. ID/revision/spatial/overview индексы получают структурный delta.
Существующие объекты сохраняют ссылки и относительный порядок. При загрузке индекс строится
по материализованным членам, ручное отделение передаёт его следующему снимку. В JSON его нет.

Версии 0/1/2/3 сохраняют прежний отбор, включая ordinal/priority/cell старых sampler. Старое
изображение области не перестраивается при чтении. Явная кнопка «Расположить естественно»
обновляет правило; общая правка рисунка/размеров/вида может заново упорядочить собственные
объекты за один Undo-шаг. Граница и плотность такого общего переупорядочивания не вызывают.
Временные кэши и оценка изменяемых ячеек входят в DEC-025.

При экспорте встроенные исходники оцениваются по заголовкам через codec/MemoryStream,
без копии encoded bytes и без pixel decode. Резерв кэша ограничен его прежними 256 MiB,
но для маленькой карты складывается из фактических размеров, encoded bytes и 16 MiB
служебного запаса. Linked/неизвестные исходники сохраняют полный консервативный резерв.
Это устраняет ложное требование большого резерва Windows для небольшого экспорта;
допуск настоящих крупных операций и атомарность вывода не изменены.

`ManualAreaSelectionCommand` объединяет ручное удаление/геометрическое изменение/перенос на
другой слой с отделением выбранных членов области. Меняются только затронутые ячейки владения,
их счётчик и Exclusions; данные остальных ячеек разделяются с прежним снимком. Обычная команда
применяется к снимку с отделёнными членами, а Undo/Redo возвращают точные итоговые снимки.
Preview и отменённое перетаскивание не отделяют объекты. Общие ownership guards остаются для
прочих команд и объектов пути; скрытые/заблокированные участники не дают частичного результата.

`PlacementAreaGeometry` строит консервативные интервалы ячеек scanline-обходом симметрической
разности старой/новой границы. Совпадающие рёбра и перекрывающиеся горизонтальные отрезки
сокращаются; события вершин и пересечений разделяют интервалы. В каждой затронутой ячейке
проверяется вхождение центра. Без изменения параметров остальные ячейки разделяют прежние
immutable массивы. Старые объекты остаются на своих местах; новые вставляются в порядок глубины
области со смыканием либо дописываются в конец слоя без него;
удаление использует существующий persistent chunk/ID/spatial/overview delta Stage 17.

`PlacementAreaCommand` — единая транзакция метаданных и объектов с точным сохранением снимков
для Undo/Redo. Пересчёт не может присвоить чужой ID или слот отделённого объекта. Новый набор
встраивается по хешам, включая ещё не использованные случайные варианты. Полная проверка
входов/связей выполняется при чтении, локальная команда проверяет собственный delta. Допуск
DEC-025 сохраняется: обычный edit reserve плюс оценка затронутых ячеек/кандидатов; отказ не
меняет документ или историю. Изображения разделяются между экземплярами.

Presentation запускает точный Count с debounce 150 ms, отменой устаревшей задачи и последовательным
выполнением расчётов. Результат публикуется только для того же актуального черновика. Счётчик
и описание рисунка закреплены над «Разместить». Presentation держит черновик вне документа: до 64 конкретных штампов, распределённых по
изменяемой части, до 128 override-записей с учётом удаляемых образцов. Геометрия проверяется
при предпросмотре и завершении жеста; самопересечение/вырождение явно отклоняются. CanvasInputRouter
управляет новой жестовой веткой; рабочая панель восстанавливает сохранённые параметры, источник
и миниатюру без каталога. App рисует контур и ручки отдельно от SceneRenderer.

Адресный `native-stage18` использует собственное неактивирующееся окно и изолированные profile,
project, recovery, settings. Кнопки/поля и canvas router проверяются внутри visual tree;
выбор исходного набора, reopen, Undo/Redo и файловые диалоги используют явно обозначенные
адаптеры. Замер берётся на временной копии существующей карты, исходный SHA-256 проверяется.
Это кадр Avalonia, не физический scan-out; retained heap включает историю, индексы и кадр.

## Stage 17 — путь расстановки (2026-09-06)

Core хранит immutable `PlacementPath` в ObjectLayer и выполняет `PlacementPathCommand` как
единую транзакцию над входами, ownership и конкретными SceneObject. Undo/Redo возвращают точные
снимки. Планировщик использует существующую детерминированную расстановку штампов; random seed
зависит от seed пути и слота, а ObjectId — от path ID и слота. Перенос узла пересчитывает только
два соседних сегмента, изменение общего шага/стиля — собственный ряд. Rendering не меняет модель.

Presentation держит незавершённые узлы и drag preview отдельно от Session.Document, максимум
64 видимых штампа. UI использует штатный CanvasInputRouter и существующие целочисленные поля,
scale/style controls. Путь можно выбрать на холсте или в списке, отделение имеет явную кнопку.
Прямое изменение управляемого штампа отклоняется до записи истории. Скрытые/заблокированные
слои и sublayer учитываются; редактирующие линии/узлы рисует только canvas control, не exporter.

Persistent SceneObjectList разделяет неизменённые chunks и производный ID→chunk/offset индекс.
Revision index переносит replacement/removal/append delta; удаление элементов пути не заставляет
пересоздавать глобальный индекс. Spatial index использует стабильную базу с собственными
изменениями, overview обновляет затронутые chunks по ObjectId с сохранением текущего порядка.
Первичное построение индексов остаётся холодной операцией над документом. Полный пересчёт карты
не нужен при последующей локальной правке. Увеличенный replacement delta собственного длинного
пути не сбрасывается из-за прежнего порога 1024 объектов.

Общий WorkingMemoryBudget сохранён: обычный edit reserve плюс оценка membership и объектов
изменённых сегментов; отказ не меняет document/revision/history. Изображения разделяются по SHA.
В `tools/Map.Performance` добавлен адресный native-stage17: собственное неактивное окно, scoped
control/router events, изолированные файлы; reopen/file picker и выбор набора — обозначенные
adapter calls. Это проверка кадра Avalonia, не измерение физического scan-out экрана.

## Фактический стек — сверка 2026-09-05

| Слой | Выбор | Причина |
|---|---|---|
| Runtime | .NET 10 LTS / C# 14 | установлен на рабочем ПК владельца, быстрый локальный build/run |
| Desktop UI | Avalonia 12.1.1 | нативное окно без Chromium, Windows backend |
| Canvas | Skia через Avalonia custom rendering | аппаратный Direct3D-путь с программным fallback, offscreen render/export |
| UI state | собственный `ViewModelBase` / `INotifyPropertyChanged` | Presentation не зависит от Avalonia; Core не зависит от UI |
| Tests | xUnit с собственными assertions xUnit | адресные unit/integration tests вне `Map.slnx` |
| Диагностика скорости | собственные scenario counters в `tools/Map.Performance` | отдельные адресные сценарии только при необходимости |
| Запуск | framework-dependent `win-x64` | минимальный локальный output для текущего ПК |

Версии пакетов заданы в `.csproj`; SkiaSharp — 3.119.4. Никакой runtime-зависимости от Python,
Node, браузера или сети у приложения нет. Описание возможных альтернатив ниже является историей
выбора стека, а не текущим поручением менять архитектуру.

## Почему не Electron/Tauri

Требование — именно программа, не веб-приложение. WebView увеличивает размер/память и переносит продукт в браузерную модель. Для сложного графического холста это не даёт преимущества, оправдывающего нарушение требования.

## Почему не Qt 6/C++ сейчас

Qt Graphics View зрелый и силён для множества 2D-объектов, но C++ увеличивает стоимость итерации с Opus и сложность memory-safety. LGPL/GPL-комбинации требуют более строгого контроля динамической линковки и модулей. Qt остаётся резервом, если Avalonia/Skia spike не проходит бюджеты.

## Почему не Godot сейчас

Godot хорош как 2D-движок и умеет экспортировать Windows-приложения, но редактор карт — document-centric desktop tool с док-панелями, доступностью, файловыми диалогами и сложными inspectors. Это возможно, но потребует больше собственной UI-инфраструктуры. Godot остаётся резервом для случая, если приоритет сместится к игровому viewport.

## Модули решения

```text
Map.App              composition root, Avalonia views, window lifecycle, file dialogs
Map.Presentation     view-models, UI contracts, canvas input router; без Avalonia
Map.Core             document model, geometry, tools, commands, invariants
Map.Rendering        scene traversal, caches, Skia adapters; геометрический hit-test в Core
Map.Persistence      .mapproj, migrations, atomic save, recovery journal
Map.Assets           import, validation, catalog, thumbnails, provenance
Map.Export           offscreen tiled export and color pipeline
Map.Platform.Windows Windows paths, settings, recent maps, diagnostics
Map.Tests.*          адресные regression tests, запускаемые только при необходимости
```

Зависимости направлены внутрь:

```text
App -> все семь production-библиотек
Presentation -> Core, Assets, Rendering, Persistence, Export
Export -> Core, Rendering
Rendering, Persistence, Assets, Platform.Windows -> Core
Core -> нет других проектов Map, UI или Skia
```

### Stage 12D: граница открытия и миграции файла

Следующий абзац описывает исторический Stage 12D. Текущий writer выбирает schema v4/v5/v6,
а copy-only registry последовательно переносит v1/v2/v3 до v4 (DEC-022). Object payload хранится
в независимых bounded chunks; текущие ограничения и расхождение общих byte budgets с DEC-021/022
отмечены в `STATE_HANDOFF.md`. Исторический v2 не является текущей целевой схемой.

`Map.Persistence` сначала классифицирует контейнер через общий bounded ZIP/JSON admission. V2
открывается полностью, v1 только предлагает последовательную `v1 → v2` migration-copy, future
schema не понижается. Миграция пишет уникальный sibling temp, выполняет полный v2 read-back и
публикует только в отсутствующий destination; source никогда не открывается на запись. Unknown
object хранится в `Map.Core` как `Placeholder` с bounded immutable raw JSON без зависимости Core
от persistence DTO. Dashboard выполняет inspection/migration асинхронно, а локальный diagnostics
log получает только стабильные codes и тип исключения без абсолютных путей и содержимого карты.

## Модель редактирования

- `MapDocument` — единственный источник истины.
- UI создаёт `IEditCommand`; команда валидирует preconditions и выдаёт reversible delta.
- Drag/brush gesture собирает промежуточный preview, а в историю попадает одна завершённая транзакция.
- Renderer получает immutable snapshot/revision и не изменяет документ.
- Долгие операции работают на snapshot с cancellation token и progress.
- Selection, viewport и hover — session state, а не обязательная часть сохранённой сцены.

## Рендеринг

### Координаты

- документ хранит world coordinates в `double`;
- viewport — матрица world→screen;
- raster layers имеют собственную pixel density и tile origin;
- export использует тот же scene evaluator, но другой target и scale.

### Проход

1. определить видимую область;
2. запросить spatial index для Object layers;
3. взять валидные layer/tile caches;
4. применить masks → content → effects в зафиксированном порядке;
5. нарисовать selection/guides отдельным overlay, не попадающим в export.

### Производительность

- spatial index для hit-test и culling;
- tile cache для Brush/Land и дорогих фильтров;
- revision-based invalidation, а не глобальный redraw по любому property;
- LOD при движении viewport и точный pass после idle;
- texture atlas применять только после измерения, не как преждевременную сложность;
- background decoding/thumbnails с ограниченным parallelism;
- память undo/cache имеет бюджеты и telemetry только локально в diagnostics.

### Stage 13: стоимость зависит от видимого и изменённого

Эта форма реализована Stage 13A–13D. Операции `O(all objects)` убраны из pointer/drag/UI-list
hot path; повышение canvas limit выполнено только после зелёных 50K render/native gates.

- `DocumentRevisionIndex` хранит object/layer/group lookup и
  spatial entries для конкретной immutable revision; он не сериализуется и не становится вторым
  источником истины.
- `SceneObjectList` хранит object layer persistent chunks со structural sharing: изменение одного
  объекта создаёт новый небольшой chunk и новый корень, а не полный список на 50 000 элементов.
- Spatial index возвращает стабильные draw-order indices. Hit-test, marquee и viewport используют
  один broad phase, после которого точная geometry проверяется только у кандидатов.
- События `DocumentStructureChanged`, `ObjectPropertiesChanged`, `SelectionChanged`,
  `ViewportChanged` и `CanvasInvalidated` не подменяют друг друга. Selection/drag не перестраивают
  Objects list; несколько invalidation до кадра coalesce-ятся.
- Objects list использует `ItemsSource` и `VirtualizingStackPanel`/измеренный эквивалент. Число
  созданных controls ограничено видимыми строками и overscan; поиск/группы не материализуют
  отдельный control на каждый скрытый объект.
- Scene chunks получают bounded raster `ObjectOverviewCache`. Близкий zoom и export используют
  точную геометрию; overview хранит маленькое raster image на spatial chunk/zoom bucket и
  агрегирует subpixel primitives по совместимым batch. `DrawAtlas` и `SKVertices` измерены, но не
  выбраны для heterogeneous mixed scene.
- Редактируемый/moving объект исключается из static chunk cache и рисуется dynamic overlay;
  завершение команды инвалидирует только старые и новые spatial chunks.
- Кэширование имеет 1024-item/64 MiB предел, LRU/eviction, render-thread guard и deterministic
  dispose нативных Skia resources. Content hash инвалидирует только изменившиеся old/new chunks;
  selected/moving object рисуется отдельным exact overlay.
- Размер логического canvas не определяет размер единого render surface. Viewport, preview и
  export продолжают работать tiles/regions; public canvas равен 131 072, single export остаётся
  16 384, tile-package — 131 072, dense full-canvas bitmap запрещён.

Сравнительные probes не обосновали новую production dependency: сохранены собственный uniform
spatial index, встроенная виртуализация Avalonia и уже используемый SkiaSharp. `NetTopologySuite`,
`Mapsui`, Godot и внешние reference repositories не добавлены в продукт.

Профили и численные acceptance-критерии находятся в
`docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`; roadmap хранит пользовательские границы, а не детали
конкретного контейнера данных.

## Потоки

### Технические исправления 2026-09-06

Это текущая реализация поверх `196062e`; прежние Stage 13 числа относятся к своим fixtures.
Общий итог и измерения находятся в handoff и внешнем отчёте readiness.

- Catalog разделяет SHA content и membership variant: новый logical set получает собственные
  variant IDs, content bytes дедуплицируются. Sync mutation работает с private copy и
  публикуется в памяти после durable index write. Ошибка возвращает прежний root; metadata
  writer хранит последнюю успешно записанную копию для восстановления favorites/recent.
- Save, primary autosave и recovery фиксируют одну immutable revision и один operation-owned
  `ExportStampImageSource`. Thumbnail этой revision создаётся один раз и переиспользуется
  файлом/recent; версия через UI создаётся async и отвергается, если исходный document сменился.
  Worker completion возвращается в UI context. Ошибка recent-index после durable save не
  превращает успешную запись карты в ложный отказ.
- Thumbnail передаёт actual scale/visible bounds и stamp source общему renderer, исключая
  selection/chrome. Декодированный source и ступени minification принадлежат одному
  `AssetImageCache` и одному byte envelope; близкий масштаб использует original image.
- Native detail tiles могут вычисляться существующим borrowed Skia GPU context. Готовый
  результат читается обратно в raster-owned cache; время включает завершение GPU работы.
  При отсутствии GPU действует CPU path. Cache ownership/thread pins не передаются UI worker.
  Порядок, conservative culling и старые/новые bounds сохраняются. После G1–G4 sampling учитывает
  размер каждого stamp, явные CPU mip pixels и исходный порядок GPU meshes (DEC-025).
  Incremental/fresh/reopen внутри backend совпадают точно; CPU/GPU округление подтверждается
  отдельным пооперационным разбором, а не произвольной поблажкой общему RGB.
- Save/version/autosave/preview/export одного EditorViewModel используют общий serial resource
  slot. Отменённый preview, который ещё не освободил native buffers, не запускает рядом второй.
  Export учитывает active tile/bleed, decoded cache, tile cache и encoder. Live Windows available
  RAM дополнительно проходит общий WorkingMemoryBudget (DEC-025): private всех Map, OS reserve,
  возвращаемые резервы и один межоконный/межпроцессный heavy slot. Export engine владеет своим
  резервом, per-editor gate сохраняет порядок. Диск проверяется для полного sibling temp.
  Это согласованный admission; allocator Windows и фактический VRAM проверяются отдельно.
- Single image и tile package вычисляют одинаковую сетку active tiles с effect bleed.
  PNG single output потоково пишет строки через ограниченные bands (DEC-023). JPEG/WebP single
  output собирают ограниченный final bitmap для encoder; package пишет тайлы
  последовательно. `SKData` остаётся native encoded buffer и передаётся file stream без
  дополнительного полного managed `ToArray`. Маленький preview сохраняет byte[] API.
- Detach освобождает gallery/version/export bitmaps и canvas caches; stamp images освобождаются
  под тем же render gate после pinned frame. Undo не обрезается произвольной глубиной.

### Небольшой контракт расширения инструмента

Каждое расширение использует существующие границы, не второй источник истины:

| Граница | Stamp | Brush |
|---|---|---|
| Данные / validation | SceneObject + StampStyle + SHA resource reference | BrushLayer + sparse immutable TileMap / byte checks |
| Ввод / временный жест | CanvasInputRouter → placement preview | CanvasInputRouter → Begin/Continue/EndStroke, transient coverage |
| Команда / Undo | PlaceAssetObjectsCommand / ReplaceObjectsCommand | BrushStrokeCommand: before/after только затронутых tiles |
| Отбор / hit / affected region | SceneGeometry bounds, ObjectSpatialIndex, old+new bounds и эффекты | допустимый raster target, bounds мазка и изменённые TileKey |
| Rendering / LOD | ordered evaluator, conservative culling, bounded detail/overview cache | TileImageCache, mask/material/alpha в порядке слоёв |
| Persistence / migration | v4 bounded ordered object chunks, source bytes/references | только занятые raster entries; canonical keys и bytes |
| Export | тот же evaluator с resource source, без UI chrome | тот же tile/filter evaluator с полями эффектов |

Проверка контракта: native pointer placement/Brush/undo/save/reopen, targeted geometry/chunk
tests и locality probe. При 20K/50K/200K объектов изменение одного объекта копирует один
256-object chunk и обновляет одну index delta; при 50/512/8192 raster tiles малый stroke
копирует один 256 KiB buffer, сохраняет untouched bytes и точно отменяется. Bounds эффектов,
ошибки target и источников должны проверяться до публикации команды; renderer не меняет документ.
Новая разновидность эффекта должна также явно определить локальный halo либо глобальную
зависимость для export. Кэши, indexes и transient previews не сериализуются.

- UI thread: ввод, короткие изменения модели, композиция кадра;
- worker pool: decode, thumbnail, save serialization, export tiles, checksums;
- snapshot не блокирует редактирование надолго;
- результат фоновой операции применяется только если document revision/operation token совместим.

## Надёжность

- атомарный save: write sibling temp → flush/закрытие → полный semantic read-back → atomic replace;
  предыдущий target становится одним bounded .bak при замене;
- recovery journal в `%LOCALAPPDATA%\Map\Recovery`, а не рядом с read-only файлом;
- startup lock только на конкретный открытый документ, с обработкой stale lock;
- crash dump/diagnostic log не содержит содержимое карты или пути без согласия на экспорт отчёта;
- миграция выполняется на копии и сохраняет source backup.

## Устойчивость импорта к битым файлам

Это требование к надёжности, а не киберзащита: программа локальная и офлайн, чужих файлов из сети не принимает. Но собственные файлы владельца регулярно оказываются повреждёнными — оборванная запись, копирование на флешку, сбойный экспорт из другого редактора, случайно указанный не тот файл. Редактор обязан в этих случаях дать понятную ошибку, а не упасть, зависнуть на разжатии или записать данные мимо контейнера.

- лимиты размеров и пикселей: файл, который разжимается в десятки гигабайт, отклоняется до чтения, а не съедает память;
- пути внутри ZIP приводятся к каноническому виду; `..`, абсолютные пути и symlink наружу считаются повреждением контейнера;
- SVG читается в ограниченном статическом профиле: без script, внешних URL и встроенного HTML — такие узлы не нужны карте и являются признаком неподходящего файла;
- битый asset не ломает каталог целиком: он попадает в placeholder с сохранением исходных данных;
- шрифты и метаданные проходят отдельную валидацию.

## Диагностика без телеметрии

Локальная страница Diagnostics показывает renderer, GPU/driver, CPU fallback, размеры cache/undo, open document stats, последние save/recovery события. Отправка наружу отсутствует по умолчанию.

Нижняя цветная плашка editor (прямое дополнение владельца от 2026-09-06) показывает размеры,
число top-level объектов, FPS и состояние сохранения; по нажатию — слои, заметки, версии,
Undo/Redo, файл, autosave/recovery и последнее durable время. Счётчик объектов суммирует
layer totals, не обходит все элементы при refresh. FPS считает завершённые разные map frames,
исключая повтор compositor, и не является частотой монитора или 1000/render time. Наблюдатель
обновляет UI раз в 500 мс, не инвалидирует холст ради себя, останавливается при detach и не
меняет документ. Время сохранения при reopen берётся из metadata, а не из часов открытия.

## Технические ворота до принятия DEC-002

Spike должен доказать:

1. настоящее нативное окно и отсутствие WebView/server;
2. pan/zoom 60 FPS на 10 000 простых объектов на тестовом ПК;
3. hit-test/select без полного обхода сцены;
4. raster brush tile и одна undo-транзакция;
5. layer mask preview;
6. offscreen export минимум 4K;
7. atomic save + recovery после принудительного завершения;
8. framework-dependent `win-x64` запуск в настоящем окне на текущем ПК владельца.

Если два прохода оптимизации не дают бюджетов, сравнить Qt spike на том же сценарии и вынести DEC-002 владельцу заново.
