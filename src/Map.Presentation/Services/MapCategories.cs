using Map.Core.Documents;

namespace Map.Presentation.Services;

/// <summary>
/// One category of maps: a workshop of Map with its own tools, placement and generation.
/// </summary>
/// <param name="Id">The token the document and the file carry.</param>
/// <param name="Title">The name shown on the category card and in messages.</param>
/// <param name="Tagline">One line under the title.</param>
/// <param name="Description">A paragraph for the detail pane of the create dialog.</param>
/// <param name="Highlights">What is inside, one line each.</param>
/// <param name="ArtworkKey">Approved artwork for the card (Assets/Atlas).</param>
/// <param name="GalleryKey">Approved artwork for the large example in the detail pane.</param>
/// <param name="DefaultWidth">Canvas width the settings step starts from.</param>
/// <param name="DefaultHeight">Canvas height the settings step starts from.</param>
/// <param name="Workshop">Which editing surface the application opens for this category.</param>
public sealed record MapCategoryDefinition(
    MapCategoryId Id,
    string Title,
    string Tagline,
    string Description,
    IReadOnlyList<string> Highlights,
    string ArtworkKey,
    string GalleryKey,
    int DefaultWidth,
    int DefaultHeight,
    string Workshop);

/// <summary>
/// The categories this build knows. The create dialog offers exactly this list; opening a map of
/// a category that is not here is refused with a clear message, because categories do not mix
/// and no workshop can stand in for another.
/// </summary>
/// <remarks>
/// Adding a category means adding one definition here with its own <see cref="MapCategoryDefinition.Workshop"/>
/// token and teaching the application to open that workshop. The canvas, the file container,
/// rendering and the performance core are shared by every workshop; the tools, the placement and
/// the generation are not.
/// </remarks>
public static class MapCategories
{
    /// <summary>The editing surface of the standard constructor: the editor Map has today.</summary>
    public const string StandardWorkshop = "standard-editor";

    public static MapCategoryDefinition Standard { get; } = new(
        MapCategoryId.Standard,
        "Стандартный Конструктор карт",
        "Всё, что умеет Map сегодня: холст любого размера, земля и вода, ассеты, реки, дороги, сетки и экспорт.",
        "Универсальная мастерская для карт мира, региона и поля боя. Земля рисуется кистью с берегом и "
        + "ярусами-обрывами и раскрашивается материалами и текстурами владельца. Объекты расставляются по одному, "
        + "серией, россыпью, по пути и по области из собственного каталога ассетов. Реки, дороги и комнаты — "
        + "редактируемые конструкции. Слои, маски, цветовые фильтры, сетки, именованные версии и экспорт "
        + "в PNG и dd2vtt.",
        [
            "Земля: кисть суши, берег, ярусы-обрывы, материалы и текстуры",
            "Ассеты: каталог, одиночное размещение, серия, россыпь, путь и область",
            "Реки, дороги и комнаты как редактируемые конструкции",
            "Слои, маски, цветовые фильтры, сетки и именованные версии",
            "Экспорт PNG любого размера и dd2vtt для виртуальных столов",
        ],
        ArtworkKey: "map-standard",
        GalleryKey: "home-atlas",
        DefaultWidth: 1920,
        DefaultHeight: 1080,
        Workshop: StandardWorkshop);

    /// <summary>In the order the create dialog lists them.</summary>
    public static IReadOnlyList<MapCategoryDefinition> All { get; } = [Standard];

    public static MapCategoryDefinition? Find(MapCategoryId id) =>
        All.FirstOrDefault(definition => definition.Id == id);

    public static bool IsKnown(MapCategoryId id) => Find(id) is not null;

    /// <summary>The message shown when a file names a category this build cannot open.</summary>
    public static string UnknownCategoryMessage(string title, MapCategoryId id) =>
        $"Карта «{title}» создана в категории «{id}», которой нет в этой сборке Map. Файл не изменён.";
}
