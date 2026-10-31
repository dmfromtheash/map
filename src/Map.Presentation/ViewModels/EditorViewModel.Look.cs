using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// «Вид» (owner's pick 2026-10-31, variant 1 of https://claude.ai/artifact/UA3ciWpYB3UVgFbX56uqNo): pointing at a light,
/// a style or a sea shows the whole map in it at once, and «Сравнить» held shows it without style and light. Nothing
/// here changes the map or its history; the shown document is valid only for the map it was made from.
/// </summary>
public sealed partial class EditorViewModel
{
    private MapDocument? _lookPreview;
    private MapDocument? _lookPreviewBase;

    /// <summary>The trial look, while the map it was made from is still the map.</summary>
    private MapDocument? ValidLookPreview => ReferenceEquals(_lookPreviewBase, Session.Document) ? _lookPreview : null;

    /// <summary>True while the canvas shows a trial look instead of the map as it is.</summary>
    public bool LookPreviewShown => ValidLookPreview is not null;

    /// <summary>Shows <paramref name="look"/> (the open map in another light, style or sea) on the canvas; null ends it.</summary>
    public void PreviewLook(MapDocument? look)
    {
        if (look is null && _lookPreview is null) return;
        _lookPreview = look;
        _lookPreviewBase = look is null ? null : Session.Document;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>«Сравнить»: the open map with no style and no light, as it was drawn.</summary>
    public MapDocument LookWithoutEffects() => Session.Document with { Style = null, Lighting = null };
}
