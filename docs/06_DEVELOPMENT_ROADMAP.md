# 06. Дорожная карта разработки

## Почему порядок именно такой

Мы не делаем последовательно «весь дизайн → весь backend → весь frontend». У нативного графического редактора это создаст несовместимые предположения. Правильный порядок:

1. продуктовые границы и UX-сценарии;
2. риск-спайк холста, ввода, истории и файла;
3. одна сквозная вертикаль от dashboard до повторного открытия;
4. инструменты маленькими вертикальными срезами;
5. собственный визуальный язык и ассеты поверх доказанного каркаса;
6. производительность, доступность, упаковка и release QA.

«Backend» здесь — не сервер. Это локальные `Core`, `Persistence`, `Assets`, `Rendering` и `Export`.

## Общие ворота каждого этапа

Этап закрыт только если:

- выполнена вся приёмка этапа;
- тесты и сборка проходят с чистого checkout;
- ручной сценарий выполнен в настоящем окне;
- обновлены handoff и решения;
- нет скрытого расширения scope;
- явно перечислено, что не проверено.

## Stage 0 — Исследование и правила (`COMPLETE_WITH_BLOCKED_MANUAL_AUDIT`)

### Готово

- публичный функциональный слепок редактора 2.0;
- PDF и таблица инструментов разобраны;
- предложены продукт, стек, архитектура, формат и QA;
- установлен минимальный SDK;
- создан пакет документации.

### Осталось

- ручной аккаунтный walkthrough по чек-листу из research;
- выбрать финальное имя позже.

DEC-001—DEC-008 приняты владельцем 25.08.2026. Stage 1 разрешён к запуску.

## Stage 1 — Technical spike нативного ядра

### Цель

Доказать или опровергнуть стек до архитектурного разрастания.

### Срез

- нативное окно Avalonia;
- custom Skia canvas;
- 10 000 тестовых объектов, pan/zoom, culling, hit-test/select;
- один raster tile brush stroke и undo/redo;
- одна mask preview;
- минимальный versioned document;
- atomic save/reopen и crash-recovery proof;
- offscreen 4K export;
- измеримый benchmark/report.

### Не входит

Dashboard, финальный дизайн, ассеты, полный toolbar, installers, сложные filters.

### Gate

DEC-002 принимается только по числам и визуальному smoke test. Если бюджеты не пройдены после двух ограниченных попыток оптимизации — отдельный сравнительный Qt spike.

## Stage 2 — Foundation и первая сквозная вертикаль

### Цель

Пользователь запускает программу, создаёт карту, двигается по холсту, добавляет тестовый объект, сохраняет, закрывает и снова открывает результат.

### Результат

- solution/module boundaries;
- local dashboard: recent/new/open;
- create document wizard;
- viewport, select/transform тестового объекта;
- command stack;
- `.mapproj` v1 минимального состава;
- dirty/saving/saved/error states;
- thumbnail;
- tests и diagnostics.

### Gate

Принудительное завершение во время сохранения не уничтожает последнюю корректную карту. Повторное открытие восстанавливает геометрию и свойства.

## Stage 3 — Слои, дерево объектов и базовая кисть

### Срезы отдельно

3A. Object/Brush/Base layers, add/rename/reorder/hide/lock.

3B. Object tree, search/select, group, move to layer, sublayer.

3C. Tile brush: color/texture/eraser, size/opacity/softness, one gesture = one command.

3D. Layer mask: add/subtract, overlay, disable/copy.

### Gate

Большой brush stroke не создаёт full-canvas snapshot на каждое pointer move. Маска влияет только на свой слой. Сохранение/undo совместимы.
Renderer не перебирает всю сцену и не создаёт bitmap размером с документ: spatial culling,
tiled raster и mip/LOD доказаны на canvas 65 536×65 536 units и профиле 50 000 объектов.

## Stage 4 — Stamp и локальный Asset Manager

### Срезы

- безопасный импорт PNG/JPEG/WebP и provenance;
- catalog packs/tags/search/recent/favorite;
- place/transform/replace;
- opacity/blend/color tweaks/shadows;
- random set/angle/flip и scatter;
- missing asset resolver;
- embedded/linked asset roundtrip.

### Gate

Ассет с тем же именем, но другим hash не подменяется тихо. Массовое размещение остаётся интерактивным и отменяется одной транзакцией на gesture.
Каталог из 10 000 пользовательских ассетов открывается/фильтруется без полного decode изображений.

## Stage 5 — Land

### Срезы

- Land layer и add/subtract;
- circle/rect/ellipse/polygon/grid-block;
- move area;
- edge styles;
- несколько Land layers и masks.

### Gate

Изменение edge style не растрирует исходную землю. Большая карта сохраняется и экспортируется детерминированно.

