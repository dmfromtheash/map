using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>Which part of the asset panel a changed setting belongs to.</summary>
public enum StampSettingGroup
{
    /// <summary>How placement works: mode, auto-select and the mode's own numbers.</summary>
    Placement,

    /// <summary>The next asset itself: scale, rotation, level, opacity, mirroring.</summary>
    NextAsset,

    /// <summary>Shadow, blend, blur, colour and the clip mask.</summary>
    Appearance,

    /// <summary>Random angle, random mirroring and a random variant after each placement.</summary>
    Randomness,
}

public sealed record StampSettingChange(StampSettingGroup Group, string Label);

public sealed partial class EditorViewModel
{
    /// <summary>
    /// Every setting of the next placement that differs from the base values restored by
    /// <see cref="ResetStampToolSettings"/>. The panel counts them on its "К базовым" command and
    /// marks the collapsed sections that hide them, so a folded section can never silently change
    /// what the next click puts on the map.
    /// </summary>
    public IReadOnlyList<StampSettingChange> StampToolChanges => DescribeStampChanges(_stampToolSettings);

    public static IReadOnlyList<StampSettingChange> DescribeStampChanges(StampToolSettings settings)
    {
        var defaults = new StampToolSettings();
        var changes = new List<StampSettingChange>();
        void Add(StampSettingGroup group, string label) => changes.Add(new(group, label));
        static bool Differs(double a, double b) => Math.Abs(a - b) > 0.0001;

        if (settings.Mode != defaults.Mode) Add(StampSettingGroup.Placement, "режим");
        if (settings.AutoSelect != defaults.AutoSelect) Add(StampSettingGroup.Placement, "автовыбор");
        if (settings.Mode == StampPlacementMode.Area && Differs(settings.AreaRadius, defaults.AreaRadius))
            Add(StampSettingGroup.Placement, "радиус");
        if (settings.Mode == StampPlacementMode.Area && Differs(settings.Density, defaults.Density))
            Add(StampSettingGroup.Placement, "плотность");
        if (settings.Mode == StampPlacementMode.Series && Differs(settings.SeriesSpacingPercent, defaults.SeriesSpacingPercent))
            Add(StampSettingGroup.Placement, "шаг серии");

        if (Differs(settings.ScalePercent, defaults.ScalePercent)) Add(StampSettingGroup.NextAsset, $"масштаб {settings.ScalePercent:0}%");
        if (Differs(settings.RotationDegrees, defaults.RotationDegrees)) Add(StampSettingGroup.NextAsset, $"поворот {settings.RotationDegrees:0}°");
        if (settings.Sublayer != defaults.Sublayer) Add(StampSettingGroup.NextAsset, $"уровень {settings.Sublayer:+0;-0;0}");
        if (Differs(settings.ObjectOpacity, defaults.ObjectOpacity)) Add(StampSettingGroup.NextAsset, $"непрозрачность {settings.ObjectOpacity * 100:0}%");
        if (settings.Style is { } style)
        {
            if (style.FlipHorizontal || style.FlipVertical) Add(StampSettingGroup.NextAsset, "отражение");
            if (style.ShadowOpacity > 0) Add(StampSettingGroup.Appearance, "тень");
            if (style.Blend != BlendModes.Normal) Add(StampSettingGroup.Appearance, "смешивание");
            if (style.Blur > 0) Add(StampSettingGroup.Appearance, "размытие");
            if (Differs(style.Hue, 0) || Differs(style.Saturation, 0) || Differs(style.Brightness, 0) || Differs(style.Contrast, 0))
                Add(StampSettingGroup.Appearance, "цвет");
            if (style.ClipMaskEnabled) Add(StampSettingGroup.Appearance, "маска");
        }

        if (settings.RandomAngleStep != defaults.RandomAngleStep) Add(StampSettingGroup.Randomness, "случайный угол");
        if (settings.RandomFlip) Add(StampSettingGroup.Randomness, "случайное отражение");
        if (settings.RandomizeVariant) Add(StampSettingGroup.Randomness, "случайный вариант");
        if (settings.RotationJitterDegrees > 0) Add(StampSettingGroup.Randomness, "разброс поворота");
        if (settings.SizeJitterPercent > 0) Add(StampSettingGroup.Randomness, "разброс размера");
        if (settings.OpacityJitterPercent > 0) Add(StampSettingGroup.Randomness, "разброс непрозрачности");
        if (settings.HueJitterDegrees > 0 || settings.SaturationJitterPercent > 0 || settings.BrightnessJitterPercent > 0)
            Add(StampSettingGroup.Randomness, "разброс цвета");
        return changes;
    }
}
