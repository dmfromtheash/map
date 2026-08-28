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
и Brush/Mask/Text shortcuts. Срез **10B Library v2 + variant sets + set-aware stamp placement +
diagnostic fixtures** реализован на branch `stage/10-asset-library-sets`: schema v2 с миграцией и
защитой повреждённого индекса, collections/sets/variants, поиск/сортировка/группировка/фильтры/
recent/favorite, виртуализированный каталог, `EditorTool.Stamp` с Area/Density и детерминированным
многовариантным размещением одной undoable командой, а также два собственных diagnostic fixtures.

Поверх него отдельным commit выполнен **10B Fix 01** по findings независимого Codex-review:
настоящая группировка на одной виртуализированной поверхности, полный набор фильтров с
переименованием/reorder/cover набора, явные состояния missing/hash-mismatch/checking/unsupported,
атомарный One/Area gesture со стоимостью по числу уникальных SHA, стабильный между процессами
seed, полный `used-in-current-map` и полная проверка схемы v2 с отказом на семантически
повреждённом индексе. По решению владельца scatter теперь размножает вариант, не поворачивая и не
отражая его.

Отдельный **10B Fix 02** закрывает два дефекта проверки Fix 01: карточки с захваченными значениями
больше не переиспользуются для другой строки после прокрутки/фильтра, а все видимые копии одного
набора в tag-группах получают одинаковое асинхронное состояние файла. Нативный driver теперь
доказывает каталог больше высоты окна, scroll/filter visual-to-row match, две tag-копии и реальный
`used-in-current-map`; фоновый режим не выводит окно поверх работы владельца. Self-check Fix 02
пройден, но независимая приёмка текущего авторского контекста ещё требуется. High-DPI и нативный
файловый диалог остались `PARTIAL`/`BLOCKED` — причины в
`docs/STATE_HANDOFF.md`. Полный object/grid inspector, `IsometricSquares`, multi-selection
UI/groups и font catalog остаются после 10B.

Поверх Fix 02 отдельным commit выполнен **UX-срез упрощения Library-панели**: default-путь
показывает только breadcrumbs, текстовые кнопки импорта, поиск, быстрые фильтры, счётчик и
карточки; сортировка/группировка/редкие фильтры, коллекции и управление набором свёрнуты в
именованные Expander, блоки выбранного набора и размещения появляются только после выбора, а
`Один штамп` скрывает scatter-контролы. Фоновый driver подтвердил срез (21 PNG, 96 проверок,
323 штампа / 12 SHA); это self-check авторского контекста, независимая приёмка Codex ещё
требуется. Последующая проверка Codex точного диапазона не нашла блокирующего production-дефекта;
два пропуска background-driver (media type и exact tag) исправлены и подтверждены свежим прогоном
на 22 PNG / 99 checks. Горячие клавиши и High-DPI остаются `PARTIAL`, нативный файловый диалог и
независимая clean-context UX-критика — `BLOCKED`. Подробности:
`reviews/STAGE_10B_ASSET_LIBRARY_VERIFICATION.md`.

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
- локальная Library v2 по DEC-013: collections/breadcrumbs/search/sort/group/filters,
  recent/favorite, virtualized thumbnail grid и миграция плоского каталога;
- logical variant sets по DEC-014: badge/expand/cover/current, previous/next, random-after-place,
  Area/Density и multi-asset placement одной undoable command;
- два собственных logical diagnostic fixtures по `research/TEST_ASSET_FIXTURES.md`: один stamp
  set с 12 PNG variants и один brush-tip PNG; они не являются release starter pack;
- usability sessions: новичок/опытный/keyboard/high-DPI;
- никаких заимствованных Inkarnate assets или trade dress.

### Порядок оставшихся срезов Stage 10

1. **10B — Library v2 и variant-set stamp workflow:** единый owner-approved крупный срез;
   контракт — `prompts/STAGE_10B_ASSET_LIBRARY_VARIANT_SETS.md`. Реализован; поверх него выполнены
   отдельные commits 10B Fix 01, Fix 02 и UX-срез упрощения перегруженной Library-панели. Codex
   проверил точный Claude-диапазон и закрыл два пропуска фонового драйвера; 10B остаётся `PARTIAL`
   по keyboard/DPI/file-picker и clean-context UX-gates.
2. **10C — Multi-selection/groups:** Ctrl+A/Ctrl+click, group/ungroup, group transform и UIA.
   Production-логика независимо проверена и исправлена на branch
   `stage/10c-multi-selection-groups` (`FACT / PARTIAL`):
   упорядоченное множественное выделение с primary и restore, `Ctrl+A` по поиску и типовому
   фильтру панели объектов, именованные группы с nullable `GroupName` в `.mapproj` (schemaVersion
   не поднимался), вход в группу с breadcrumb, мультитрансформации/`sublayer`/`opacity`/
   lock/visibility/delete одной undo-транзакцией с атомарным отказом на locked/hidden члене,
   общая рамка selection chrome через прежний `RenderOptions.CoSelectedObjectIds`, мульти-панель
   свойств без ложных единых значений и фоновый driver
   `scripts/drive-stage10c-multi-selection-groups.ps1`. Независимая проверка обнаружила и отдельным
   fix-срезом закрыла два дефекта: частичное выделение закрытой группы через `Ctrl+A` и изменение
   участника группы на скрытом/заблокированном слое. Canvas marquee/Esc/Ctrl+click и keyboard
   transforms доказаны адресными тестами/routing-review, но не фоновым нативным вводом; поэтому
   соответствующий runtime-gate остаётся `PARTIAL`. 10B keyboard/DPI/file-picker и clean-context
   gates этим срезом также не закрыты.
3. **10D — Object inspectors:** Stamp/Text/Line/Shape/Brush controls и color/effects parity.
   Функциональная реализация и security hardening закреплены локальным checkpoint
   `stage/10d-object-inspectors`: 683/683 tests, native driver 13/13, 7 PNG/30 checks, save/reopen
   и mutation/coverage evidence (`FACT` self-acceptance). Независимый exact-range gate остаётся
   `PARTIAL`; отчёт:
   `reviews/STAGE_10D_OBJECT_INSPECTORS_VERIFICATION.md`.
4. **10E — Grid geometry:** отдельная `IsometricSquares`, property sheet, snap/golden fixtures.
5. **10F — Licensed font catalog/navigation и редактируемые bindings.**

Эти срезы не смешиваются с Stage 9B–9E. Выполнение 10B не позволяет объявить весь Stage 10
готовым.

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

Сроки без измеренного spike будут выдумкой. После Stage 1 Codex/тимлид оценивают каждый крупный
вертикальный срез по фактической скорости и рискам. Дорожная карта определяет порядок и критерии,
а не обещает календарь.

## Definition of MVP

MVP закрывает Stages 1–7 и базовую часть Stage 9: dashboard, файл/recovery, viewport/select,
layers/masks, brush, stamps/assets, land, line/shape/text/notes/grid и 8K export. Filters,
compositions, 16K и финальная упаковка могут перейти в beta, но архитектурные инварианты large-map,
tiled raster и отсутствие full-canvas allocation обязательны до кисти/ассетов и не откладываются.
