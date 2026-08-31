# 05. Формат проекта и модель данных

## Цели

- один переносимый файл проекта;
- без обязательной локальной БД;
- безопасное восстановление и миграции;
- стабильные ID и детерминированная сериализация;
- поддержка linked и embedded собственных ассетов;
- возможность получить диагностику структуры без запуска renderer.

## Контейнер `.mapproj` (`ACCEPTED BASELINE`)

ZIP-контейнер с canonical UTF-8 именами:

```text
manifest.json
scene.json
preview.png
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
  "schemaVersion": 1,
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

1. индекс слоя;
2. sublayer (`-10..10` в первой совместимой модели; расширение возможно миграцией);
3. object order внутри sublayer;
4. child order группы.

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

Если происхождение неизвестно, `licenseStatus = unknown`, и такой ассет нельзя включить в распространяемый pack без решения владельца.

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

- журнал находится в `%LOCALAPPDATA%\Map\Recovery\<documentId>`;
- содержит base file fingerprint, base saved revision и последовательность durable deltas/snapshots;
- запись throttled и выполняется в фоне;
- после clean save старые recovery records компактируются;
- после clean close временный журнал удаляется только после доказанного save или явного discard;
- при сбое пользователь видит timestamp и preview recoverable state.

## Checkpoints

Именованный checkpoint — не просто запись undo, а durable snapshot/delta baseline с названием, временем и preview. Восстановление checkpoint создаёт undoable транзакцию или новую копию документа; история не уничтожается тихо.

## Миграции

- текущая каноническая schema — `v2`; реальный исторический `v1` открывается только через явную
  новую migration-copy, schema `v0` в истории проекта нет;
- обычный v2 open требует явные layer types, обязательные collections и единственный BaseColor;
- `schemaVersion` изменяется только при несовместимом формате;
- миграции последовательные `vN → vN+1`;
- неизвестные поля v1 сохраняются в опубликованной v2-копии на том же stable root/entity;
- неизвестный объект становится видимым placeholder с исходным kind и raw JSON до 256 KiB UTF-8;
- migration test использует fixtures всех поддерживаемых версий;
- source file всегда read-only и не меняет bytes/timestamp; destination обязан отличаться и не
  существовать, а публикация выполняется только после полного v2 read-back.

## Лимиты чтения контейнера

Локальный/offline файл является внешней границей данных: он может быть повреждён, ошибочно
назван или специально сформирован. До полной распаковки reader проверяет metadata всех ZIP
entries, canonical archive names, регистронезависимые дубликаты, per-entry/category/total budget
и compression ratio. Archive entry никогда не извлекается; для ZIP-имён `..`, абсолютные пути,
backslash, `:` и пустые сегменты всегда являются corruption. Это отдельное правило от linked
filesystem fallback выше.

Текущий принятый envelope (DEC-015):

| Ресурс | Лимит |
|---|---:|
| ZIP entries | 16 384 |
| один JSON entry | 32 MiB |
| один raw tile | 256 KiB |
| один asset entry | 64 MiB |
| preview entry | 8 MiB |
| assets aggregate | 256 MiB |
| tiles aggregate | 256 MiB |
| JSON aggregate | 64 MiB |
| total uncompressed | 512 MiB |
| compression ratio | 1000 + 1 MiB additive slack на entry |
| preview | ≤8192 по оси и ≤16 MiPixels |
| semantic layers | 4 096 |
| scene + composition objects | 100 000 aggregate |
| notes | 10 000 |
| asset DTOs | 16 384 aggregate |
| vector nodes | 500 000 aggregate |
| одна JSON string | 1 MiB UTF-8 |
| JSON tokens | 8 000 000 на entry |

`ReadEntry` выделяет ровно заявленный размер, использует exact read и проверяет отсутствие
лишних decompressed bytes. Уже проверенные asset-массивы передаются документу по внутреннему
ownership-transfer пути без второй полной копии; публичный конструктор остаётся defensive.
До materialization DTO streaming `Utf8JsonReader` считает все известные массивы (`layers`,
`objects`/`items`, `notes`, `assets`, `nodes`, `regions`, `portals`, `tiles`, `grids`, `filters`),
строки и tokens. Поэтому миллионы коротких `{}` не превращаются в многократно больший managed
object graph внутри формально допустимого byte budget.
Превышение envelope — `MapProjectResourceLimitException` с гарантией «файл не изменён», а не
ложное заявление о повреждении v1-проекта.
