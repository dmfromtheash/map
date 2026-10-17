# 05. Формат проекта и модель данных

## Цели

- один переносимый файл проекта;
- без обязательной локальной БД;
- безопасное восстановление и миграции;
- стабильные ID и детерминированная сериализация;
- поддержка linked и embedded собственных ассетов;
- возможность получить диагностику структуры без запуска renderer.

## Текущая схема — раскраска Земли, schema v9 (2026-09-08)

Writer выбирает максимальную необходимую схему по основному документу и всем именованным версиям:

| Содержимое | Схема |
|---|---|
| Есть раскраска Земли | v9 |
| Иначе есть дороги | v8 |
| Иначе есть реки/озёра | v7 |
| Иначе есть редактируемые области | v6 |
| Иначе есть пути расстановки | v5 |
| Нет перечисленных конструкций | v4 |

Reader напрямую принимает v4–v9; v9 — максимальная поддерживаемая схема текущей рабочей копии.

У слоя `Land` необязательный `landPaintTiles` в `scene.json` перечисляет тайлы раскраски.
Их байты лежат в `rasters/land-paint/{layerId}/{x}_{y}.bin`: 256×256 RGBA premultiplied,
4 байта на пиксель, тот же ограниченный сырой формат, что у растровой кисти. Отсутствующее
поле означает пустую раскраску; `tiles` формы Земли по-прежнему содержит одноканальное
покрытие. Чтение проверяет тип владельца, схему, размеры, уникальность и общий бюджет тайлов.
Именованные версии используют ту же структуру под собственным checkpoint-префиксом;
контейнер получает v9, даже если краска сохранилась только в одной такой версии.
Производные изображения предпросмотра не заменяют эти исходные пиксели. Маски слоя и
вырезанные водой берега применяются при рисовании и не запекаются в RGBA-раскраску.

### Дороги, schema v8

Контейнер с дорогами хранит `roads` у объектного слоя: ID/имя сети, общие узлы, участки с
ссылками на узлы и шириной/кривизной вершин, замкнутость, общий и отдельный стиль, уровень,
видимость, блокировку, `widthScale` и `seed`. Производные контуры и кэши в файл не записываются.
Необязательный `surfaceOrder` задаёт порядок ID дорог и воды внутри слоя; порядок по умолчанию
восстанавливается при отсутствии явной последовательности. Пять дефектов дорог из проверки
08.09 исправлены по DEC-034; этап остаётся PARTIAL по [handoff](STATE_HANDOFF.md).
Успешный round-trip формата не доказывает полную техническую или художественную приёмку.

Разделы ниже описывают расширения в момент введения соответствующего этапа. Условия выбора
версии применяются с приоритетом таблицы выше; старые указания максимальной версии не действуют.

## Stage 19 — реки, schema v7 (2026-09-07)

При отсутствии раскраски Земли и дорог writer выбирает v7, если документ или именованная версия содержит реку;
иначе действует выбор v4/v5/v6. Reader открывает v4–v9 через общий object-chunk
контейнер; старые файлы не переписываются. В `scene.json` у слоя типа `Object` появляется
необязательный массив `rivers`; каждая река: `id`, `name`, `channels[]` (`id`, `name`,
`nodes[]` из `id`, `x`, `y`, `width`, `sharp`, необязательное `mouth` с `parentChannelId`,
`parentNodeId`, `fraction` 0..1), `style` (`waterColor`, `opacity`, `bankColor`, `bankWidth`,
`bankShadowOpacity`, `bankShadowBlur`, `highlightColor`, `highlightOpacity`, `taperSource`,
`mouthFade`), `widthScale`. Координаты узлов — мировые единицы холста; сохраняются только
входы, контур и стык вычисляются при чтении. Река в слое другого типа или в контейнере ниже v7
отвергается как повреждение; snapshot v7 требует контейнер не старше v7. Диагностика файла
принимает v9 как максимальную поддерживаемую схему. Доработка 2026-09-07 добавляет в русло
необязательные `source` (той же формы, что `mouth`) и `closed` (озеро), а в стиль — `edgeRoughness`,
`cutsLand`, `clipToLand`; ранние v7-файлы без этих полей читаются со значениями по умолчанию
(нет истока, не озеро, 0, true, true). Схема остаётся v7.

