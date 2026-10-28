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
        ["QuickLandVisibilityButton"] = "Показать или скрыть слой суши",
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
        ["RiverPenStyleButton"] = "Вид реки: тонкие линии пером",
        ["RiverIllustratedStyleButton"] = "Вид реки: рисованная заливка",
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
    };

    /// <summary>The line for a control name, or null.</summary>
    public static string? For(string? name) => name is not null && Tips.TryGetValue(name, out var tip) ? tip : null;

    /// <summary>Gives every loaded control without a tooltip its line from the directory (call once at start-up).</summary>
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<Control>((control, _) =>
        {
            if (ToolTip.GetTip(control) is null && For(control.Name) is { } tip) ToolTip.SetTip(control, tip);
        }, RoutingStrategies.Direct);
    }
}
