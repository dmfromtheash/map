# Clean-room слепок каталога и наборов вариантов

Дата: **26.08.2026**. Статус: **FACT / PARTIAL / CLEAN-ROOM**.

## Граница исследования

Проверялось только видимое пользовательское поведение уже открытой тестовой карты владельца:
обычные кнопки, вкладки, поля, подсказки и результат действий. Не использовались DevTools,
network/storage, внутренние модели, исходный код страницы, выгрузка или копирование чужих
ассетов. Названия, графика, категории, теги и компоновка конкурента не являются требованиями к
Map. Продукт получает собственную терминологию, visual language и только собственные или явно
лицензированные материалы.

Доказательные снимки текущего прохода находятся в игнорируемом каталоге
`artifacts/inkarnate-mechanics-20260826-continuation/` и не входят в продукт.

## FACT — каталог как пользовательский сценарий

- каталог открывается отдельной крупной панелью поверх редактора;
- слева есть дерево источников/разделов с количеством элементов, сверху — breadcrumbs;
- основной список поддерживает поиск, изменение размера thumbnails, сортировку, группировку и
  составные фильтры;
- наблюдались сортировки по названию и дате, группировки по pack/tag и фильтры по tag, отсутствию
  tag, pack, пользовательской группе, диапазону даты, использованию в текущей карте и типу ассета;
- есть отдельные `Recent` и `Favorites`; добавление в избранное сразу отражается во вкладке;
- пользовательский раздел показывает локально добавленные материалы отдельно от поставляемых
  наборов.

Для Map это означает локальную `Library`, а не Marketplace: собственное дерево коллекций,
breadcrumbs, поиск/сортировка/фильтры, recent/favorite и virtualized thumbnail grid. Аккаунт,
публикация, облачные источники и названия чужих разделов не переносятся.

## FACT — «ассет-матрёшка»

- одна карточка может представлять логический ассет и показывать числовой badge количества
  вариантов; в наблюдаемой выборке встречались наборы от нескольких до десятков вариантов;
- нажатие badge раскрывает варианты прямо в каталоге, не превращая их в несвязанные карточки;
- после выбора варианта Stamp Tool показывает текущую позицию `N / total`, предыдущий/следующий
  вариант и миниатюры всех вариантов набора;
- текущий вариант можно менять вручную; наблюдаемый shortcut — `Alt+wheel`, а справка также
  показывает `Alt+Right/Left`;
- отдельный toggle `Randomize Stamps` после размещения выбирает следующий вариант набора случайно;
- избранным отмечается логическая карточка набора, а выбранный вариант остаётся текущим внутри неё;
- set работает вместе с режимами массового размещения, Area/Density, random angle и random flip.

## Принятая независимая модель Map

```text
AssetContent (SHA-256, размеры, alpha, storage, provenance)
  ↑
AssetVariant (stable variant ID, content SHA, label, anchor, default scale)
  ↑
AssetSet (stable set ID, ordered variant IDs, cover ID, collection, tags, favorite)
  ↑
AssetCollection (stable collection ID, optional parent ID, own name/order)
```

- одиночный импорт автоматически становится singleton set;
- множественный импорт может создать один set с упорядоченными variants;
- старый плоский `catalog.json` мигрирует в singleton sets без потери SHA, provenance, linked
  path, pack, tags и favorite;
- tile каталога представляет `AssetSet`; variant остаётся конкретным содержимым;
- current variant и recent/favorite — локальное состояние каталога, а не состояние карты;
- в `.mapproj` каждая размещённая копия по-прежнему ссылается на конкретный SHA-256. Поэтому
  render, undo/redo и reopen не зависят от наличия локального set и не reroll-ят случайность;
- при массовом размещении команда сначала разрешает весь список вариантов и asset references,
  затем одним undoable gesture добавляет готовые объекты;
- renderer не знает о catalog/set и никогда сам не выбирает случайный вариант.