## Stage 18 — области, schema v6 (2026-09-06–07)

Writer выбирает v6, если основной документ или хотя бы одна именованная версия содержит область.
Без областей сохраняется прежний выбор v4/v5. Reader открывает v4/v5/v6 через общий object-chunk
маршрут; исходные v4/v5 не требуют миграционной перезаписи. V6 нужен, чтобы старый reader явно
отклонил неизвестную сохраняемую сущность. Миграции v1–v3 к v4 и атомарное сохранение сохранены.

Необязательное поле `placementAreas` объектного слоя содержит ID, имя, source set/variant IDs,
упорядоченные вершины со стабильными ID, плотность, seed, request, scale/rotation/sublayer/opacity,
StampStyle, замороженные content hashes/размеры вариантов и масок, MemberCount, Cells, Exclusions,
LayoutVersion, OccupiedSlots, CompositionOrigin, CompositionLength, CompositionAngleDegrees,
Pattern, JoinEdges, SizeVariationPercent, ContentBounds и ContentMasks. ContentBounds хранит четыре
нормализованные координаты видимой alpha-области для каждого известного SHA; проверяются
конечность, диапазон 0–1, положительные размеры и принадлежность сохранённым вариантам.
ContentMasks хранит 32 строки uint (силуэт 32×32, 128 байт данных) на SHA источника;
проверяются длина, непустота и принадлежность сохранённому варианту. Это общие immutable данные,
не копия изображения для каждого объекта. При отсутствии метаданных используется прямоугольник.

LayoutVersion=4 задаёт структурную композицию (опорные позиции, окружение и края), 3 сохраняет
первый расчёт природных групп, 2 — компактный отбор по прямоугольникам, 1 — первоначальный
отбор с промежутком 6%; отсутствующее поле/0 сохраняет раннюю россыпь. Для версий 3/4 плотность
ограничена 0–100%, разброс размера — 0–50%, длина/угол/центр рисунка конечны. Pattern поддерживает
Masses, Ridge, Groups, Clearings; смысл выбирает пользователь, распознавания содержимого ассета нет.
Прежние версии расчёта не меняются при открытии. Неизвестная версия явно отклоняется.
Версия 4 без ContentMasks сохраняет расстановку по прямоугольникам; наличие замороженных
силуэтов включает плотное смыкание и привязку размеров гряды к подножиям. Чтение само не
измеряет и не дописывает силуэты. Центр каждого сохранённого слота остаётся центром изображения.

JoinEdges по умолчанию false при чтении старого файла; для новой области UI включает его.
Сохраняется конкретный порядок SceneObject, в том числе порядок перекрытия от задних к передним.
Производный persistent индекс этого порядка строится при чтении и не попадает в JSON.
OccupiedSlots хранит места, пропущенные из-за посторонних штампов, отдельно от ручных Exclusions;
оба набора учитываются в проверке полноты. Отделённые объекты остаются обычными объектами.

Ключ Cells — 64-битная упаковка двух знаковых координат ячейки; член содержит полный слот X/Y/Ordinal
и детерминированный ObjectId. Exclusions относятся к постоянным слотам, в том числе за текущей
границей и выше текущей плотности. Пиксели не дублируются: обычные object chunks ссылаются на
content-addressed assets. После открытия штампы не генерируются заново.

Ручное удаление, геометрическая правка и перенос выбранных членов области отделяют их в той же
транзакции: Cells/MemberCount уменьшаются, исходные слоты добавляются в Exclusions. Остальные
члены и слотные ID не меняются. Для этого не добавляются поля или новая schema: штатный v6
round-trip сохраняет ручные исключения, в том числе после изменения плотности.

