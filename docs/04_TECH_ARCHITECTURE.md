# 04. Техническая архитектура

## Предлагаемый стек

| Слой | Выбор | Причина |
|---|---|---|
| Runtime | .NET 10 LTS / C# 14 | установлен на рабочем ПК владельца, быстрый локальный build/run |
| Desktop UI | Avalonia 12.1 | нативное окно без Chromium, MIT, Windows backend и доступность |
| Canvas | Skia через Avalonia custom rendering | аппаратный Direct3D-путь с программным fallback, offscreen render/export |
| UI state | CommunityToolkit.Mvvm | небольшой MIT toolkit; не смешивать с доменной моделью |
| Tests | xUnit + FluentAssertions только если лицензия/политика подтверждены; иначе xUnit assertions | быстрые unit/integration tests |
| Benchmarks | BenchmarkDotNet + встроенные scenario counters | измерять scene traversal, hit test, save/export |
| Запуск | framework-dependent `win-x64` | минимальный локальный output для текущего ПК |

Версии пакетов фиксируются после spike. Никакой runtime-зависимости от Python, Node, браузера или сети у приложения нет.

## Почему не Electron/Tauri

Требование — именно программа, не веб-приложение. WebView увеличивает размер/память и переносит продукт в браузерную модель. Для сложного графического холста это не даёт преимущества, оправдывающего нарушение требования.

## Почему не Qt 6/C++ сейчас

Qt Graphics View зрелый и силён для множества 2D-объектов, но C++ увеличивает стоимость итерации с Opus, сложность memory-safety и поставки. LGPL/GPL-комбинации требуют более строгого контроля динамической линковки и модулей. Qt остаётся резервом, если Avalonia/Skia spike не проходит бюджеты.

## Почему не Godot сейчас

Godot хорош как 2D-движок и умеет экспортировать Windows-приложения, но редактор карт — document-centric desktop tool с док-панелями, доступностью, файловыми диалогами и сложными inspectors. Это возможно, но потребует больше собственной UI-инфраструктуры. Godot остаётся резервом для случая, если приоритет сместится к игровому viewport.

## Модули решения

```text
Map.App              composition root, window lifecycle, DI
Map.Presentation     Avalonia views/view-models, commands, dialogs
Map.Core             document model, geometry, tools, commands, invariants
Map.Rendering        scene traversal, caches, hit-test, Skia adapters
Map.Persistence      .mapproj, migrations, atomic save, recovery journal
Map.Assets           import, validation, catalog, thumbnails, provenance
Map.Export           offscreen tiled export and color pipeline
Map.Platform.Windows Windows paths, file dialogs, diagnostics
Map.Tests.*          адресные regression tests, запускаемые только при необходимости
```

Зависимости направлены внутрь:

```text
Presentation ─┐
Rendering ────┼──> Core
Persistence ──┤
Assets ───────┤
Export ───────┘
App связывает адаптеры, но Core не знает о них.
```

### Stage 12D: граница открытия и миграции файла

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

- UI thread: ввод, короткие изменения модели, композиция кадра;
- worker pool: decode, thumbnail, save serialization, export tiles, checksums;
- snapshot не блокирует редактирование надолго;
- результат фоновой операции применяется только если document revision/operation token совместим.

## Надёжность

- атомарный save: write temp → validate/read-back manifest → flush → replace → backup;
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
