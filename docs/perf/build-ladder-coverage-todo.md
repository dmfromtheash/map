# Лестница постройки — покрытие всех функций: что осталось (16.11)

Ветка `claude/build-ladder` (ce7ce7d0): `tools/Map.Performance/NativeBuildLadder.Coverage.cs` — `CoverageList()` 101 функция по
семействам `docs/perf/perf-functions.md`; прогон красный, если у функции нет успешной строки, у семейства `### 1.N` нет функции
или у значения `EditorTool`/`StampPlacementMode`/`LandShapeKind`/`DecorationKind` нет функции. Сторож
`tools/ai/remote/Run-Guard.ps1 -Ref <ветка> -Out <папка> [-Full] [-Seeds 101,202] [-Repeats 2]` → `Compare-Ladder.ps1` против
`docs/perf/baseline-build-ladder.csv` (базы ещё нет: первый прогон сохраняет `baseline-candidate.csv`, принимает владелец).

**Покрыто 26:** земля (эллипс, многоугольник, круг, берег, заливы, материал), стиль моря, река к морю, озеро, подъём рельефа,
Серия, Путь, Область, Россыпь, автодорога, подписи вдоль дороги и реки, замок, дома, подписи, коридор, комнаты, двери, мебель,
область с правилами и её подпись, 4 вида украшений. **Не покрыто с причиной 6:** погода (только путешествие по гексам), полный
экспорт PNG/тайлов/PDF (огромные файлы), избранное и импорт в каталог (пишут в библиотеку владельца), размер холста (другой
профиль), таймлайн/сравнение/окно игрока (отдельные окна), повторное открытие (отдельный процесс `MAP_BUILD_OPEN`).

## Рецепты для 69 оставшихся (из разведки кода, проверить при вставке)
- **После continents:** Rectangle, GridBlock, BoundedFill через `ActiveLandShape` + перетаскивание; стирание материала
  `SetLandPaintMode(true)`, `SetLandPaintAction("Erase")`, мазок; `SetActiveLandStyle(...)`/`CycleActiveLandStyle()`;
  `SetSeaStrength`, `SetSeaScale`.
- **После rivers:** `RiverDrawMode = Nodes`, `StartRiver`, `AddRiverNode`, `FinishRiver`; приток — первая точка на русле;
  `SelectRiver` → `WaterLevel = Node` → `SelectRiverNode` → `SetSelectedRiverNodeWidth` / `SetSelectedRiverNodeFeature(Waterfall)`;
  `SetRiverWidthScalePercent`, `SetRiverWaterColor`, `DeleteSelectedRiver`.
- **relief:** `AddLandLayer` + мазок + `SetLandLevel(1)`; `SetLandReliefAction(Lower)`; `SetLandCliffMaskMode(true)` +
  `SetLandCliffMaskAction(Wall)` + мазок; выход `SetLandPaintMode(false)`.
- **roads:** `StartRoad`, `AddRoadNode`, `FinishRoad`; `SetSelectedBranchWidth` / `ApplyRoadLook`; `SetCrossingKind(Ford)`
  (переправы — только против рек того же объектного слоя); `DuplicateSelectedRoads`, `DeleteSelectedRoads`.
- **Новая фаза editing (между towns и regions):** `SelectAt`/`ClickForSmoke`, потом `additive: true`; рамка
  `BeginMarquee`→`UpdateMarquee`→`EndMarquee`; `SelectAllByFilter`; перенос `BeginDrag`→`UpdateDragTo`→`EndDrag`;
  `RotateSelectedObject`, `SetSelectionScalePercent`; `CopySelectedObjects`+`PasteObjects`; `GroupSelection`/`UngroupSelection`;
  `ReorderSelection(1,false)`; `MoveSelectionToLayer`; `DeleteSelectedObject`; инструменты Line, Shape, Measure — перетаскиванием
  с `ActiveTool`; Notes — щелчок по пустому месту; слои `AddObjectLayer`, `ToggleLayerVisibility`×2, `ToggleLayerLock`×2,
  `SetActiveLayerOpacity`, `ReorderActiveLayer`, `DuplicateActiveLayer`; маска `ToggleActiveMask` + инструмент Mask + мазок;
  кисть `AddBrushLayer` + инструмент Brush + мазок; штампы `SetStampHueDegrees(commit: true)`, `SetStampFlip`,
  `SetStampShadowStandard`; стирание с Alt: россыпь + `VectorDrawFromCenter = true` + перетаскивание; шаг пути
  `SetEditablePathSpacing` (ждать `PendingPathCalculation`); `SearchLibraryAsync`.
