# 02. Функциональная спецификация

## Статусы и приоритеты

- `FACT` — наблюдалось в официальных материалах.
- `PROPOSED` — наш локальный эквивалент или улучшение.
- `OPEN` — требует ручной проверки аккаунтного UI.
- `P0` — основа MVP; без неё нет пригодного редактора.
- `P1` — функциональная полнота первой стабильной версии.
- `P2` — расширение после доказанной основы.

## 1. Локальная библиотека карт

| Возможность | Приоритет | Требуемое поведение |
|---|---:|---|
| Быстрое создание | P0 | тип карты, размер/ориентация, единицы, фон, сетка, пресет |
| Недавние карты | P0 | локальные миниатюры, путь, дата изменения, статус recovery |
| Открыть файл | P0 | `.mapproj`, понятная ошибка, безопасная миграция копии |
| Поиск и сортировка | P1 | имя, тип, дата, размер; сортировка без изменения файлов |
| Шаблоны | P1 | создать независимую копию из локального шаблона |
| Клонировать | P0 | копия проекта с новым documentId и названием |
| Удалить | P1 | подтверждение и предпочтительно корзина Windows |
| Импорт backup | P1 | предварительная проверка структуры и происхождения ассетов |
| Справка/уроки | P1 | локальные документы/видео-ссылки; отсутствие сети не ломает UI |

Сетевые блоки публичного dashboard Inkarnate (Marketplace, Community, профиль) не копируются. Их место в компоновке не резервируется.

## 2. Каркас редактора

### Постоянные области

- главное меню: новый/открыть/сохранить/сохранить как/экспорт/настройки/выход;
- undo/redo, история и статус сохранения;
- вертикальная панель инструментов;
- контекстная панель выбранного инструмента/объекта;
- холст;
- правая область «Объекты / Слои / Ассеты / Свойства»;
- строка состояния: масштаб, координаты, единицы, autosave, подсказка текущего действия.

### Общие взаимодействия (`P0`)

- pan: `Space + drag`, средняя кнопка и отдельный инструмент;
- zoom колёсиком вокруг курсора, Fit, 100%, заданный процент;
- отмена/повтор транзакций;
- копировать/вырезать/вставить/дублировать;
- временные направляющие, snap и numeric editing;
- единый контекстный inspector вместо дублирующихся несовместимых настроек.

## 3. Select / Transform (`P0`)

- клик, рамка и `Shift`-множественный выбор;
- фильтр выбора по типу объекта, слою и sublayer;
- move, scale, rotate, flip H/V через рамку и числовые поля;
- preserve aspect ratio, pivot/anchor;
- порядок вперёд/назад и на верх/низ в пределах слоя;
- перенести в другой Object layer;
- group/ungroup, select group contents;
- hide/lock/delete/duplicate;
- поиск и выбор из дерева объектов;
- новая вставка наследует настройки последнего выбранного объекта только при включённой опции «синхронизировать инструмент».

## 4. Land (`P1`, после ядра кисти)

Предметный инструмент для земли/континентов. В отличие от обычной маски имеет стили края.

- add/subtract;
- круглая кисть с size и edge roughness;
- rectangle, ellipse, polygon, grid-block;
- bucket fill ограниченной области;
- move selected land area; поворот — наше предложенное расширение `P2`;
- несколько независимых Land layers;
- land style: stroke/outline, outer/inner shadow, ripple/coast effects;
- grid-aware режим;
- быстрый preview без разрушения базовой Brush layer.

Инструмент не должен искусственно исчезать из произвольного пресета: пресет может скрыть его из default toolbar, но пользователь включает его сам.

## 5. Brush (`P0` для базовой кисти, `P1` для расширенной)

### Материал

- texture, solid color, eraser;
- каталог текстур, recent/favorite;
- hue, saturation, brightness, contrast;
- texture scale, rotation, offset, opacity.

### Геометрия

- round soft/hard и несколько встроенных presets;
- size, opacity, softness;
- edge-block, grid-block, rectangle, ellipse, polygon;
- отдельная активная Brush layer.

### Расширенная кисть

- spacing, scatter;
- size jitter и opacity jitter;
- pressure → size/opacity/softness;
- включаемая pen dynamics;
- живое превью отпечатка;
- сохранение собственного preset без встраивания чужого материала.

Один непрерывный штрих — одна undo-транзакция. Растровое состояние хранится тайлами, а не полным снимком всего холста на каждое движение.

## 6. Stamp / Asset (`P0` базовый, `P1` расширенный)

- выбрать единичный ассет или логический набор с одним/десятками упорядоченных вариантов;
- раскрыть варианты, выбрать cover/current, перейти к предыдущему/следующему и включить
  случайный следующий вариант после размещения;
- place click/drag, auto-select после размещения;
- scale, rotation, sublayer, flip, opacity, blend, blur;
- hue/saturation/brightness/contrast для поддерживаемых ассетов;
- shadow: none, object/custom, layer;
- random asset from set;
- random rotation с диапазоном/шагом;
- random horizontal/vertical flip;
- area scatter: область, density, size/angle/flip randomization;
- replace asset у существующего объекта с сохранением трансформации;
- recent/favorite;
- предупреждение о missing/unlicensed asset без уничтожения объекта.

