# Прочная связь кэшей рендера с неизменяемым документом Map — ресёрч (только чтение)

Код Map прочитан в `C:\Projects\Map-wt-pack4` на `3cfed63` (ветка `claude/fps-pack-4`). Регрессию дал коммит
`7d8251a` (ветка `claude/pack4-on-candidate`): индексы перенесены с документа на его `Layers`, добавлен
`ObjectLayerChanges.RegisterReplacement`. Статусы: FACT — прочитано в коде; ASSUMPTION — вывод, не проверенный запуском.
Ничего не собиралось и не запускалось.

---

## 1. Кэши Map, привязанные к идентичности объекта, и как они ломаются

### 1.1 Общий механизм (FACT)

Почти все производные данные лежат в `ConditionalWeakTable<экземпляр, производное>`. Связь между соседними
ревизиями хранится в одном месте — в стороннем реестре `ObjectLayerChanges` (`src/Map.Core/Documents/ObjectLayerChanges.cs:12`,
`Register` в :14-25). Он держит source сильной ссылкой только при глубине ≤ 64 (`retain = depth <= 64`), дальше — слабой.
Связь появляется, только если код, создавший новый `ObjectLayer`, сам вызвал `Register*`. Если не вызвал, всякий
потребитель уходит в «холодную» сборку O(N) или в запасной путь отрисовки. Тихо, без счётчика с причиной, и
тесты при этом зелёные.

В Core нет ни одного счётчика ревизий содержимого: в `Map.Core/Documents` есть только `TileMap.BlockRevision` (:79),
который тоже возвращает идентичность (`object?`). (FACT, grep `Revision`.)

### 1.2 Перечень

