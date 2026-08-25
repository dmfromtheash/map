# 08. QA и приёмка

## Принцип

Качество редактора доказывается на четырёх уровнях: модель, файл, изображение и настоящее пользовательское окно. Ни один уровень не заменяет остальные.

## Пирамида проверок

### 1. Unit

- geometry, transforms, bounds и spatial queries;
- command do/undo/redo и coalescing;
- layer ordering/sublayers/groups;
- schema validation/migrations;
- asset hash/provenance/path rules;
- brush tile deltas;
- numeric properties и units.

### 2. Integration

- document → save → reopen → semantic equality;
- embedded/linked/missing assets;
- autosave/recovery/checkpoint;
- import limits и malicious ZIP/SVG fixtures;
- offscreen rendering pipeline;
- cancellation и error propagation.

### 3. Rendering golden tests

Маленькие детерминированные сцены для blend, mask, shadow, text, line caps/portals, shape boolean, grid и filters. Сравнение включает pixel tolerance, metadata renderer и утверждённое обновление golden image. Нельзя автоматически перезаписывать expected.

### 4. Desktop UI tests и ручные smoke

Automation пригодна для dashboard/dialog/inspectors, но canvas жесты и визуальная композиция дополнительно проверяются вручную. В отчёте фиксируются Windows scale, renderer и ввод.

## Обязательные сценарии P0

1. New → draw/place → save → close → reopen.
2. Undo/redo после transform и brush stroke.
3. Save failure: read-only/locked/full-disk simulation → данные остаются доступны.
4. Kill during temp write → предыдущий файл открывается; recovery предлагается.
5. Missing asset → placeholder, resolve, save/reopen.
6. Corrupted tile/manifest → конкретная диагностика, не crash.
7. 10 000 объектов → pan/zoom/select budget.
8. 4K transparent PNG → реальный файл, dimensions/alpha проверены.
9. Keyboard-only panel flow и visible focus.
10. 125/150/200% Windows scaling без обрезанных критичных controls.

## Fault injection

Тестовые адаптеры должны уметь оборвать запись:

- до temp create;
- в середине ZIP;
- после flush temp, до replace;
- во время replace;
- после replace, до cleanup;
- при нехватке места/доступа;
- при stale recovery/lock.

Для каждого случая определяется, какой файл считается последним корректным и что видит пользователь.

## Performance suite

Фиксированные synthetic fixtures:

- `objects-1k`, `objects-10k`, `objects-50k`;
- `brush-4k-100-strokes`;
- `masks-20-layers`;
- `assets-mixed-2gb-catalog` (после MVP);
- `export-4k`, `export-8k`;
- `open-save-large`.

Собирать median/P95 frame time, hit-test latency, managed/native memory, cache hit rate, save/export time. Число без модели ПК и renderer не считается доказательством.

## File-format fixtures

- минимальный валидный проект;
- каждый тип слоя/объекта;
- unknown fields/type;
- каждая поддерживаемая schemaVersion;
- Unicode/кириллица в названии и заметках;
- linked/embedded/missing asset;
- malicious path traversal и compression bomb metadata;
- partially corrupted project.

## Приёмка визуала

Не использовать расплывчатое «похоже красиво». Для каждого экрана проверять hierarchy, плотность, контраст, focus, empty/loading/error states, long Russian text, high DPI и alignment. Для canvas — selection visibility на светлом/тёмном контенте.

## Release gate

- clean build и все обязательные тесты;
- dependency/license/SBOM review;
- self-contained запуск в чистой VM;
- install/update/uninstall и file association;
- отсутствие сетевых запросов в offline smoke;
- recovery/fault suite;
- performance budgets на эталонном ПК;
- минимум один реальный проект создан, закрыт, восстановлен и экспортирован;
- известные ограничения опубликованы, а не скрыты.

## Матрица отчёта

| Метка | Значение |
|---|---|
| `FACT` | проверено сейчас указанным способом |
| `PARTIAL` | часть критериев пройдена; перечислить остальные |
| `PROPOSED` | идея/план, не результат |
| `BLOCKED` | объективно нужен выбор/доступ/среда; указать точный блокер |

