using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>What a rail entry does when the user presses it.</summary>
public enum EditorRailEntryKind
{
    /// <summary>Switches the editor to a tool and nothing else.</summary>
    Tool,

    /// <summary>Opens the asset catalog, and arms placement of whatever the user picks there.</summary>
    Library,
}

/// <summary>One button of the tool rail, named the way UI Automation will find it.</summary>
/// <param name="AutomationId">The control's name, which the native smoke and the tests address.</param>
/// <param name="Tool">The tool this entry represents while it is active.</param>
/// <param name="Kind">Whether pressing it switches a tool or opens the library.</param>
public sealed record EditorRailEntry(string AutomationId, EditorTool Tool, EditorRailEntryKind Kind);

/// <summary>
/// The kind-specific block a selected object adds under the shared one. Exactly one is mounted at
/// a time, which is what keeps a bitmap-only setting - replace asset, colour adjustment, shadow,
/// clip mask - away from an object that has no bitmap.
/// </summary>
public enum ObjectKindSection
{
    None,
    Asset,
    Line,
    Shape,
    Primitive,
    Text,
    Composition,
}

/// <summary>A property of the shared "Объект" block, in the order the user meets it.</summary>
public enum ObjectPrimaryProperty
{
    /// <summary>The thumbnail and the name, so the block says what it is about.</summary>
    Identity,

    /// <summary>The owning object layer, chosen from an explicit list.</summary>
    Layer,

    /// <summary>The object's own level, -10..+10, as a discrete slider.</summary>
    Level,

    Scale,
    Opacity,
    Rotation,

    /// <summary>Выше / Ниже / Наверх / Вниз - order inside one level.</summary>
    Ordering,
}

/// <summary>
/// The shape of the two surfaces the owner decided about: the tool rail and the first block a
/// selected object shows.
/// <para>
/// It lives here rather than inside the view because the order IS the decision - "Ассеты is the
/// one way in, Земля sits directly under it", "слой и уровень come before the rare settings" - and
/// a decision that only exists as the order of some Children collection cannot be checked. The
/// view builds from this list, so the two can never disagree.
/// </para>
/// </summary>
public static class EditorSurfaceLayout
{
    /// <summary>
    /// The rail, in groups separated by a divider. Assets is a Library entry: it opens the catalog
    /// instead of switching to a tool of its own, which is why the old separate "Штамп" button is
    /// gone - it was the second, competing way to do the very same job.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<EditorRailEntry>> RailGroups { get; } =
    [
        [new EditorRailEntry("SelectToolButton", EditorTool.Select, EditorRailEntryKind.Tool)],
        [
            // Земля belongs directly under Ассеты: both put something on the map, and the two are
            // what a new map needs first.
            new EditorRailEntry("AssetsLibraryButton", EditorTool.Stamp, EditorRailEntryKind.Library),
            new EditorRailEntry("LandToolButton", EditorTool.Land, EditorRailEntryKind.Tool),
        ],
        [
            new EditorRailEntry("LineToolButton", EditorTool.Line, EditorRailEntryKind.Tool),
            new EditorRailEntry("ShapeToolButton", EditorTool.Shape, EditorRailEntryKind.Tool),
            new EditorRailEntry("TextToolButton", EditorTool.Text, EditorRailEntryKind.Tool),
            new EditorRailEntry("NotesToolButton", EditorTool.Notes, EditorRailEntryKind.Tool),
        ],
        [
            new EditorRailEntry("BrushToolButton", EditorTool.Brush, EditorRailEntryKind.Tool),
            new EditorRailEntry("MaskToolButton", EditorTool.Mask, EditorRailEntryKind.Tool),
        ],
    ];

    /// <summary>Every rail entry, dividers dropped, in the order they are stacked.</summary>
    public static IReadOnlyList<EditorRailEntry> Rail { get; } = [.. RailGroups.SelectMany(group => group)];

    /// <summary>
    /// The first block of a selected object. Layer and level lead because they decide what covers
    /// what; scale, opacity and rotation follow because they are touched constantly. Everything
    /// rarer - geometry, appearance, shadow, blend, blur, colour, clip mask - stays below in its
    /// own block and is not named here.
    /// </summary>
    public static IReadOnlyList<ObjectPrimaryProperty> ObjectPrimaryOrder { get; } =
    [
        ObjectPrimaryProperty.Identity,
        ObjectPrimaryProperty.Layer,
        ObjectPrimaryProperty.Level,
        ObjectPrimaryProperty.Scale,
        ObjectPrimaryProperty.Opacity,
        ObjectPrimaryProperty.Rotation,
        ObjectPrimaryProperty.Ordering,
    ];

    /// <summary>The rail entry that owns a tool, or null when the tool has no button of its own.</summary>
    public static EditorRailEntry? EntryFor(EditorTool tool) =>
        Rail.FirstOrDefault(entry => entry.Tool == tool);

    /// <summary>
    /// Which kind-specific block an object shows. Placeholders share the asset block because a
    /// missing file is still an asset object and must keep every setting it had.
    /// </summary>
    public static ObjectKindSection SectionFor(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Stamp or SceneObjectKind.Placeholder => ObjectKindSection.Asset,
        SceneObjectKind.Line => ObjectKindSection.Line,
        SceneObjectKind.Shape => ObjectKindSection.Shape,
        SceneObjectKind.Rectangle or SceneObjectKind.Ellipse => ObjectKindSection.Primitive,
        SceneObjectKind.Text => ObjectKindSection.Text,
        SceneObjectKind.Composition => ObjectKindSection.Composition,
        _ => ObjectKindSection.None,
    };
}
