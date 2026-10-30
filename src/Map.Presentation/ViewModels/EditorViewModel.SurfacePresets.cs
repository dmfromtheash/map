using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>Named looks of a river. The older drawn looks (stream, lowland, wide, pen atlas) were
/// removed by the owner on 2026-10-30 (DEC-079); saved rivers in them still open and draw.</summary>
public enum RiverLookPreset { Painted, Natural }

/// <summary>Named looks of a road: way of drawing and width together.</summary>
public enum RoadLookPreset { Trail, CountryRoad, Highway, Painterly, Painted }

public sealed partial class EditorViewModel
{
    /// <summary>
    /// One click gives a selected river a complete look instead of thirteen separate numbers.
    /// Style and width change in one undo entry; opacity and the land/sea behaviour the user
    /// chose are kept.
    /// </summary>
    public void ApplyRiverLook(RiverLookPreset preset)
    {
        if (SelectedRiver is not { } river) return;
        var keep = river.Style;
        var (style, scale, name) = preset switch
        {
            RiverLookPreset.Painted => (RiverStyle.Painted(), river.WidthScale, "Река: живописная"),
            _ => (RiverStyle.Natural(), river.WidthScale, "Река: живая"),
        };
        style = style with
        {
            Opacity = keep.Opacity, CutsLand = keep.CutsLand, ClipToLand = keep.ClipToLand,
            MouthFade = keep.MouthFade, TaperSource = style.TaperSource && keep.TaperSource,
        };
        CommitRiver(river with { Style = style, WidthScale = scale }, name);
    }

    /// <summary>A complete road look: drawing preset plus a width that fits it, one undo entry.</summary>
    public void ApplyRoadLook(RoadLookPreset preset)
    {
        if (SelectedRoad is not { } road) return;
        var keep = road.Style;
        var (style, scale, name) = preset switch
        {
            RoadLookPreset.Trail => (RoadStyle.Country() with { EdgeBreak = 0.5, RutOpacity = 0, ShoulderOpacity = 0.3 }, 0.55, "Дорога: тропа"),
            RoadLookPreset.Highway => (RoadStyle.PenRuts(), 1.5, "Дорога: тракт"),
            RoadLookPreset.Painterly => (RoadStyle.Painterly(), 1.2, "Дорога: живописный путь"),
            RoadLookPreset.Painted => (RoadStyle.Painted(), 1.0, "Дорога: как горы владельца"),
            _ => (RoadStyle.Country(), 1.0, "Дорога: просёлок"),
        };
        style = style with
        {
            Opacity = keep.Opacity, TextureId = keep.TextureId, TextureOpacity = keep.TextureOpacity, TextureScale = keep.TextureScale,
            StartCap = keep.StartCap, EndCap = keep.EndCap, TaperLength = keep.TaperLength,
        };
        CommitRoad(road with { Style = style, WidthScale = scale }, name);
    }
}