Проверяются простая невырожденная геометрия, конечные параметры, полнота активных слотов,
счётчик членов, детерминированные ID, уникальность владения между путями/областями, принадлежность
слою/подуровню, положение центра, exclusions и ссылки на все сохранённые изображения/маски.
Область запрещена в слое другого типа и контейнере v4/v5. Snapshot v6 требует контейнер не старше
v6; путь и его v5 snapshot допустимы внутри v6. Даже после преобразования основной области в
обычные объекты её сохранённая именованная версия удерживает v6 и метаданные управления.

## Stage 17 — сохраняемые пути, schema v4/v5 (2026-09-06)

Writer выбирает v5, когда основной документ или хотя бы одна именованная версия содержит путь.
Без путей writer сохраняет v4. Reader открывает обе версии напрямую; исторические миграции
v1–v3 по-прежнему ведут к v4. Преобразование существующего v4-файла для чтения не требуется,
исходный файл не перезаписывается. V5 использует тот же ZIP, object chunks, assets, hashes и
атомарную замену. Старый reader отклоняет v5 явно, чтобы не потерять управление рядом.

В JSON объектного слоя добавлен необязательный `placementPaths`. Запись `PlacementPath` содержит:
ID/имя, исходные ID набора и варианта для происхождения, замороженные content hashes и размеры
всех разрешённых вариантов/масок, узлы со стабильными ID, шаг, seed, request, scale/rotation/style,
sublayer/opacity, точные пары `slot → ObjectId` и постоянные `Exclusions`. Слот — ID начального
узла сегмента плюс порядковый номер штампа на сегменте. Пиксели хранятся один раз в assets.

Управляемые штампы по-прежнему записываются в object chunks; при открытии они не генерируются
повторно. Проверяются уникальные path/member ID, полнота слотов с учётом шага и исключений,
детерминированный ObjectId, принадлежность stamp своему слою и наличие всех ссылок на assets.
Путь запрещён в слое другого типа и в scene v4. Snapshot версии проверяется аналогично;
snapshot v5 требует контейнер v5. Неизвестные данные не превращаются молча в обычный ряд.

Удаление/перенос отдельного управляемого штампа, группировка или изменение его вида требуют
предварительного отделения. Копия выбранных штампов получает новые ID и является обычными
объектами; исходное владение сохраняется. Дублирование целого слоя с путями, resize/crop документа
явно требуют сначала превратить пути в обычные объекты. Перестановка/видимость/lock слоя,
именованные версии и удаление целого слоя сохраняют естественную семантику владельца слоя.

## Контейнер `.mapproj` (`ACCEPTED BASELINE`)

ZIP-контейнер с canonical UTF-8 именами:

```text
manifest.json
scene.json
preview.png
objects/
  <object-layer-id>/<ordinal-8-digits>.json
assets/
  <sha256>.<ext>
asset-manifest.json
rasters/
  <layer-id>/<level>/<x>_<y>.png
fonts/                 optional, only embeddable licenses
checkpoints/           optional named baselines
```

Recovery journal и transient undo **не** хранятся внутри основного файла при каждом жесте.

## Manifest

Минимальные поля:

```json
{
  "format": "map-project",
  "schemaVersion": 4,
  "appVersion": "0.1.0",
  "documentId": "uuid",
  "title": "Untitled map",
  "createdUtc": "ISO-8601",
  "modifiedUtc": "ISO-8601",
  "canvas": {
    "width": 4096,
    "height": 4096,
    "unit": "px",
    "background": "#FFFFFFFF"
  },
  "preset": "blank",
  "sceneSha256": "...",
  "assetManifestSha256": "..."
}
```

Точные JSON-схемы создаются на отдельном этапе и golden fixtures сохраняются в tests.

## Сцена

В canonical schema v4 `scene.json` хранит только структуру документа и ordered descriptors
объектных чанков. Массив `objects` внутри слоя больше не записывается. Каждый `ObjectLayer`
ссылается на последовательность `objects/<layer-id>/<ordinal>.json`; порядок descriptors и
порядок элементов внутри каждого чанка вместе являются точным порядком объектов слоя.

