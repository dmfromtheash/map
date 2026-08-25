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

### Embedded

Плюсы: переносимость и воспроизводимость. Минусы: размер. Файл именуется по SHA-256, одинаковые данные дедуплицируются.

### Linked

Плюсы: маленький проект и общий каталог. Минусы: перенос пути. Хранятся content hash, относительный/catalog ID и fallback thumbnail; абсолютный путь не является единственной ссылкой.

При открытии: hash match → catalog resolve → relative path → user resolver → placeholder. Никакой тихой подмены несовпадающим файлом.

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

- `schemaVersion` изменяется только при несовместимом формате;
- миграции последовательные `vN → vN+1`;
- неизвестные поля сохраняются, где это безопасно;
- неизвестный объект загружается placeholder с raw payload;
- migration test использует fixtures всех поддерживаемых версий;
- source file не перезаписывается до успешной проверки результата.

## Лимиты безопасности

До чтения содержимого проверять количество ZIP entries, суммарный/распакованный размер, compression ratio, image dimensions, path traversal и дубликаты canonical names. Конкретные лимиты задаются после performance tests и показываются понятной ошибкой.