В первом foundation-срезе не требуется менять schema `.mapproj`: принадлежность к set можно
восстановить по SHA, когда локальный каталог доступен, но документ остаётся самодостаточным по
фактически размещённым вариантам.

## Собственная семантика размещения

- `One`: размещается выбранный variant;
- manual cycle: следующий/предыдущий variant меняет только tool state;
- `Random after placement`: после успешной команды новый current variant выбирается seeded PRNG;
- `Many`: один drag/gesture создаёт независимые stamps; при random toggle variant выбирается для
  каждого stamp до создания команды;
- `Area`: радиус в world units; при активной сетке UI дополнительно показывает эквивалент в cells;
- `Density`: число stamps на `10 000` квадратных world units;
- расчёт количества: `clamp(round(density * area / 10000), 1, 5000)`;
- sampling равномерный внутри круга с сохранённым seed; random angle/flip также разрешаются до
  создания команды;
- undo/redo обязаны воспроизводить те же ObjectId, позиции, углы, flip и SHA вариантов.

Это наша явно документированная семантика. Закрытые алгоритмы, thresholds и seeds конкурента не
известны, не нужны и не воспроизводятся.

## Сопоставление с текущим Map

### FACT — уже есть

- `AssetRecord` с SHA-256 identity, dimensions/alpha, linked/embedded paths, tags, pack, favorite
  и provenance;
- безопасный PNG/JPEG/WebP import, dedupe, 128×128 thumbnail, hash verification;
- атомарная запись индекса с backup;
- metadata-only load/search и gate 10 000 записей без decode originals;
- плоский поиск и favorite;
- stamp placement/replace/scatter как undoable команды;
- `.mapproj` хранит конкретный `MapAssetReference` и `StampStyle.AssetSha256`.

### PARTIAL / отсутствует

- schema/version каталога и миграция плоской модели;
- `AssetCollection`, `AssetSet`, `AssetVariant` и set-level favorite;
- breadcrumbs, tree/counts, sort/group/composite filters, recent и virtualized thumbnail grid;
- раскрытие вариантов, cover/current variant, prev/next и random-after-placement;
- multi-asset placement command для scatter из разных variants;
- Area/Density как измеримые параметры gesture вместо demo count;
- native UIA/keyboard/high-DPI gate для каталога;
- два собственных логических diagnostic fixtures, включая много-вариантный set.

## Публичная open-source сверка

Официальная документация и repository Tiled просмотрены только как независимое подтверждение
общих data-model patterns: tileset может быть коллекцией отдельных image files, элементы имеют
стабильные IDs и metadata, individual images могут различаться размером, а relative probability
используется в random editing mode. Особенно полезна граница `remove from collection != delete
content`: это согласуется с нашим разделением `AssetContent` и `AssetVariant`.

Код, UI, file format и assets Tiled не переносятся. Map сохраняет собственную SHA-oriented model,
Avalonia UI и `.mapproj`; внешняя dependency не добавлена.

## UX и доступность Map

- числовой badge получает accessible name вроде «12 вариантов», а не остаётся немой цифрой;
- все icon-only controls имеют UIA `Name`, role, selected/expanded state и видимый focus;
- tree и grid работают с клавиатуры; focus не теряется при фильтрации/virtualization;
- пусто/loading/missing/hash-mismatch/unsupported/damaged-index показаны как разные состояния;
- удаление content не входит в foundation; исключение variant из set не удаляет оригинал;
- private user imports не попадают в screenshots, fixtures или распространяемый pack.

## Исторический foundation-срез

На момент написания следующим крупным срезом были Library v2 + variant sets + set-aware stamp
placement и diagnostic fixtures. В текущей линейной истории они, multi-selection/groups, object
inspectors, `IsometricSquares` и font catalog уже реализованы. Бывший Stage 9B перенумерован в
Stage 12A; актуальный порядок находится только в `docs/06_DEVELOPMENT_ROADMAP.md`.