Descriptor содержит `ordinal`, `count`, `byteLength`, `sha256`, conservative world `bounds`,
canonical `entry` и `representationVersion`. Writer ограничивает чанк одновременно 8 192
объектами и 8 MiB несжатого UTF-8 JSON, пишет его прямо в ZIP и считает размер/SHA-256 на потоке.
Reader до materialization проверяет metadata всего ZIP, descriptor, размер entry, count, SHA-256,
JSON depth/string budget и отсутствие незаявленных `objects/` entries. После преобразования
объектов фактические bounds обязаны помещаться в объявленные. Основная сцена и snapshot каждой
«Версии» используют один и тот же формат.

### Общие сущности

- `MapDocument`: metadata, canvas, ordered layers, notes, document settings.
- `Layer`: stable ID, type, name, order, visible, locked, opacity, blend, mask/effects.
- `SceneObject`: stable ID, type, layerId, sublayer, local transform, bounds, visible, locked, style.
- `Group`: children IDs и transform; отсутствие циклов — инвариант.
- `AssetRef`: content hash, source pack, embed/link mode, license/provenance ID.
- `AssetSet`/`AssetVariant` относятся к локальному каталогу, а не являются обязательной частью
  scene: объект stamp хранит конкретный content hash размещённого варианта.
- `GridDefinition`: geometry, cell size, offset, rotation, style, snap policy.
- `Note`: anchor kind/id/position и безопасный rich-text document.

### Типы объектов

- `StampObject`
- `LineObject`
- `ShapeObject`
- `TextObject`
- `CompositionInstance`
- `PlaceholderObject` для неизвестного/потерянного типа без удаления исходных данных.

## Порядок и композиция

Окончательный порядок задаётся:

1. канонический индекс слоя: слои `Land` всегда находятся ниже слоёв `Object`; внутри каждого
   типа сохраняется пользовательский порядок, а старый файл с обратной парой безопасно
   нормализуется при открытии;
2. sublayer (`-10..10` в первой совместимой модели; расширение возможно миграцией);
3. object order внутри sublayer;
4. child order группы.

На границе `Land`/`Object` объект всегда рисуется выше земли. Поднять `Land` над `Object` или
опустить `Object` под `Land` через команды порядка нельзя.

Blend, opacity, mask и filter вычисляются по документированному pipeline. Порядок не должен зависеть от порядка JSON-полей или обхода словаря.

## Raster tiles

- фиксированный tile size выбирается spike (кандидат 256/512 px);
- прозрачные пустые tiles не сохраняются;
- lossless PNG как безопасная база; другой codec — только после сравнения времени/размера;
- tile key включает layerId, coordinate и base resolution;
- checksums позволяют отличить corruption от отсутствующего tile;
- brush command хранит до/после только затронутых tiles или reversible stroke delta в пределах бюджета.

## Ассеты

### Локальный каталог

Каталог имеет собственную версионированную schema и не является источником истины для открытой
карты:

- `AssetContent`: SHA-256 identity, media/dimensions/alpha, stored/linked path, thumbnail и
  provenance;
- `AssetVariant`: stable ID, content SHA, label, anchor/default scale;
- `AssetSet`: stable ID, ordered variant IDs, cover variant, collection/tags/favorite;
- `AssetCollection`: stable ID, optional parent, display name/order;
- bounded local state: recent и last selected variant per set.

Старый плоский record мигрирует в singleton set. Миграция индекса записывает новый temp,
проверяет его и сохраняет backup; повреждённый единственный индекс не перезаписывается.

При размещении set сначала разрешается в конкретные content hashes. `.mapproj` сохраняет каждый
использованный `MapAssetReference` и каждый `StampStyle.AssetSha256`; renderer не зависит от
локальной Library и не выполняет random selection. Обратная связь «этот stamp принадлежит set»
может восстанавливаться по SHA, если каталог доступен, и не нужна для корректного render/export.

### Embedded

Плюсы: переносимость и воспроизводимость. Минусы: размер. Файл именуется по SHA-256, одинаковые данные дедуплицируются.

### Linked

Плюсы: маленький проект и общий каталог. Минусы: перенос пути. Хранятся content hash, относительный/catalog ID и fallback thumbnail; абсолютный путь не является единственной ссылкой.