| # | Кэш (файл:строка) | Ключ | Что предполагает | Что будет, если предположение нарушено |
|---|---|---|---|---|
| 1 | `SceneRenderer.OverviewPlans` (`Map.Rendering/SceneRenderer.cs:766`), построение `ObjectOverviewPlan` (:6524-6560) | экземпляр `ObjectLayer` | у нового экземпляра слоя есть связь `ObjectLayerChanges` с прежним экземпляром, у которого план уже есть | **Холодный план O(N)** (2M объектов). В отличие от #3 и #4, здесь **нет запасного пути через `layer.Objects`**: даже `with { Opacity = … }` при том же списке объектов без `Register` строит план с нуля |
| 2 | `OverviewPreparation.GetLayer` (`SceneRenderer.OverviewPreparation.cs:151-166`), `Request` (:194), `TryTakeOrRequest` (:234-236) | `ReferenceEquals(state.Layer, layer)` | новый экземпляр слоя означает новое содержимое | Каждый новый экземпляр **отменяет** фоновую работу (`Cancel(state)`) и ставит её заново. Пока снимок не готов, `TryTakeOrRequest` не отдаёт растры фрагментов. Отрисовка берёт прежний растр по `LayerId` (`TryGetPreviousOverview`, :6331), а без него рисует объекты по одному. Если новые экземпляры приходят чаще, чем строится холодный план, работа не завершается никогда. ASSUMPTION: это и есть падение 44.6 → 9.2 FPS |
| 3 | `DetailRevisions` + `RevisionsByObjects` + `RevisionsFor` (`SceneRenderer.cs:767-793`), `DetailTileRevisions` (:3585-3660) | экземпляр `ObjectLayer`, затем экземпляр `layer.Objects` | одно из двух: связь в `ObjectLayerChanges` или тот же экземпляр списка | Без связи `Version()` выдаёт новый глобальный номер (`Interlocked.Increment(ref _detailRevision)`, :3653). Все плотные тайлы вида собираются заново, а `_unsupported` пересчитывается проходом по всем объектам (:3613). Есть и побочный риск: `_parent` — сильная ссылка, цепочка не ограничена, и `Version()` рекурсивно идёт по ней (:3651) |
| 4 | `ObjectSpatialIndex.Cache` / `ByObjects` (`Map.Core/Documents/ObjectSpatialIndex.cs:13`, :115-125) | `ObjectLayer`, затем экземпляр списка | так же, как в #3 | Холодная сетка O(N). Внутри: `ReferenceEquals(basisObjects[at], item)` (:45) |
| 5 | `DocumentRevisionIndex.Cache` (`DocumentRevisionIndex.cs:25`, `For` :137-140, `ShareUnchangedLayers` :20-23, `Register*` :368-591) | `MapDocument`, а в `7d8251a` — `document.Layers` | каждая новая ревизия зарегистрирована через `Register*` или `Share*` | Полный корневой индекс по всем объектам (`RootBuilds++`). Его используют выделение и переопределения (`SceneRenderer.cs:3469`, :6402, :6965) |
| 6 | `ObjectNameIndex.Cache` (:17, в `7d8251a` по `Layers`) | документ / список слоёв | то же | Холодный индекс имён |
| 7 | `AssetUsageIndex` (:13), `BoundObjectIndex` (`Editing/BoundObjectIndex.cs:14`), `LabelPriority.Index` (`Editing/LabelPriority.Index.cs:44`), `PlacementOwnershipIndex` (:10) | экземпляр списка объектов или слой | связь из `ObjectLayerChanges.Register` (:21-24) | Холодные индексы O(N) |
| 8 | `StyleRenderer`: `TextSplits` (`Styles/StyleRenderer.Split.cs:25`), `PartsByLayers`/`BareLayers`/`NameLayers`/`SplitByDocument` (`StyleRenderer.cs:463-466`), `Extents`, `VisibleStampAssets` (`StyleRenderer.Painter.cs:230`, :260), `PrintDocuments` (`StyleRenderer.Print.cs:10`) | список, слой, документ | производные слои связаны через `RegisterDerived` | Стиль на миллионе объектов снова проходит по всем объектам |
| 9 | `KeptLayerSupport` (:1081), `SurfaceOrders` (:1250), `SpatialIndexBuilds` (:4510), `LocalAssetSets` (:6196), `DetailRasterContexts` (`SceneRenderer.DetailRaster.cs:56`) | `ObjectLayer` | экземпляр слоя меняется вместе с содержимым | Лишние пересборки: экземпляр меняется при изменении свойств, которые на эти кэши не влияют |
| 10 | `SceneObjectList.TextChunks` (:19), `MapProjectStore.ObjectChunkCache` (`Map.Persistence/MapProjectStore.ObjectChunkCache.cs:15`) | массив-фрагмент `SceneObject[]` | фрагмент неизменяем и создаётся заново только при правке | **Сравнительно прочно**: идентичность фрагмента почти совпадает с его содержимым. Ломается, только если правка без нужды пересобирает фрагмент с тем же содержимым |
| 11 | `ObjectOverviewCache` (`ObjectOverviewCache.cs:20-26`, :332, :347-386) | `(LayerId, x, y, zoom, ContentHash)` | — | **Уже содержательный ключ** — образец для остальных. Растр переживает смену экземпляров, если план выдаёт те же хэши |
| 12 | Реки, дороги, суша, гексы: `CarvedLandCache.cs:22-25`, `RiverBankRenderer.cs:22`, `RiverRenderer.Bodies` (:22), `RoadRenderer.Bodies` (:20), `HexChunkCache.cs:41`, `HexLayerRenderer.cs:116/383/663`, `RiverGeometry.Cache` (`Editing/RiverGeometry.cs:69`), `RoadCrossings` (:66), `CoarseCoverage.cs:99`, `MaskCoverageStates`/`LandCoverageStates` (`SceneRenderer.cs:796-797`) | экземпляр элемента | неизменяемый элемент пересоздаётся только при правке | Точечные пересборки. Объёмы малые, поэтому здесь меньше вреда, чем от #1–#5 |

Почему тесты не поймали регрессию (FACT): `RenderPathCostGuardTests` (:79-85) проверяют одну ссылку
`ObjectLayerChanges.TryGet(target)` после одной команды. Ни один тест не смотрит на сквозной путь
«документ после произвольной операции → кадр без холодной сборки и без поштучной отрисовки». Счётчик `_plansBuilt`
(`OverviewPreparation.cs:67`, :361) не различает «сборка по дельте» и «холодная сборка», и тест его не проверяет.

---

## 2. Как это решают зрелые системы

