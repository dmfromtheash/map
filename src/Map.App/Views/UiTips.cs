using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Map.App.Views;

/// <summary>
/// DEC-070: one directory of what every control does, by its <c>Name</c> - a short title-free line, as the owner asked
/// («при наводке на каждую кнопочку… текст что она такое, максимально короткий и понятный»). A control that sets its own
/// tooltip keeps it; one that has none gets its line here when it is loaded. The same lines are meant to feed the action
/// search (Ctrl+K), so one wording serves both. The native-standard-panel-shots smoke lists any visible control left
/// without a tooltip.
/// </summary>
public static class UiTips
{
    private static readonly Dictionary<string, string> Tips = new(System.StringComparer.Ordinal)
    {
        // top bar and world row
        ["FileMenuButton"] = "Открыть, сохранить как, версии, размер карты, клавиши",
        ["ExportButton"] = "Выгрузить карту картинкой, для печати, VTT или пакетом игрока",
        ["ActionSearchButton"] = "Найти любую функцию по слову и открыть её место (Ctrl+K)",
        ["ActionSearchBox"] = "Слово: красота, роль, море, туман, город…",
        ["WorkspaceDrawTab"] = "Земля, вода, дороги, линии, фигуры, подписи, кисть и маска",
        ["WorkspaceObjectsTab"] = "Каталог ассетов и расстановка: один, серия, россыпь, путь, область",
        ["WorldSectionButton"] = "Заметки и мир: атлас, время, планета, город, сектор, пойнткроул, проверки",
        ["WorkspaceAppearanceTab"] = "Свет, стили и море — лента под картой",
        ["WorldSectionCloseButton"] = "Скрыть список «Мир»",
        ["AtlasUpButton"] = "Перейти к родительской карте атласа",
        ["AtlasCheckButton"] = "Найти расхождения между этой картой и её подкартами",
        ["AtlasRefreshButton"] = "Перерисовать превью подкарт на этой карте",
        ["GeographyCheckButton"] = "Найти реки в гору, дороги в воду и другие ошибки географии",
        ["TimelineButton"] = "Эпохи: какие объекты видны в выбранном году",
        ["PointcrawlButton"] = "Карта мест и путей между ними для игры",
        ["PlanetButton"] = "Сделать карту планетой: шов, полюса, глобус",
        ["BookButton"] = "Книга-атлас и художественный язык карты",
        ["StarSectorButton"] = "Звёздный сектор: системы на гексах",
        ["CityButton"] = "Сгенерировать или перестроить город",
        ["IsometricButton"] = "Показать карту в изометрии",
        ["ScaleSyncButton"] = "Перенести реки и дороги между картой и подкартами",
        ["EraserButton"] = "Стирать нарисованное кистью",

        // layers and notes
        ["AddLayerButton"] = "Новый слой: земля, объекты, кисть, гексы",
        ["AddObjectNoteButton"] = "Заметка, привязанная к выбранному объекту",
        ["AddWorldNoteButton"] = "Заметка в центре видимой части карты",
        ["EntityAtlasSearchButton"] = "Искать карточку по всем картам атласа",
        ["NewEntityKind"] = "Тип новой карточки: персонаж, место, фракция…",
        ["NewTransitionKind"] = "Тип перехода: лестница, пещера, шахта, портал",
        ["CheckTransitionsButton"] = "Найти переходы, у которых нет второго конца",

        // appearance transfer and presets
        ["CopyAppearanceButton"] = "Запомнить вид выбранного объекта",
        ["PreviewAppearanceButton"] = "Показать запомненный вид на выбранном, не меняя карту",
        ["ApplyAppearanceButton"] = "Перенести запомненный вид на выбранное",
        ["CancelAppearanceButton"] = "Убрать предпросмотр вида",
        ["SaveAppearancePresetButton"] = "Сохранить запомненный вид как пресет",
        ["SavePlacementPresetButton"] = "Сохранить текущие настройки размещения как пресет",
        ["RegionalPresetChoice"] = "Выбрать сохранённый пресет",
        ["LoadRegionalPresetButton"] = "Применить выбранный пресет",
        ["RenameRegionalPresetButton"] = "Переименовать выбранный пресет",
        ["DeleteRegionalPresetButton"] = "Удалить выбранный пресет",

        // brush and mask
        ["BrushPresetChoice"] = "Выбрать пресет кисти",
        ["ApplyBrushPresetButton"] = "Взять настройки выбранного пресета",
        ["SaveBrushPresetButton"] = "Сохранить текущую кисть как пресет",
        ["RenameBrushPresetButton"] = "Переименовать выбранный пресет кисти",
        ["DeleteBrushPresetButton"] = "Удалить выбранный пресет кисти",
        ["ClearBrushTipButton"] = "Вернуть круглый отпечаток кисти",
        ["BrushCreateTargetButton"] = "Создать слой для кисти и сразу рисовать",
        ["MaskRectangleButton"] = "Скрыть или показать прямоугольником",
        ["MaskEllipseButton"] = "Скрыть или показать эллипсом",
        ["MaskPolygonButton"] = "Скрыть или показать многоугольником",
        ["MaskRoundButton"] = "Скрыть или показать круглой кистью",

        // land
        ["LandSoftBrushButton"] = "Мягкий край мазка",
        ["LandCrispBrushButton"] = "Чёткий край мазка",
        ["LandDryBrushButton"] = "Рваный сухой край мазка",
        ["LandApplyShapeButton"] = "Добавить выбранную фигуру к суше слоя",
        ["LandClearButton"] = "Стереть всю сушу этого слоя",
        ["LandClearPaintButton"] = "Убрать раскраску, форма суши останется",
        ["LandMoveButton"] = "Сдвинуть выделенный участок суши",
        ["LandMoveSnapToggle"] = "Сдвигать по клеткам сетки",
        ["LandMaterialCatalogButton"] = "Выбрать материал земли из каталога",
        ["LandTexturePickerCatalogButton"] = "Выбрать текстуру из каталога",
        ["NewLandTexturePickerCatalogButton"] = "Выбрать текстуру для новой суши",
        ["OpenLandStyleButton"] = "Заливка, берег и обрывы этого слоя",
        ["ContinentStepHeights"] = "Шаг 1: поле высот материка",
        ["ContinentStepErosion"] = "Шаг 2: размыть горы водой",
        ["ContinentStepClimate"] = "Шаг 3: климат и леса",
        ["ContinentStepRivers"] = "Шаг 4: реки от гор к морю",
        ["ContinentStepSettlements"] = "Шаг 5: поселения и дороги",
        ["ContinentAllButton"] = "Все пять шагов подряд",
        ["ContinentRecalculateButton"] = "Пересчитать с текущими настройками",
        ["ContinentApplyButton"] = "Записать материк на карту (одно Ctrl+Z)",
        ["ContinentCancelButton"] = "Остановить расчёт и убрать превью",

        // rivers
        ["StartLakeButton"] = "Обвести озеро по точкам",
        ["RiverPicker"] = "Выбрать реку или озеро этой карты",
        ["WaterLayerPicker"] = "Перенести выбранную воду на другой слой",
        ["WaterSectionCreate"] = "Начать новую реку или озеро",
        ["WaterSectionEdit"] = "Открыть настройки выбранной воды",
        ["WaterSectionVisibility"] = "Скрыть или показать только выбранную воду; слой остаётся",
        ["WaterSectionLock"] = "Запретить или разрешить правку только выбранной воды",
        ["RoadSectionVisibility"] = "Скрыть или показать только выбранную дорогу; слой остаётся",
        ["RoadSectionLock"] = "Запретить или разрешить правку только выбранной дороги",
        ["LandSectionVisibility"] = "Скрыть или показать выбранную сушу",
        ["LandSectionLock"] = "Запретить или разрешить правку выбранной суши",
        ["RiverTexturePickerCatalogButton"] = "Выбрать текстуру воды из каталога",
        ["RiverAddLabelButton"] = "Подпись, идущая вдоль русла",
        ["ApplyRiverFlowWidthButton"] = "Ширина растёт от истока к устью по притокам",
        ["InsertRiverNodeButton"] = "Новая точка русла посередине участка",
        ["DeleteRiverNodeButton"] = "Убрать выбранную точку русла",
        ["SplitRiverChannelButton"] = "Разрезать русло в выбранной точке",
        ["DetachDownstreamRiverButton"] = "Сделать всё ниже точки отдельной рекой",
        ["TrimRiverBeforeButton"] = "Удалить русло выше выбранной точки",
        ["TrimRiverAfterButton"] = "Удалить русло ниже выбранной точки",
        ["ReverseRiverChannelButton"] = "Поменять исток и устье местами",
        ["SimplifyRiverChannelButton"] = "Убрать лишние точки русла",
        ["SmoothRiverChannelButton"] = "Сгладить изгибы, не добавляя точек",
        ["MeanderRiverChannelButton"] = "Предпросмотр естественных извилин",
        ["PreviewRiverDeltaButton"] = "Предпросмотр дельты в устье",

        // roads
        ["RoadPicker"] = "Выбрать дорогу этой карты",
        ["RoadLayerPicker"] = "Перенести выбранную дорогу на другой слой",
        ["RoadSectionCreate"] = "Начать новую дорогу",
        ["RoadSectionEdit"] = "Открыть настройки выбранной дороги",
        ["RoadTexturePickerCatalogButton"] = "Выбрать текстуру дороги из каталога",
        ["RoadAddLabelButton"] = "Подпись, идущая вдоль дороги",
        ["InsertRoadVertexButton"] = "Новая точка посередине участка",
        ["DeleteRoadVertexButton"] = "Убрать выбранную точку дороги",
        ["DetachRoadVertexButton"] = "Отделить участок от общего перекрёстка",
        ["SplitRoadBranchButton"] = "Разрезать участок в выбранной точке",
        ["ReverseRoadBranchButton"] = "Поменять начало и конец участка",
        ["SimplifyRoadBranchButton"] = "Убрать лишние точки участка",
        ["SmoothRoadBranchButton"] = "Сгладить изгибы, не добавляя точек",
        ["ToggleRoadBranchClosedButton"] = "Соединить конец маршрута с началом",
        ["OpenSurfaceToolButton"] = "Открыть инструмент со всеми настройками выбранного",

        // background generation of a hex world
        ["GenerationApplyButton"] = "Перенести готовый расчёт на карту; отменяется одним шагом",
        ["GenerationCancelButton"] = "Остановить расчёт и убрать предпросмотр; карта не меняется",
        ["GenerationProtectBrushButton"] = "Кисть: отметить гексы, которые расчёт не трогает",
        ["GenerationProtectSelectionButton"] = "Выделенные гексы расчёт не трогает",
        ["GenerationProtectionEraseToggle"] = "Кисть снимает защиту вместо того, чтобы ставить",
        ["GenerationRecalculateButton"] = "Посчитать заново с теми же настройками",
        ["GenerationSelectionOnlyToggle"] = "Считать только выделенные гексы",
        ["GenerationUnprotectSelectionButton"] = "Снять защиту с выделенных гексов",
        ["HexGenAllButton"] = "Все семь шагов подряд: суша, рельеф, климат, реки, поселения, дороги, государства",
        ["HexGenStepLand"] = "Шаг 1: где суша, а где море",
        ["HexGenStepRelief"] = "Шаг 2: холмы и горы",
        ["HexGenStepClimate"] = "Шаг 3: климат и леса",
        ["HexGenStepRivers"] = "Шаг 4: реки по стоку с гор к морю",
        ["HexGenStepSettlements"] = "Шаг 5: города и деревни",
        ["HexGenStepRoads"] = "Шаг 6: дороги между поселениями",
        ["HexGenStepStates"] = "Шаг 7: государства и границы",
        // hex map structure
        ["HexAddLevelAboveButton"] = "Добавить уровень над этим: небо, верхний ярус",
        ["HexAddLevelBelowButton"] = "Добавить подземелье под этим уровнем",
        ["HexLevel_*"] = "Перейти на этот уровень карты",
        ["HexAddRegionButton"] = "Новый регион из выделенных гексов",
        ["HexRegionParent_*"] = "В какой регион входит этот",
        ["HexChildMapButton"] = "Открыть подкарту этого гекса",
        ["HexClearSelectionButton"] = "Снять выделение гексов",
        ["HexSelectAllButton"] = "Выделить все гексы карты",
        ["HexInsertColumnsButton"] = "Добавить две колонки гексов справа",
        ["HexInsertRowsButton"] = "Добавить два ряда гексов снизу",
        ["HexDeleteColumnButton"] = "Убрать крайнюю колонку гексов",
        ["HexDeleteRowButton"] = "Убрать крайний ряд гексов",
        ["HexPathSelect_*"] = "Выбрать этот путь, чтобы править",
        ["HexPathAddLabelButton"] = "Подпись, идущая вдоль выбранного пути",
        ["HexPoiAddButton"] = "Добавить точку интереса на выбранный гекс",
        ["HexPrintButton"] = "Карта гексов в PDF для печати",
        ["HexPresentationChoice"] = "Как показывать гексы: рисунком, цветом или сеткой",
        // the key, fog and journeys
        ["HexKeyCategoryBox"] = "Чем считается этот гекс в ключе: место, опасность, сокровище…",
        ["HexKeySaveButton"] = "Сохранить запись ключа для этого гекса",
        ["HexKeyClearButton"] = "Удалить запись ключа этого гекса",
        ["HexKnowledgeSaveButton"] = "Сохранить, что игроки знают об этом гексе",
        ["HexRevealConditionBox"] = "Когда гекс открывается игрокам: всегда, при посещении, вручную, с даты",
        ["HexRevealAllButton"] = "Открыть игрокам все гексы",
        ["HexHideAllButton"] = "Скрыть от игроков все гексы",
        ["HexPartyToolButton"] = "Поставить или передвинуть отряд игроков",
        ["HexJourneyModeBox"] = "Как идёт отряд: пешком, верхом, по воде",
        ["HexJourneyPickFromButton"] = "Щёлкните по гексу, откуда идти",
        ["HexJourneyPickToButton"] = "Щёлкните по гексу, куда идти",
        ["HexJourneyBuildButton"] = "Проложить маршрут и посчитать дни",
        ["HexJourneyReachBox"] = "Показать, куда отряд дойдёт за 1, 2 и 3 дня",
        ["HexJourneySecretBox"] = "Маршрут виден только ведущему",
        // first steps on an empty map and the light presets
        ["StartAssetsButton"] = "Открыть каталог и поставить первую гору или другой рисунок",
        ["StartLandButton"] = "Взять «Землю» и нарисовать первую сушу",
        ["StartRiverFromEmptyButton"] = "Взять «Реку» и провести первую реку по точкам",
        ["LightPresetTorch"] = "Огонь-факел: маленький тёплый круг света",
        ["LightPresetWindow"] = "Свет из окна: ровный, холодноватый",
        ["LightPresetCampfire"] = "Костёр: большой тёплый круг света с мерцанием",
        // catalog passport
        ["PassportAssetRoleBox"] = "Роль набора: дерево, ель, куст, вершина, склон, дом, башня — по ней ставят лес, хребет и поселение",
    };

    /// <summary>The line for a control name, or null. A key ending in «*» serves every name with that start
    /// (rows numbered at run time, like HexLevel_2).</summary>
    public static string? For(string? name)
    {
        if (name is null) return null;
        if (Tips.TryGetValue(name, out var tip)) return tip;
        var cut = name.IndexOf('_', System.StringComparison.Ordinal);
        return cut > 0 && Tips.TryGetValue(name[..(cut + 1)] + "*", out var family) ? family : null;
    }

    /// <summary>Gives every loaded control without a tooltip its line from the directory (call once at start-up).</summary>
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<Control>((control, _) =>
        {
            if (ToolTip.GetTip(control) is null && For(control.Name) is { } tip) ToolTip.SetTip(control, tip);
        }, RoutingStrategies.Direct);
    }
}
