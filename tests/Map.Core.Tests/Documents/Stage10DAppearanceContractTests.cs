using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public sealed class Stage10DAppearanceContractTests
{
    // ---------------------------------------------------- UI percent <-> domain offset mapping

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, -1)]
    [InlineData(200, 1)]
    [InlineData(150, 0.5)]
    [InlineData(25, -0.75)]
    public void Percent_maps_to_offset_and_back(double percent, double offset)
    {
        Assert.Equal(offset, ColorAdjustment.PercentToOffset(percent), precision: 12);
        Assert.Equal(percent, ColorAdjustment.OffsetToPercent(offset), precision: 12);
    }

    [Theory]
    [InlineData(-40, -1)]
    [InlineData(260, 1)]
    public void Percent_outside_the_scale_clamps_to_the_domain_bounds(double percent, double offset)
    {
        Assert.Equal(offset, ColorAdjustment.PercentToOffset(percent), precision: 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_percent_is_rejected(double percent)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => ColorAdjustment.PercentToOffset(percent));
        Assert.Contains("finite", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_stored_offset_is_rejected(double offset)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => ColorAdjustment.OffsetToPercent(offset));
        Assert.Contains("finite", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Standard_shadow_requires_all_four_exact_preset_values()
    {
        Assert.True(ShadowPreset.IsStandard(
            ShadowPreset.StandardOffsetX, ShadowPreset.StandardOffsetY,
            ShadowPreset.StandardBlur, ShadowPreset.StandardOpacity));
        Assert.False(ShadowPreset.IsStandard(5, 4, 6, 0.55));
        Assert.False(ShadowPreset.IsStandard(4, 5, 6, 0.55));
        Assert.False(ShadowPreset.IsStandard(4, 4, 7, 0.55));
        Assert.False(ShadowPreset.IsStandard(4, 4, 6, 0.5));
    }

    // ----------------------------------------------------------------- supported blend modes

    [Fact]
    public void The_supported_blend_list_holds_exactly_the_seventeen_render_modes()
    {
        Assert.Equal(
            ["Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten",
             "SoftLight", "HardLight", "Difference", "Add", "ColorBurn", "ColorDodge",
             "Exclusion", "Hue", "Saturation", "Color", "Luminosity"],
            BlendModes.Supported);
        Assert.All(BlendModes.Supported, blend => Assert.True(BlendModes.IsSupported(blend)));
        Assert.False(BlendModes.IsSupported("Glow"));
        Assert.False(BlendModes.IsSupported("normal"));
        Assert.False(BlendModes.IsSupported(null));
        Assert.False(BlendModes.IsSupported(""));
    }

    // ------------------------------------------------------------- new neutral style fields

    [Fact]
    public void A_stamp_style_has_no_reference_size_by_default()
    {
        var style = new StampStyle("a1b2");
        Assert.Null(style.ReferenceWidth);
        Assert.Null(style.ReferenceHeight);
    }

    [Fact]
    public void A_vector_stroke_is_solid_and_unblurred_by_default()
    {
        var stroke = new VectorStrokeStyle();
        Assert.Equal(StrokePattern.Solid, stroke.Pattern);
        Assert.Equal(0, stroke.Blur);
    }

    // ------------------------------------------------------- effect overhang enters bounds

    [Fact]
    public void Text_blur_and_shadow_expand_the_object_bounds()
    {
        var plain = TextObject(new TextStyle());
        var blurred = TextObject(new TextStyle { Blur = 10 });
        var shadowed = TextObject(new TextStyle
        {
            ShadowOpacity = 0.5,
            ShadowOffsetX = 12,
            ShadowOffsetY = 12,
            ShadowBlur = 8,
        });

        var plainBounds = SceneGeometry.BoundsOf(plain);
        var blurredBounds = SceneGeometry.BoundsOf(blurred);
        var shadowedBounds = SceneGeometry.BoundsOf(shadowed);

        Assert.True(blurredBounds.Left < plainBounds.Left);
        Assert.True(blurredBounds.Right > plainBounds.Right);
        Assert.True(shadowedBounds.Right > plainBounds.Right);
        Assert.True(shadowedBounds.Bottom > plainBounds.Bottom);
    }

    [Fact]
    public void Stroke_blur_expands_the_line_bounds()
    {
        var plain = LineObject(new VectorStrokeStyle());
        var blurred = LineObject(new VectorStrokeStyle { Blur = 9 });

        var plainBounds = SceneGeometry.BoundsOf(plain);
        var blurredBounds = SceneGeometry.BoundsOf(blurred);

        Assert.True(blurredBounds.Left < plainBounds.Left);
        Assert.True(blurredBounds.Bottom > plainBounds.Bottom);
    }

    private static SceneObject TextObject(TextStyle style) => new(
        ObjectId.New(), SceneObjectKind.Text, "подпись",
        100, 100, 300, 120, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
    {
        Text = style,
    };

    private static SceneObject LineObject(VectorStrokeStyle stroke) =>
        VectorGeometry.CreateLine(
            ObjectId.New(), "линия",
            [new VectorPoint(0, 0), new VectorPoint(200, 80)], stroke);
}