Лицензии: Excalidraw — MIT; Bevy — MIT/Apache-2.0; Salsa — MIT/Apache-2.0; CodeMirror — MIT; Roslyn — MIT.
Код ниже прочитан, не копировался и не запускался. Брать предлагается только идеи.

### 2.1 Excalidraw — `version` + `versionNonce` в самом элементе (источник прочитан, sha `3a317b5`)
- Любая правка увеличивает `version` и задаёт новый случайный `versionNonce`, и делает это в одной точке:
  [mutateElement.ts#L129-L147](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/mutateElement.ts#L129-L147),
  иммутабельный вариант [newElementWith#L149-L183](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/mutateElement.ts#L149-L183),
  [bumpVersion#L184-L197](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/mutateElement.ts#L184-L197).
  Если ничего не изменилось, возвращается тот же элемент и версия не растёт.
- Сцена хранит `sceneNonce` — общий признак сброса кэша рендера:
  [Scene.ts#L136-L145](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/Scene.ts#L136-L145),
  [#L303-L305](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/Scene.ts#L303-L305).
- Слабое место у них то же, что у Map. Растр элемента лежит в `WeakMap` по идентичности
  ([renderElement.ts#L724-L774](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/renderElement.ts#L724-L774))
  и сбрасывается вручную через `ShapeCache.delete`. Для правки «мимо сцены» понадобилась отдельная заплатка: nonce
  элемента примешивают к ключу ([Renderer.ts#L361-L372](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/excalidraw/scene/Renderer.ts#L361-L372)).
  Там, где кэш хранит номер версии, проверка надёжна: `cached.version === element.version`
  ([shape.ts#L640-L673](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/element/src/shape.ts#L640-L673)).
  Версия плюс nonce служат и для слияния при совместной работе
  ([reconcile.ts#L23-L44](https://github.com/excalidraw/excalidraw/blob/3a317b55f508815d10a4c12acca99df30f026521/packages/excalidraw/data/reconcile.ts#L23-L44)).
- **Урок для Map:** номер версии хранится в самих данных и меняется в единственной точке правки. Кэш сравнивает
  *числа*, а не экземпляры. Каждая дорога «в обход» — это отдельная ошибка (их заплатка с nonce это показывает).

### 2.2 Bevy ECS — тики изменений (источник прочитан, sha `7ea4827`)
- У каждого компонента есть `ComponentTicks { added, changed }`. Потребитель хранит свой `last_run` и спрашивает
  `is_newer_than(last_run, this_run)`:
  [tick.rs#L57-L66](https://github.com/bevyengine/bevy/blob/7ea4827e16d5d50d48e22d9a249c554ad073f38a/crates/bevy_ecs/src/change_detection/tick.rs#L57-L66),
  [ComponentTicks#L199-L247](https://github.com/bevyengine/bevy/blob/7ea4827e16d5d50d48e22d9a249c554ad073f38a/crates/bevy_ecs/src/change_detection/tick.rs#L199-L247).
  Переполнение обработано явно: старые тики периодически ограничиваются сверху
  ([mod.rs#L15-L26](https://github.com/bevyengine/bevy/blob/7ea4827e16d5d50d48e22d9a249c554ad073f38a/crates/bevy_ecs/src/change_detection/mod.rs#L15-L26),
  [check_tick#L80-L91](https://github.com/bevyengine/bevy/blob/7ea4827e16d5d50d48e22d9a249c554ad073f38a/crates/bevy_ecs/src/change_detection/tick.rs#L80-L91)).
- Тик ставится **конструктивно**: единственный путь к изменяемому значению, `DerefMut`, вызывает `set_changed()`.
  С `track_location` он ещё и записывает место вызова (`changed_by`). Обойти можно только явным вызовом
  `bypass_change_detection()`:
  [traits.rs#L490-L505](https://github.com/bevyengine/bevy/blob/7ea4827e16d5d50d48e22d9a249c554ad073f38a/crates/bevy_ecs/src/change_detection/traits.rs#L490-L505).
- **Урок для Map:** новая ревизия появляется без участия того, кто пишет команду. Сбросить происхождение можно только
  явно и с названной причиной. Место создания записывается в отладке, поэтому «откуда холодная сборка» видно сразу.

### 2.3 Salsa (rust-analyzer) — ревизии, долговечность, backdating, журнал событий (источник прочитан, sha `52b12a4`)
- Глобальный счётчик ревизий и `last_changed_revision(durability)`:
  [zalsa.rs#L447-L466](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/src/zalsa.rs#L447-L466).
  Проверка мемо за O(1): если `verified_at == now` или с момента `verified_at` не менялось ничего нужной
  долговечности, перепроверять не нужно:
  [maybe_changed_after.rs#L347-L390](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/src/function/maybe_changed_after.rs#L347-L390),
  [durability.rs#L3-L22](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/src/durability.rs#L3-L22).
- **Backdating:** если пересчитанное значение *равно* старому, `changed_at` остаётся старым, и потребители ничего не
  пересчитывают. Нарушение монотонности здесь — обнаруживаемая ошибка (`report_backdate_violation`):
  [backdate.rs#L15-L59](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/src/function/backdate.rs#L15-L59).
- Журнал событий `WillExecute` / `DidValidateMemoizedValue`
  ([event.rs#L28-L60](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/src/event.rs#L28-L60)).
  Тесты сравнивают журнал с ожидаемым
  ([tests/common/mod.rs#L41-L57](https://github.com/salsa-rs/salsa/blob/52b12a418909c9d982a842691c6eaacfc6d62936/tests/common/mod.rs#L41-L57)),
  поэтому лишний пересчёт роняет тест.
- **Урок для Map:** (а) ключ — число ревизии плюс «долговечность»: свойства слоя меняются часто, содержимое редко;
  (б) равное содержимое при новом происхождении **приравнивается к старой ревизии** вместо пересборки;
  (в) журнал «кэш взят / пересчитан / причина», который тесты проверяют напрямую.

### 2.4 CodeMirror 6 — производное состояние живёт *в* снимке, обновление идёт только через транзакцию (источник прочитан, sha `9c80127`)
- Новый `EditorState` создаётся только из `Transaction`, и **каждое** поле (`StateField`) получает
  `update(old, tr)` с описанием изменений `tr.changes`:
  [state.ts#L95-L125](https://github.com/codemirror/state/blob/9c801279cb83011e6f92af778f4443406e8f1200/src/state.ts#L95-L125).
  Если `compare(old, new)` даёт «равно», сохраняется старое значение и зависимые не пересчитываются:
  [facet.ts#L322-L349](https://github.com/codemirror/state/blob/9c801279cb83011e6f92af778f4443406e8f1200/src/facet.ts#L322-L349).
- `RangeSet.map(changes)` повторно использует фрагменты, которых изменение не коснулось, и пересобирает только
  затронутые:
  [rangeset.ts#L282-L300](https://github.com/codemirror/state/blob/9c801279cb83011e6f92af778f4443406e8f1200/src/rangeset.ts#L282-L300),
  отображение позиций — [change.ts#L120-L122](https://github.com/codemirror/state/blob/9c801279cb83011e6f92af778f4443406e8f1200/src/change.ts#L120-L122),
  [#L251](https://github.com/codemirror/state/blob/9c801279cb83011e6f92af778f4443406e8f1200/src/change.ts#L251).
- **Урок для Map:** дельта — часть нового снимка, а не запись в стороннем слабом реестре. Создать снимок без дельты
  нельзя: такого API просто нет. Это самая сильная форма связи. Фрагментное отображение один в один соответствует
  фрагментам `SceneObjectList` (256 объектов).

### 2.5 Roslyn — кэш по равенству значений и причина каждого шага (sha `b9b56c2`, прочитано через gh api)
- Каждый шаг генератора помечается одной из причин `New / Modified / Unchanged / Cached / Removed`:
  [IncrementalStepRunReason.cs#L10-L36](https://github.com/dotnet/roslyn/blob/b9b56c2cf6bc9e7f6449e9eece2e3a478a61dc83/src/Compilers/Core/Portable/SourceGeneration/IncrementalStepRunReason.cs#L10-L36).
  Сравнение по значению задаётся явно через `WithComparer`, шаги получают имена через `WithTrackingName`:
  [ValueSourceExtensions.cs#L132-L163](https://github.com/dotnet/roslyn/blob/b9b56c2cf6bc9e7f6449e9eece2e3a478a61dc83/src/Compilers/Core/Portable/SourceGeneration/Nodes/ValueSourceExtensions.cs#L132-L163).
  Учёт включается флагом `TrackIncrementalGeneratorSteps`
  ([GeneratorDriverOptions.cs#L18](https://github.com/dotnet/roslyn/blob/b9b56c2cf6bc9e7f6449e9eece2e3a478a61dc83/src/Compilers/Core/Portable/SourceGeneration/GeneratorDriverOptions.cs#L18)).
  Принятая практика тестов: прогнать генератор второй раз и потребовать, чтобы все шаги были `Cached`/`Unchanged`.
  Документация прямо предупреждает, что кэш по ссылкам ломается: [incremental-generators.md#L1060-L1081](https://github.com/dotnet/roslyn/blob/b9b56c2cf6bc9e7f6449e9eece2e3a478a61dc83/docs/features/incremental-generators.md#L1060-L1081).
- Red/green-деревья (по документации, исходники не читались): зелёные узлы не зависят от позиции и разделяются между
  версиями, красные создаются по требованию. Кэш привязан к неизменяемому содержимому, а не к обёртке.

### 2.6 Остальное (по документации; исходники НЕ читались — NOT CHECKED)
- **Chromium cc, property trees:** изменение transform, opacity или clip идёт по деревьям свойств и **не** сбрасывает
  содержимое `PictureLayer`. Содержимое сбрасывается регионами (`SetNeedsDisplayRect` / `invalidation`). Для Map это
  прямой ответ на `RevisionsByObjects`: маска, прозрачность и имя слоя — свойства, а не содержимое.
- **Flutter:** `RepaintBoundary` и `markNeedsPaint` — явные грязные флаги на узле. `Key` сохраняет связь элемента при
  перестройке дерева, без него связь теряется по позиции. Тот же урок: идентичность без явного ключа хрупка.
- **Reselect / Immer / React keys:** мемоизация по `===` теряет кэш, когда создаётся новая ссылка с тем же содержимым.
  Reselect лечит это сравнением результата (`resultEqualityCheck`, аналог backdating). React требует стабильный `key`
  и предупреждает в dev-режиме, если его нет. Это пример громкого отказа в отладке.
- **Figma** (публичные статьи о мультиплеере): ключ — id узла и номер изменения свойства, а не объект в памяти.

**Сводка паттернов:** (1) номер ревизии в самих данных, растёт в единственной точке правки; (2) дельта внутри
снимка; (3) содержимое и свойства — раздельные ревизии; (4) равное содержимое сохраняет старую ревизию
(backdating); (5) журнал «попадание / дельта / холодно, причина», который проверяют тесты; (6) сбросить
происхождение можно только явно и с причиной.

---

## 3. Предлагаемый дизайн для Map (PROPOSED)

### 3.1 Данные в Core

```csharp
// Map.Core/Documents/ContentRevision.cs
public readonly record struct ContentStamp(long Lineage, long Revision);   // в память, не сериализуется
internal static class RevisionClock { static long _n; public static long Next() => Interlocked.Increment(ref _n); }

public enum LineageReason { Loaded, Constructed, ReplacedWholeContent, Derived /* стиль */, Imported }
```

**`SceneObjectList`** (класс, равенство по ссылке; на равенство записей `ObjectLayer`/`MapDocument` не влияет)
получает поля:
- `ContentStamp Stamp` — `Lineage` сохраняется по всей цепочке правок, `Revision = RevisionClock.Next()` у каждого
  нового экземпляра.
- `ListDelta? Delta` — **сильная** ссылка на компактную дельту от родителя: `ParentRevision`, `RemovedObjects`
  (сами удалённые `SceneObject` вместе с прежними индексами, по размеру правки), `AddedIndices`, `OrderCompacted`.
  Исходный список держать не нужно: дельта самодостаточна. Поэтому нет зависимости от GC и от предела глубины 64,
  которые сейчас есть в `ObjectLayerChanges`.
- `long[] ChunkRevisions`, параллельный `_chunks`: у неизменённого фрагмента ревизия наследуется, у пересобранного
  берётся новая. Это фрагментное отображение, как в CodeMirror `RangeSet.map`.
- `ulong Fingerprint` — сумма по модулю 2^64 от `Mix(ObjectId, RuntimeHelpers.GetHashCode(obj))`, поддерживается
  за O(дельты). Это не ключ кэша, а **детектор** «то же содержимое, новое происхождение» (backdating из Salsa) и
  «другое содержимое, а ревизия та же» (строгий режим).

**Кто увеличивает ревизию.** Её задают только конструкторы `SceneObjectList`, то есть сами операции
`ReplaceAt/ReplaceMany/Splice/InsertMany/AppendRange/RemoveRange`: они передают в приватный конструктор
`source.Stamp.Lineage` и дельту. Команде ничего регистрировать не нужно (по образцу `DerefMut` в Bevy). Происхождение
можно сбросить только явным `SceneObjectList.NewLineage(objects, LineageReason reason)`. Нынешний
`From(IReadOnlyList)` для чужого списка становится `NewLineage(…, Constructed)`. В строгом режиме записывается место
вызова (`[CallerFilePath]/[CallerLineNumber]`, как `changed_by` в Bevy).

**`ObjectLayer`**: `ContentStamp ObjectsStamp => _objects.Stamp`; `PropertyRevision` вычисляется по значимым для
рендера свойствам (`Mask`, `Opacity`, `Visible`, `Blend`, `Effects`, `SublayerStates`) как дешёвый структурный хэш.
`with { Opacity = … }` копирует поле `_objects`, поэтому ревизия содержимого сохраняется **автоматически** — без
`Register` и без `RevisionsByObjects`. Это разделение содержимого и свойств, как в Chromium.

**`MapDocument`**: `LayersStamp` — хэш последовательности `(LayerId, ObjectsStamp.Revision, PropertyRevision)`.
`DocumentRevisionIndex` и `ObjectNameIndex` ключуются им, а не экземпляром документа или `Layers`. Дельта документа
выводится из дельт слоёв: слои с той же ревизией совпадают, у остальных есть `Delta`.

### 3.2 Кэши рендера

Общий помощник `RevisionedCache<TValue>` в Map.Rendering:
- Хранилище — `Dictionary<LayerId, Ring>`, где `Ring` — небольшое LRU из 3–4 записей `(ContentStamp, TValue)`
  (для Undo/Redo на шаг-два назад). Это **сильные** ссылки с бюджетом в `WorkingMemoryBudget`, а не
  `ConditionalWeakTable`.
- `Resolve(layer)` возвращает `(value, reason)`, где reason из `CacheReason { Hit, Delta(n), Backdated, ColdLoaded,
  ColdNewLineage(reason), ColdChainTooLong, ColdEvicted, ColdUnexplained }`:
  1. Точное совпадение `(LayerId, Lineage, Revision)` → `Hit`.
  2. Та же `Lineage`, и цепочка `Delta.ParentRevision` за ≤ K шагов доходит до ревизии в кольце → дельты применяются
     по порядку → `Delta(n)`. Нужен и обратный ход для Undo: дельта симметрична (удалённые + добавленные), её можно
     применить в обратную сторону.
  3. Новое происхождение, но `Fingerprint` и `Count` совпадают с записью кольца → фоновая сверка по фрагментам
     (`ReferenceEquals` объектов) и **принятие старого значения** → `Backdated`.
  4. Иначе холодная сборка **в фоне**, причина фиксируется по `LineageReason`.
- `ColdUnexplained` — промах при той же `Lineage` и разорванной цепочке, или промах без вытеснения и без
  `NewLineage`. Это ошибка Core: `Debug.Fail` в отладке и в тестах (`MAP_STRICT_CACHES=1`), строка в журнале
  производительности (DEC-110) в Release.

Перевод потребителей:
- `OverviewPlans` → `RevisionedCache<ObjectOverviewPlan>`, ключ — `ObjectsStamp`. Дельта плана строится из
  `ListDelta` (тот же код, что сейчас на :6527-6560, но вход берётся из списка, а не из `ObjectLayerChanges`).
- `OverviewPreparation.LayerWork`: ключ `LayerId` + `ObjectsStamp`. Смена `PropertyRevision` не отменяет работу.
  `TryTakeOrRequest` сравнивает `snapshot.Stamp == layer.ObjectsStamp`, а не экземпляры. Пока новый план не готов,
  рисуется **старый план того же `LayerId`** с растрами из `ObjectOverviewCache` (там ключ — хэш содержимого,
  неизменённые фрагменты совпадут). Это stale-while-revalidate: поштучная отрисовка остаётся только при первом
  открытии карты.
- `DetailRevisions`/`RevisionsByObjects` → ключ `(ObjectsStamp, хэш SublayerStates)`. `DetailTileRevisions._parent` —
  ревизия-число с ограниченной цепочкой (K), без рекурсии.
- `ObjectSpatialIndex`, `BoundObjectIndex`, `AssetUsageIndex`, `LabelIndex`, `PlacementOwnershipIndex` переходят на
  тот же помощник (Core), ключ — `ObjectsStamp`.
- Кэши из пунктов 9 и 12 таблицы переводятся по мере надобности; для мелких элементов `ConditionalWeakTable` допустим.

### 3.3 Строгий учёт и тест, который громко падает

1. `CacheLedger` (internal, `Interlocked`): `[cache, reason] → count`. Плюс счётчики отрисовки
   `PerObjectFallbackDraws` (объекты `DrawOverviewObjects`/`DrawObject` вне растров на плотном слое) и
   `ColdBuildObjectsVisited`. Всё это попадает в `RenderStats` и в журнал производительности.
2. Тест `Every_editor_command_keeps_render_caches_warm` в `Map.Rendering.Tests`, рядом с `RenderPathCostGuardTests`:
   - Документ на 200 000 объектов (размер существующих guard-тестов) плюс отдельный вариант на миллион
     (`Million_object_variant`). Кэши прогреваются: план обзора, ревизии тайлов, пространственный индекс, индексы
     ревизий и имён.
   - Для **каждой** реализации `IEditCommand` из каталога (как в `c2ba8e8` «O(N) guards for every editor command»),
     а также для операций уровня документа (`WithCanvas`, `WithLayers` тем же списком, восстановление контрольной
     точки, стилевое разделение, смена маски, прозрачности, видимости подслоя) выполняются Apply → кадр →
     Undo → кадр → Redo → кадр. Кадр — headless `SceneRenderer.Render` с `ObjectCache` и `OverviewPreparation` при
     синхронном завершении фона.
   - Проверки: `ledger[*, ColdUnexplained] == 0`; `ColdNewLineage` разрешён только командам из явного списка
     с причиной (например, «сгенерировать город» → `ReplacedWholeContent`); `PerObjectFallbackDraws == 0` после
     завершения фона; `DocumentRevisionIndex.RootBuilds`, `SceneObjectList.IdentityRootBuilds` и
     `ColdBuildObjectsVisited` не выросли. Сообщение об ошибке содержит кэш, причину, команду и место создания
     списка (из строгого режима).
3. Тест на сам механизм (детектор): создать `ObjectLayer` с тем же содержимым через `NewLineage`. Ожидается
   `Backdated`, а не холодная сборка. Ревизия, которая не меняется при изменённом `Fingerprint`, — исключение.
4. Тест-«ловушка» в `Map.Core.Tests`, через рефлексию: у каждого публичного метода, возвращающего `MapDocument`,
   у слоя с неизменёнными объектами `ObjectsStamp` сохраняется (`with`, `WithLayer`, `WithLayers`, `WithCanvas`,
   `WithRevisedLayers`…).
5. Живая проверка: в журнале производительности показывать по каждому кадру `cold=…, reason=…`, а перфомансный смоук
   падать при `ColdUnexplained > 0`.

---

## 4. Миграция небольшими безопасными коммитами (PROPOSED)

1. `test(perf)`: `CacheLedger` и `PerObjectFallbackDraws` в нынешних местах (`OverviewPlans` miss, `PreparePlan`
   cold, `DetailTileRevisions` без связи, `RootBuilds`). Плюс сквозной тест из §3.3-2, пока со списком известных
   холодных путей (Skip с причиной, как сделано в guard-тестах). Поведение не меняется. Тест сразу покажет и
   регрессию `7d8251a`.
2. `feat(core)`: `ContentStamp` + `ChunkRevisions` + `Fingerprint` в `SceneObjectList`. Ревизию выдают
   конструкторы, `NewLineage(reason)` — явный, `From` для чужого списка помечен. Потребителей пока нет. Тесты:
   правка → ревизия+1 и та же `Lineage`, `with` сохраняет ревизию, Undo возвращает старую.
3. `feat(core)`: `ListDelta` в списке. `ObjectLayerChanges.TryGet` сначала читает дельту из списка, потом CWT.
   В строгом режиме обе дельты сверяются.
4. `perf(render)`: `RevisionedCache` и перевод `OverviewPlans` с `OverviewPreparation` (ключи по ревизии,
   stale-while-revalidate, свойства не отменяют работу). Тесты из шага 1 для обзора снимаются со Skip.
5. `perf(render)`: `DetailRevisions`/`RevisionsByObjects` → по ревизии. Ограниченная цепочка без рекурсии.
6. `perf(core)`: `ObjectSpatialIndex`, `BoundObjectIndex`, `AssetUsageIndex`, `LabelIndex`, `PlacementOwnership` →
   по ревизии.
7. `perf(core)`: `MapDocument.LayersStamp`; `DocumentRevisionIndex`/`ObjectNameIndex` ключуются им.
8. `feat(render)`: backdating по `Fingerprint` и строгий режим (`Debug.Fail`, `MAP_STRICT_CACHES`). Сквозной тест
   становится обязательным для всех команд.
9. `refactor(core)`: удалить `ObjectLayerChanges` CWT и `RegisterDerived`. Стилевые производные слои получают
   `Lineage = (source.Lineage, transformId)` и свою дельту.

Каждый шаг: Release-сборка `Map.slnx`, тесты затронутых проектов по фильтру, на шагах 4–7 — смоук
`Map.Performance` на плотной карте с журналом (`cold`/`fallback`).

---

## 5. Риски

- **Память:** дельты держат удалённые объекты. Цепочку нужно ограничить (K ≈ 64 правки или по байтам), длиннее —
  честная холодная сборка с причиной `ColdChainTooLong`. Кольцо из 3–4 планов на слой при 2M объектов заметно по
  памяти; нужен учёт в `WorkingMemoryBudget`.
- **Undo далеко назад:** старый экземпляр списка несёт старую ревизию. Если её нет в кольце, нужен обратный ход по
  дельтам или холодная сборка. Обратный ход требует симметричных дельт (удалённые + добавленные с индексами).
- **Ревизии не сохраняются в файл:** после открытия — новая `Lineage`, причина `ColdLoaded`. Это ожидаемо, но фоновая
  сборка 2M объектов остаётся O(N). Ей нужна stale-while-revalidate картинка (нынешняя «холодная подмена» суши).
- **Ложные срабатывания строгого режима** на законной полной замене содержимого (генерация, импорт). Нужен явный
  `LineageReason` и белый список в тесте; иначе тест начнут отключать.
- **Коллизии `Fingerprint`:** это только детектор. Backdating принимается после фоновой сверки по фрагментам, а не
  по одному хэшу.
- **Потоки:** ревизии выдаются через `Interlocked`; кольцо кэша работает под блокировкой `OverviewPreparation._gate`
  или неблокирующей заменой.
- **Равенство записей:** поля ревизии добавлять только в `SceneObjectList` (класс), не в записи `ObjectLayer`/
  `MapDocument`, иначе изменится равенство документов и тесты сериализации.
- **Объём переделки:** около 15 мест на CWT в Core/Rendering. Переводить только кэши из пунктов 1–8 таблицы, остальные
  оставить: они мелкие и почти не ломаются.
- **ASSUMPTION:** точная причина падения до 9.2 FPS в `7d8251a` по коду не восстановлена (кандидаты —
  `RegisterReplacement` с полной дельтой, новые экземпляры слоёв при `WithLayers`/восстановлении контрольной
  точки, перезапуск `GetLayer` по `ReferenceEquals`). Шаг 1 миграции её назовёт.