При открытии: embedded bytes → catalog resolve → безопасный project-relative fallback →
placeholder. Catalog и fallback принимаются только после SHA-256 точных прочитанных байтов;
никакой тихой подмены несовпадающим файлом.

Rooted, drive-relative, root-relative, UNC, extended/device и ADS-представления не допускаются.
Старые синтаксически относительные пути с `.`/`..` сохраняются в модели, но автоматический
fallback не открывает результат за пределами папки проекта. Внешний linked import остаётся
доступен по catalog/content identity; отсутствие локального каталога даёт placeholder.

## Asset provenance

Для каждого ассета:

- source type: created/user-imported/pack;
- original filename только как display metadata;
- author/owner, license ID/text/URL, commercial-use flag;
- importedUtc;
- SHA-256;
- modifications/derived-from при наличии;
- default scale/anchor/tags.

Если происхождение неизвестно, `licenseStatus = unknown`, и такой ассет нельзя включить в стандартный pack без решения владельца.

## Сохранение

```text
serialize immutable snapshot
→ write to unique sibling temp or safe local temp
→ validate ZIP entries + manifest + checksums
→ flush file buffers
→ replace target atomically where supported
→ keep bounded .bak
→ mark revision saved only after success
```

Если replace не удался, документ остаётся dirty; temp/recovery путь показывается пользователю.

## Autosave и recovery

Primary autosave после первого явного Save использует тот же atomic `MapProjectStore.Save`, тот же
immutable revision checkpoint и общий save gate. Idle debounce равен 4 секундам; изменение во
время I/O остаётся dirty и сохраняется следующим проходом. Эта операция обновляет текущий
`.mapproj`, но не создаёт новый DTO и не меняет schema.

- журнал находится в `%LOCALAPPDATA%\Map\Recovery\<documentId>`;
- содержит base file fingerprint, base saved revision и последовательность durable deltas/snapshots;
- запись throttled и выполняется в фоне;
- после clean save старые recovery records компактируются;
- после clean close временный журнал удаляется только после доказанного save или явного discard;
- при сбое пользователь видит timestamp и preview recoverable state.

## Checkpoints

В UI checkpoints называются «Версии». One-click создание даёт автоматическое имя, local time и
preview; старые пользовательские имена открываются без миграции. Optional rename является
отдельной undoable command. Сам checkpoint остаётся durable snapshot/delta baseline; порядок,
лимиты, preview и restore/delete совместимы с существующим контейнером.

## Миграции

- базовый контейнер и цель миграции старых файлов — `v4`, расширения конструкций используют
  v5–v9 по таблице выше; реальные исторические `v1`–`v3` открываются только через
  явную новую migration-copy, schema `v0` в истории проекта нет;
- обычный v4 open требует явные layer types, обязательные collections, единственный BaseColor и
  canonical independently verified object chunks;
- `schemaVersion` изменяется только при несовместимом формате;
- миграции последовательные `vN → vN+1`;
- неизвестные поля v1 сохраняются в созданной v2-копии на том же stable root/entity;
- неизвестный объект становится видимым placeholder с исходным kind и raw JSON до 256 KiB UTF-8;
- migration test использует fixtures всех поддерживаемых версий;
- source file всегда read-only и не меняет bytes/timestamp; destination обязан отличаться и не
  существовать, а окончательная замена выполняется только после полного v4 read-back;
- adjacent `v3→v4` переносит main scene и checkpoint snapshots в chunk storage, сохраняя точный
  object order/ID и raw JSON неизвестных объектов. Source bytes/timestamp не меняются.

## Лимиты чтения контейнера

Локальный/offline файл является внешней границей данных: он может быть повреждён, ошибочно
назван или специально сформирован. До полной распаковки reader проверяет metadata всех ZIP
entries, canonical archive names, регистронезависимые дубликаты, per-entry/category/total budget
и compression ratio. Archive entry никогда не извлекается; для ZIP-имён `..`, абсолютные пути,
backslash, `:` и пустые сегменты всегда являются corruption. Это отдельное правило от linked
filesystem fallback выше.