Набор является сущностью локального каталога, но каждая размещённая копия сохраняет конкретный
SHA-256 варианта. Случайность разрешается до создания команды: renderer, reopen и undo/redo не
выбирают вариант заново. Один Many/Area gesture с разными variants остаётся одной транзакцией.

Панель выделенного stamp обязана давать точные controls для каждого перечисленного свойства, а не
фиксированные demo-кнопки. Основной блок: replace asset, пропорциональный scale плюс точные W/H,
rotation, sublayer, оба flip и opacity. Расширенный блок: shadow preset/custom, blend, blur и
Hue/Saturation/Brightness/Contrast с отдельным reset. Полная матрица и acceptance:
`research/OBJECT_AND_GRID_PARITY.md`.

## 7. Line (`P1`)

Единый инструмент покрывает дороги, стены, границы, реки и маршруты. Отдельный legacy Path не создаётся.

- free draw и pen/node mode;
- stroke как цвет или повторяемый собственный asset;
- width, opacity, blend, blur, sublayer;
- hue/saturation для asset stroke;
- offset, end caps, node caps, shadow;
- straight/curved nodes, add/move/delete/cut node;
- guides и snap;
- portals — неразрушаемые разрывы линии для дверей/окон, с перемещением, шириной и удалением;
- optional clip-mask stroke для взаимодействия с ближайшей или выбранной Brush layer (`P2`, только после ясной семантики).

## 8. Shape (`P1`)

- free draw, pen, brush, rectangle, ellipse, polygon;
- editable nodes и portals;
- add/subtract/merge boolean operations;
- stroke использует Line engine;
- fill: solid color или texture;
- fill scale/rotation/offset/opacity;
- overall opacity/blend/blur;
- построение помещений, зон и подложек без растрирования до явного flatten.

Fill color, stroke color/width и общий opacity/blend/blur доступны как фактические значения в
inspector и меняются undoable-командами; одного циклического preset недостаточно.

## 9. Text (`P1`)

- multiline text и resize editor;
- font family, weight/bold, size;
- fill, outline color/thickness, shadow;
- rotate, scale, flip, sublayer;
- curve, letter spacing, line height;
- horizontal alignment;
- opacity, blend, blur;
- новый текст может наследовать настройки последнего выбранного текста;
- шрифты должны иметь известную лицензию и встраиваемость; отсутствующий шрифт не должен ломать файл.

## 10. Notes (`P1`)

- заметка с привязкой к координате или объекту;
- заголовок и rich text: headings, списки, bold/italic, ссылки;
- список, поиск, перейти к заметке, edit/delete;
- заметки по умолчанию не попадают в экспорт изображения;
- локальный Markdown-поднабор вместо скрытого облачного HTML.

## 11. Слои и дерево объектов (`P0`)

Типы слоя:

- `ObjectLayer`: stamp/text/line/shape/group;
- `BrushLayer`: растровая живопись;
- `LandLayer`: маска земли + edge style;
- `FilterLayer`: неразрушающий эффект нижележащей композиции;
- `GridLayer`: одна или несколько сеток;
- `BaseColorLayer`: обязательный фон, не удаляется.

Общее поведение:

- add/rename/reorder/duplicate/hide/lock/delete;
- новый/дублированный слой появляется над активным;
- opacity и blend mode уровня слоя;
- Object layer: layer shadow, clipping mode, sublayer visibility/lock;
- add/copy/hide/delete mask;
- поиск объектов, select all, per-object hide/lock;
- stable layerId/objectId;
- `flatten` — явная необратимая вектор→растр операция с предупреждением и undo; исходная редактируемость сохраняется до подтверждения.

Публичный обзор показывает, что слои Inkarnate нельзя выбирать группой, связывать или сливать. Это не обязательное ограничение для нас, но folder/group layers остаются `P2` после базовой совместимости.

## 12. Маски (`P1`)

- маска принадлежит конкретному слою;
- white = visible/opaque, black = hidden/transparent;
- красный overlay только для визуализации редактируемой маски;
- brush add/subtract, softness, shapes, pen pressure;
- маска влияет только на владельца, не на соседние слои;
- copy mask to another compatible layer;
- disable mask без потери данных;
- маски для Object, Brush, Grid и совместимых Filter layers;
- несколько масок/стек масок — `P2`, если performance spike подтвердит.

## 13. Grid (`P1`)

- видимые presets: square, diagonal diamond (`Square` + rotation `45°`), hex flat и hex pointy;
- размер ячейки, subdivisions, line width/color/opacity;
- offset и rotation;
- выше/ниже других слоёв;
- hide/lock;
- layer mask для локального скрытия сетки;
- snap объектов, узлов, portals и land/brush grid-block;
- экспорт с сеткой или без неё независимо от видимости редактора.

## 14. Filters (`P1` базовые, `P2` дорогие)

- color adjustment: brightness/contrast, saturation, hue, temperature;
- texture overlay;
- vignette, blur/sharpen после измерения;
- opacity/blend и mask;
- reorder/duplicate;
- интерактивный preview с LOD; финальный точный рендер при export.