- **regions:** Text `BeginTextPlacement`→`EndTextPlacement`; `SetSelectedTextFontFamily`; кисти области
  `SetAreaDensityBrush(true)`/`SetAreaProtectionBrush(true)` + `Begin/Continue/EndAreaDensityStroke`;
  `SetEditableAreaPattern(Peaks)`, `BakeEditableArea`, красота области (`SetEditableAreaSoftEdge`…, ждать
  `PendingEditableAreaWork`). **`TryAnotherEditableAreaArrangement` на области с защитой — известное падение в Core** (дефект,
  разобрать отдельно).
- **Новая фаза look:** `ApplyStylePreset` (каждый стиль по очереди, Undo после замера); `SetTimeOfDay(t,true)`, `PlaceLightAt`;
  туман `SetFogEnabled(true)` + инструмент Notes + `FogBrush = true` + мазок; `AddFilterLayer` + `SetActiveFilter`.
- **Новая фаза hex:** `AddHexLayer`, `HexTileId`, `HexMode = Place`, `Begin/Continue/EndHexGesture`; `RunHexGeneratorAll`,
  ждать `PendingGeneration`, `ApplyGeneration`.
- **Новая фаза generation:** материк `SelectLayer(land)`, `RunContinentAll`, ждать `PendingGeneration`, `ApplyGeneration`;
  `StartAreaGeneration` + `ApplyGeneration`; город `RegenerateCity()` (НЕ `CreateCityAtAsync` — создаёт файл рядом с картой).
- **history/files:** в каждой фазе `history/undo`, `history/redo`; `files/save` (`SaveAsync`), `files/autosave`
  (`TryAutosaveAsync`), `files/checkpoint` (`CreateCheckpoint`), `files/export-preview` (`BeginExport`→
  `WaitForExportPreviewAsync`→`CloseExport`). Имена строк — как в `CoverageList()`.
- **Разнообразие (владелец 16.11):** каждый прогон — новое зерно и другие каталоги `H:\MapLibrary`/`D:\MapLibrary`, все режимы
  выставления, каждый стиль, все авто-генерации; фиксированные зёрна 101/202 — только для базы сторожа.

## Дополнение 16.11 вечер (ab822f7a): ротация каталогов сделана, остальное — следующей сессии
- Сделано: без `MAP_PERF_CATALOGS` профиль на каждый seed подключает интерьеры + 3–5 случайных каталогов библиотеки (повторное
  открытие — те же); `MAP_BUILD_CATALOGS=preferred` — прежний выбор. Только компиляция, не запускалось. Риск: если в выпавших
  каталогах нет набора с нужным названием, возьмётся любой (на месте гор — мебель) — смотреть снимки первого прогона.
- Не сделано: (1) seed переставляет 5 режимов `StampPlacementMode` между ролями, каждый ≥1 раза за прогон; (2) фаза look —
  `ApplyStylePreset` по номеру прогона (`seed % 21` по `MapStylePreset`), пункт «каждый стиль когда-нибудь замерен» по истории
  `ladder.csv`; (3) все автогенерации отдельными строками `generation/<вид>`: материк, область, гекс-мир, город
  (`RegenerateCity`), региональная расстановка по каждому `RegionalPlacementMode`, «другая раскладка» (не на области с защитой);
  (4) функции стандартной категории (`MapCategories`, `NativeStandardPanelShots`, `NativeMapCategoriesSmoke`) в `CoverageList()`;
  расширить красную проверку на `MapStylePreset`, `RegionalPlacementMode`, `ContinentStep`; (5) `buildProblems[]`: правка
  > 16 мс, FPS или 1 % low < 60, рост памяти за фазу — функция, строка, числа, координаты и масштаб + снимок места
  (`BuildShot` после `SetScale`), вывод списком в `Run-BuildLadder.ps1`; (6) проверить, что ротация подключает новые каталоги
  владельца (`environment.catalogs`, `notes set/*`); (7) первый прогон 10 тыс. случайным seed + снимки, затем `Run-Guard.ps1`.