Текущий envelope: DEC-015/021/022, с разделением категорий по прямому техническому поручению
владельца от 2026-09-05 (уточнение DEC-022 ниже). Все значения — несжатые bytes, кроме
явно указанных ZIP file/central directory. Совокупные лимиты применяются одновременно.

| Ресурс | Лимит |
|---|---:|
| ZIP entries | 16 384 |
| ZIP file | 1 280 MiB |
| central directory / совокупные имена entries | 16 MiB / 2 MiB |
| одно имя entry | 512 bytes |
| один structural JSON entry | 32 MiB |
| structural JSON aggregate, включая checkpoint metadata | 64 MiB |
| один schema-v4 object chunk | ≤8 192 объектов и ≤8 MiB |
| v4 object chunks aggregate, включая checkpoint roots | 960 MiB |
| один legacy scene.json при чтении/миграции | 512 MiB |
| исходный migration payload aggregate | 768 MiB |
| один raw tile | 256 KiB |
| один asset entry | 64 MiB |
| preview entry | 8 MiB |
| assets aggregate | 512 MiB |
| tiles aggregate | 1 024 MiB |
| JSON aggregate | 1 024 MiB |
| total uncompressed | 2 048 MiB |
| compression ratio | 1000 + 1 MiB additive slack на entry |
| preview | ≤8192 по оси и ≤16 MiPixels |
| semantic layers | 4 096 |
| scene + composition objects | нет фиксированного count-limit; действует byte/depth envelope |
| notes | нет фиксированного count-limit; действует byte/depth envelope |
| asset DTOs | 16 384 aggregate |
| vector nodes/regions/portals | нет фиксированного count-limit; действует byte/depth envelope |
| одна JSON string | 1 MiB UTF-8 |
| JSON tokens | нет отдельного count-limit; действует byte/depth envelope |

`ReadEntry` выделяет ровно заявленный размер, использует exact read и проверяет отсутствие
лишних decompressed bytes. Уже проверенные asset-массивы передаются документу по внутреннему
ownership-transfer пути без второй полной копии; публичный конструктор остаётся defensive.
До materialization DTO streaming `Utf8JsonReader` проверяет глубину, строки и сохраняемые
структурные пределы. Cardinality пользовательского содержимого не ограничивается отдельной
произвольной цифрой; память по-прежнему ограничена размером чанка/entry/container. Schema v4
устраняет единый object DTO/UTF-8 graph: writer и reader удерживают не больше одного bounded
object chunk, а документ накапливается через immutable chunked `SceneObjectList`.
Превышение envelope — `MapProjectResourceLimitException` с гарантией «файл не изменён», а не
ложное заявление о повреждении v1-проекта.

Сумма structural/object envelopes не увеличивает прежний JSON aggregate 1 024 MiB. Большой
legacy scene не становится разрешением записывать structural JSON такого размера в v4.
Метаданные ZIP проверяются до чтения payload; header inspection допускает legacy entry только
для определения schema, затем реальная категория и migration envelope проверяются отдельно.
Лимиты checkpoints остаются 20 версий / 64 MiB совокупных snapshot data. Object chunks и tiles
версий пока лежат под отдельными roots; общие source bytes в памяти разделяются, а контейнер
может повторять одинаковые данные. Это ограниченный рост, не неограниченный журнал.

Writer использует ZIP Create, полный семантический read-back и atomic publication. Это полная
перезапись, не incremental save. JSON и raw tiles используют Optimal; PNG/JPEG/WebP и previews
пишутся без повторного Deflate. Качество и source bytes не меняются. Уже сериализованный один
object JSON не валидируется второй раз внутри Utf8JsonWriter; независимый read-back остаётся.

Migration payload держит открытый source ZIP и читает неизменённые entries по требованию;
изменённые entries принадлежат payload до Dispose. Main/checkpoint legacy scenes преобразуются
по очереди. Legacy JSON DOM и итоговый документ всё ещё могут сосуществовать: это уменьшение
повторного удержания, а не полностью потоковый legacy parser. Проверенная копия публикуется
после полного v4 read-back; повторное открытие опубликованной копии для возврата того же
результата убрано. Исходник не изменяется.