## 15. Каталог и Asset Manager (`P1`)

- собственное дерево collections/packs с counts и breadcrumbs;
- virtualized thumbnail grid, регулируемый размер карточек и bounded decode/cache;
- поиск, name/date sort, grouping по collection/tag и составные pack/tag/type/license/date/
  used-in-map filters;
- recent и set-level favorite;
- logical `AssetSet` с badge количества, inline/detail раскрытием и ordered `AssetVariant`;
- single import → singleton set; multi-import → новый set с rename/reorder/cover без удаления
  исходного content;
- built-in own pack, user packs и document-embedded assets;
- импорт PNG/JPEG/WebP/SVG только после безопасной валидации; SVG — ограниченный профиль;
- прозрачность, thumbnail, default physical/grid scale, anchor, tags;
- массовый импорт с одинаковой калибровкой масштаба;
- содержание «My assets» — только локальные материалы пользователя;
- missing asset resolver: найти файл, заменить, оставить placeholder, удалить осознанно.

Полный clean-room контракт каталога и set placement:
`research/ASSET_CATALOG_AND_VARIANT_SETS.md`.

## 16. Compositions (`P1`)

- выделенные объекты можно сохранить как локальную композицию;
- сохранить relative transforms, bounds, anchor и reference grid scale;
- вариант editable composition и вариант flattened raster stamp;
- экспорт/импорт пакета композиции с manifest и provenance;
- copy/paste между открытыми картами сохраняет прозрачность и структуру, когда форматы совместимы;
- фон, grid и filter включаются только явными флажками.

## 17. History, autosave, checkpoints, recovery (`P0`)

- undo/redo по пользовательским транзакциям;
- панель истории текущей сессии;
- именованный checkpoint перед крупным экспериментом;
- autosave on/off с явным статусом и безопасным интервалом;
- manual save (`Ctrl+S`);
- recovery journal вне основного `.mapproj`;
- backup copy before migration/replace;
- при старте после сбоя: сравнить основной файл, temp и recovery, показать понятный выбор;
- ошибки сохранения не должны закрывать документ и не должны ложно показывать `Saved`.

## 18. Изменение размера и извлечение региона (`P1`)

- увеличить/уменьшить canvas с выбором anchor;
- crop и expand с предпросмотром;
- snap boundaries to grid;
- «новая карта из региона» создаёт независимый документ с нужными пересечениями объектов;
- режимы для объектов за новой границей: keep hidden, clip, delete copy-only.

## 19. Экспорт (`P0` базовый, `P1` расширенный)

- PNG с прозрачностью, JPEG, WebP;
- размер в пикселях, scale %, DPI metadata;
- до 16K по длинной стороне в первой стабильной версии; больший результат — тайловый/региональный
  export с явной оценкой размера и без скрытого full-canvas allocation;
- экспорт всей карты или выделенного региона;
- флажки grid/notes/background;
- offscreen tiled rendering, прогресс и отмена;
- финальный результат соответствует preview в пределах определённого color pipeline;
- ошибка памяти объясняется и предлагает меньший размер/тайловый режим;
- экспорт не изменяет документ.

### Large map / deep zoom (`P0` архитектура, `P1` полный gate)

- логический размер документа не равен размеру bitmap редактора или export;
- viewport рисует только видимые spatial chunks/tiles и выбирает mip/LOD по масштабу;
- vector/shape/text остаются редактируемыми и уточняются при zoom без преждевременного flatten;
- raster/asset detail ограничен исходными данными; UI не маскирует пикселизацию как «HD»;
- background loading, decode и cache имеют bounded parallelism и RAM/VRAM budgets;
- сохранение/undo затрагивают только изменённые chunks/tiles, а не snapshot всей площади карты;
- экспорт региона и пакет тайлов позволяют получить результат больше 16K без единой огромной
  поверхности в памяти.

## 20. Настройки (`P1`)

- тема, язык, UI scale;
- configurable shortcuts с конфликтами и reset;
- autosave interval и recovery budget;
- canvas performance mode и GPU/CPU diagnostics;
- пути библиотеки и cache;
- default export;
- pen/tablet diagnostics.

## 21. Локальные улучшения относительно наблюдаемого поведения

Это не обязательный паритет и реализуется только после P0/P1:

- редактируемые shortcuts;
- folder/group layers;
- rotate Land selection;
- независимое массовое изменение только выбранного свойства без сброса других параметров;
- явные tooltips для всех icon-only controls;
- понятная семантика clip masks;
- неразрушающий flatten через скрытый source snapshot в пределах undo budget.

## 22. Открытые вопросы ручного аккаунтного аудита

- точный сценарий текущего create-map wizard и dashboard 2026;
- клавиатурные сочетания и контекстные меню текущей сборки;
- точная семантика clipping modes и взаимодействия Line/Shape fill с Brush layers;
- список blend modes и порядок вычисления shadows/filters;
- экспортные ограничения текущих тарифов нужны только как конкурентный контекст, не как наши лимиты;
- актуальное поведение Windows-версии Inkarnate и различия с браузером.