## Stage 6 — Line и Shape

### Срезы

6A. Line color, pen/free draw, editable nodes.

6B. Asset stroke, caps, offset, shadow, portals.

6C. Shape primitives, fill/stroke, editable nodes.

6D. Boolean add/subtract/merge с геометрическими fixtures.

### Gate

Узлы и portals переживают save/reopen; boolean operations не создают invalid geometry на тестовом наборе.

## Stage 7 — Text, Notes, Grid

### Срезы

- текстовая разметка и licensed fonts;
- outline/shadow/curve/spacing;
- notes panel и anchors;
- square/hex grids, snap и masks;
- configurable shortcuts, tooltips и keyboard flow.

### Gate

Документ с отсутствующим шрифтом открывается предсказуемо. Заметки не попадают в image export по умолчанию. Grid snap воспроизводим при любом zoom.

## Stage 8 — Filters и композиции

### Срезы

- базовые color/filter layers;
- masked filters и preview LOD;
- editable compositions;
- flattened composition export;
- copy/paste между двумя открытыми документами;
- калибровка по reference grid scale.

### Gate

Preview и export визуально совпадают по golden tests. Композиция не включает фон/grid/filters без явного выбора.

## Stage 9 — Надёжность полного цикла

Текущий статус на 26.08.2026: **PARTIAL**. Срез 9A (autosave journal + startup recovery) выполнен
и проверен; 9B–9E остаются отдельными задачами. Отчёт: `reviews/STAGE_09A_AUTOSAVE_VERIFICATION.md`.

### Срезы

- autosave journal;
- startup recovery UI;
- checkpoints и возврат;
- resize/crop/new-from-region;
- full export PNG/JPEG/WebP до 16K и tiled/region export больше 16K;
- отмена долгих операций;
- migrations и corrupted-file diagnostics.

### Gate

Fault-injection suite доказывает отсутствие потери последней корректной версии. Все fixtures старых схем мигрируют на копии.

## Stage 10 — Собственный UX/UI и asset pilot

Текущий статус: **PARTIAL**. Первый input-parity slice реализует работающие global/tool/selection
и Brush/Mask/Text shortcuts. Полный object/grid inspector, `IsometricSquares`, multi-selection,
stamp sets/scatter и asset fixtures ещё не реализованы.

Этот этап начинается после предоставления владельцем материалов, но отдельные UX-прототипы идут раньше каждого инструмента.

- утвердить design tokens и собственную icon system;
- light/dark/high-contrast;
- neutral built-in starter pack с доказанным происхождением;
- onboarding и локальная help;
- полный object inspector parity: stamp transform/effects/color tweaks, shape fill/stroke и общий
  sublayer/opacity/blend/blur без копирования trade dress;
- полный grid property sheet: square/isometric-squares/hex-flat/hex-pointy presets и точные size/subdivision/
  line/color/opacity/offset/rotation/snap/export controls;
- input parity отдельными slices: работающие global/tool/selection shortcuts, затем multi-select,
  groups, stamp-set/scatter controls и licensed-font navigation; пустые bindings запрещены;
- два собственных diagnostic PNG fixtures по `research/TEST_ASSET_FIXTURES.md` для transform,
  color/alpha/effects/scatter/brush проверок; они не являются release starter pack;
- usability sessions: новичок/опытный/keyboard/high-DPI;
- никаких заимствованных Inkarnate assets или trade dress.

## Stage 11 — Performance, packaging, release candidate

- 1K/10K/50K object profiles;
- large-map profile: 65 536×65 536 units, deep zoom 0,5–6400%, 20+ layers/masks;
- 10 000-asset catalog profile и bounded thumbnail/decode cache;
- 8K/16K и greater-than-16K tiled/region export profiles без full-canvas allocation;
- long brush/mask/export stress;
- memory/cache/undo budgets;
- self-contained `win-x64`;
- installer + uninstall + file association отдельным принятым решением;
- clean Windows VM smoke;
- antivirus false-positive review, SBOM/licenses;
- backup/upgrade/downgrade instructions;
- signed release только после решения владельца о сертификате.

## Не планировать датами до Stage 1

Сроки без измеренного spike будут выдумкой. После Stage 1 Opus/тимлид оценивают каждый issue-sized срез по фактической скорости и рискам. Дорожная карта определяет порядок и критерии, а не обещает календарь.

## Definition of MVP

MVP закрывает Stages 1–7 и базовую часть Stage 9: dashboard, файл/recovery, viewport/select,
layers/masks, brush, stamps/assets, land, line/shape/text/notes/grid и 8K export. Filters,
compositions, 16K и финальная упаковка могут перейти в beta, но архитектурные инварианты large-map,
tiled raster и отсутствие full-canvas allocation обязательны до кисти/ассетов и не откладываются.
