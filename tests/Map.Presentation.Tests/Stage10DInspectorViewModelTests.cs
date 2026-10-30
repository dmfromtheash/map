using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Stage 10D: the typed appearance inspector — exact UI↔domain mapping, one history entry per
/// gesture, honest refusals over locked/hidden objects and layers, per-type parameters and
/// resets, and the real brush-tip tool state.
/// </summary>
public class Stage10DInspectorViewModelTests
{
    private const string StampSha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    // ------------------------------------------------------------------ construction helpers

    private static SceneObject Stamp(
        double width = 120, double height = 90, bool visible = true, bool locked = false) => new(
        ObjectId.New(), SceneObjectKind.Stamp, "штамп", 100, 100, width, height, 0,
        "#FFFFFFFF", "#00000000", 0, visible, locked)
        {
            Stamp = new StampStyle(StampSha),
        };

    private static SceneObject Text() => new(
        ObjectId.New(), SceneObjectKind.Text, "подпись", 200, 200, 360, 140, 0,
        "#FF20242B", "#FFFFFFFF", 0, true, false)
    {
        Text = new TextStyle { Content = "Надпись" },
    };

    private static SceneObject Line(VectorStrokeStyle? stroke = null) => VectorGeometry.CreateLine(
        ObjectId.New(), "линия",
        [new VectorPoint(300, 300), new VectorPoint(500, 400)], stroke);

    private static SceneObject Shape() => VectorGeometry.CreateShape(
        ObjectId.New(), "фигура", ShapePrimitiveKind.Rectangle,
        new WorldBounds(600, 100, 800, 260));

    private static (EditorViewModel Editor, LayerId LayerId) NewEditor(
        IReadOnlyList<SceneObject> objects, bool layerVisible = true, bool layerLocked = false)
    {
        var layerId = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
                new ObjectLayer(layerId, "Объекты", layerVisible, layerLocked, 1.0, objects),
            ]);
        var editor = new EditorViewModel(
            document, documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog());
        return (editor, layerId);
    }

    private static SceneObject Find(EditorViewModel editor, ObjectId id) =>
        DocumentEditor.FindObject(editor.Session.Document, id)!;

    [Fact]
    public void Fractional_legacy_geometry_survives_display_apply_and_unrelated_single_field_edits()
    {
        var id = ObjectId.New();
        SceneObject Fractional(double angle) => new(id, SceneObjectKind.Stamp, "старый объект",
            100.25, 101.75, 120.25, 90.75, angle, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(StampSha) };
        var original = Fractional(12.5);
        var (editor, _) = NewEditor([original]);
        editor.SelectObject(original.Id);
        Assert.True(editor.CommitInspector());
        Assert.Equal(original, Find(editor, original.Id));
        Assert.Equal(0, editor.Session.UndoDepth);
        editor.RotationText = "45";
        Assert.True(editor.CommitInspector());
        Assert.Equal(Fractional(45), Find(editor, original.Id));
        editor.Undo();
        Assert.Equal(original, Find(editor, original.Id));
        editor.SetStampRotationDegrees(90);
        Assert.Equal(Fractional(90), Find(editor, original.Id));
        editor.Undo();
        Assert.Equal(original, Find(editor, original.Id));
    }

    // ----------------------------------------------------------- percent <-> domain mapping

    [Theory]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(150, 0.5)]
    [InlineData(200, 1)]
    public void Saturation_percent_reaches_the_domain_as_an_offset(double percent, double offset)
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampSaturationPercent(percent);

        Assert.Equal(offset, Find(editor, stamp.Id).Stamp!.Saturation, precision: 12);
    }

    [Fact]
    public void Brightness_and_contrast_share_the_same_percent_scale()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampBrightnessPercent(30);
        editor.SetStampContrastPercent(170);

        var style = Find(editor, stamp.Id).Stamp!;
        Assert.Equal(-0.7, style.Brightness, precision: 12);
        Assert.Equal(0.7, style.Contrast, precision: 12);
    }

    // ------------------------------------------------------ one gesture, one history entry

    [Fact]
    public void A_slider_gesture_of_many_previews_lands_as_exactly_one_undo_entry()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampBlur(1, commit: false);
        editor.SetStampBlur(3, commit: false);
        editor.SetStampBlur(5, commit: false);
        editor.SetStampBlur(6, commit: true);

        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal(6, Find(editor, stamp.Id).Stamp!.Blur);
        Assert.True(editor.Session.Undo());
        Assert.Equal(0, Find(editor, stamp.Id).Stamp!.Blur);
    }

    [Fact]
    public void A_no_op_commit_writes_no_history_entry()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampBlur(0, commit: true);
        editor.SetSelectedSublayer(0);
        editor.SetStampSaturationPercent(100);

        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Two_undo_redo_cycles_replay_the_same_style()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);
        editor.SetStampHueDegrees(35);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            Assert.True(editor.Session.Undo());
            Assert.Equal(0, Find(editor, stamp.Id).Stamp!.Hue);
            Assert.True(editor.Session.Redo());
            Assert.Equal(35, Find(editor, stamp.Id).Stamp!.Hue);
        }
    }

    // ----------------------------------------------------------------------- honest refusal

    [Fact]
    public void A_locked_object_refuses_the_appearance_edit_entirely()
    {
        var stamp = Stamp(locked: true);
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampHueDegrees(35);

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(0, Find(editor, stamp.Id).Stamp!.Hue);
        Assert.NotEqual("", editor.StatusDetail);
    }

    [Fact]
    public void A_locked_layer_refuses_the_appearance_edit_entirely()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp], layerLocked: true);
        editor.SelectObject(stamp.Id);

        editor.SetStampHueDegrees(35);

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(0, Find(editor, stamp.Id).Stamp!.Hue);
    }

    [Fact]
    public void A_hidden_layer_refuses_the_appearance_edit_entirely()
    {
        var text = Text();
        var (editor, _) = NewEditor([text], layerVisible: false);
        editor.SelectObject(text.Id);

        editor.SetTextFontSize(60);

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(32, Find(editor, text.Id).Text!.FontSize);
    }

    // ------------------------------------------------------------------------- group safety

    [Fact]
    public void Style_edits_over_a_grouped_member_keep_group_id_and_name()
    {
        var a = Stamp();
        var b = Stamp();
        var (editor, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Оформление"));
        editor.EnterGroup(Find(editor, a.Id).GroupId!.Value);
        editor.SelectAt(110, 110); // the child under the cursor inside the open group
        var selectedId = Assert.Single(editor.Session.SelectedObjectIds);

        editor.SetStampHueDegrees(20);

        var edited = Find(editor, selectedId);
        Assert.Equal(20, edited.Stamp!.Hue);
        Assert.NotNull(edited.GroupId);
        Assert.Equal("Оформление", edited.GroupName);
    }

    // ----------------------------------------------------------------- stamp scale and size

    [Fact]
    public void A_stamp_without_reference_adopts_its_current_size_as_100_percent()
    {
        var stamp = Stamp(width: 120, height: 90);
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);
        Assert.Equal(100, editor.StampScalePercent);

        editor.SetStampScalePercent(50);

        var scaled = Find(editor, stamp.Id);
        Assert.Equal(60, scaled.Width);
        Assert.Equal(45, scaled.Height);
        Assert.Equal(120, scaled.Stamp!.ReferenceWidth);
        Assert.Equal(90, scaled.Stamp!.ReferenceHeight);
        Assert.Equal(50, editor.StampScalePercent);

        editor.SetStampScalePercent(100);
        Assert.Equal(120, Find(editor, stamp.Id).Width);
        Assert.Equal(90, Find(editor, stamp.Id).Height);
    }

    [Fact]
    public void Scaling_keeps_the_stamp_centre_in_place()
    {
        var stamp = Stamp(width: 120, height: 90);
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampScalePercent(200);

        var scaled = Find(editor, stamp.Id);
        Assert.Equal(160, scaled.X + (scaled.Width / 2));
        Assert.Equal(145, scaled.Y + (scaled.Height / 2));
    }

    [Fact]
    public void Exact_size_respects_the_aspect_lock()
    {
        var stamp = Stamp(width: 120, height: 90);
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        Assert.True(editor.StampAspectLocked);
        editor.SetStampExactSize(width: 240);
        Assert.Equal(180, Find(editor, stamp.Id).Height);

        editor.StampAspectLocked = false;
        editor.SetStampExactSize(height: 90);
        Assert.Equal(240, Find(editor, stamp.Id).Width);
        Assert.Equal(90, Find(editor, stamp.Id).Height);
    }

    [Fact]
    public void The_aspect_lock_applies_to_the_common_width_and_height_fields()
    {
        var stamp = Stamp(width: 120, height: 90);
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);
        editor.RefreshInspector();

        Assert.True(editor.StampAspectLocked);
        editor.WidthText = "240";
        Assert.True(editor.CommitInspector());
        Assert.Equal(240, Find(editor, stamp.Id).Width);
        Assert.Equal(180, Find(editor, stamp.Id).Height);

        editor.StampAspectLocked = false;
        editor.RefreshInspector();
        editor.HeightText = "90";
        Assert.True(editor.CommitInspector());
        Assert.Equal(240, Find(editor, stamp.Id).Width);
        Assert.Equal(90, Find(editor, stamp.Id).Height);
    }

    // ------------------------------------------------------------- stamp flips and sublayer

    [Fact]
    public void Both_flips_are_available_and_independent()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampFlip(vertical: true);
        Assert.True(Find(editor, stamp.Id).Stamp!.FlipVertical);
        Assert.False(Find(editor, stamp.Id).Stamp!.FlipHorizontal);

        editor.SetStampFlip(horizontal: true);
        Assert.True(Find(editor, stamp.Id).Stamp!.FlipHorizontal);
        Assert.True(Find(editor, stamp.Id).Stamp!.FlipVertical);

        editor.SetStampFlip(vertical: false);
        Assert.False(Find(editor, stamp.Id).Stamp!.FlipVertical);
    }

    [Fact]
    public void Sublayer_is_clamped_to_the_supported_band()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetSelectedSublayer(12);
        Assert.Equal(10, Find(editor, stamp.Id).Sublayer);

        editor.SetSelectedSublayer(-12);
        Assert.Equal(-10, Find(editor, stamp.Id).Sublayer);
    }

    [Fact]
    public void Object_opacity_uses_the_percent_scale()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetSelectedOpacityPercent(40);

        Assert.Equal(0.4, Find(editor, stamp.Id).ObjectOpacity, precision: 12);
    }

    // ------------------------------------------------------------------ stamp shadow, blend

    [Fact]
    public void Shadow_presets_and_custom_values_are_real_commands()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampShadowStandard();
        var withShadow = Find(editor, stamp.Id).Stamp!;
        Assert.Equal(0.55, withShadow.ShadowOpacity);
        Assert.Equal(4, withShadow.ShadowOffsetX);

        editor.SetStampShadow(offsetX: -6, blur: 12, opacityPercent: 25);
        var custom = Find(editor, stamp.Id).Stamp!;
        Assert.Equal(-6, custom.ShadowOffsetX);
        Assert.Equal(12, custom.ShadowBlur);
        Assert.Equal(0.25, custom.ShadowOpacity, precision: 12);

        editor.SetStampShadowNone();
        Assert.Equal(0, Find(editor, stamp.Id).Stamp!.ShadowOpacity);
    }

    [Fact]
    public void An_unsupported_blend_is_refused_without_history()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);

        editor.SetStampBlend("Glow");

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal("Normal", Find(editor, stamp.Id).Stamp!.Blend);
        Assert.NotEqual("", editor.InspectorError);

        editor.SetStampBlend("Multiply");
        Assert.Equal("Multiply", Find(editor, stamp.Id).Stamp!.Blend);
    }

    // ------------------------------------------------------------------------- stamp resets

    [Fact]
    public void Color_reset_returns_only_the_four_adjustments_to_neutral()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);
        editor.SetStampHueDegrees(35);
        editor.SetStampSaturationPercent(150);
        editor.SetStampBlur(4);

        editor.ResetStampColorAdjustments();

        var style = Find(editor, stamp.Id).Stamp!;
        Assert.Equal(0, style.Hue);
        Assert.Equal(0, style.Saturation);
        Assert.Equal(4, style.Blur); // not a colour adjustment; stays
    }

    [Fact]
    public void The_full_appearance_reset_is_one_entry_back_to_neutral()
    {
        var stamp = Stamp();
        var (editor, _) = NewEditor([stamp]);
        editor.SelectObject(stamp.Id);
        editor.SetStampFlip(vertical: true);
        editor.SetStampBlend("Screen");
        editor.SetStampBlur(3);
        editor.SetStampShadowStandard();
        editor.SetStampContrastPercent(180);
        editor.SetSelectedOpacityPercent(50);
        var entriesBefore = editor.Session.UndoDepth;

        editor.ResetStampAppearance();

        Assert.Equal(entriesBefore + 1, editor.Session.UndoDepth);
        var reset = Find(editor, stamp.Id);
        var style = reset.Stamp!;
        // Stage 15 separates transform (including flips) from appearance reset.
        Assert.True(style.FlipVertical);
        Assert.Equal("Normal", style.Blend);
        Assert.Equal(0, style.Blur);
        Assert.Equal(0, style.ShadowOpacity);
        Assert.Equal(0, style.Contrast);
        Assert.Equal(1.0, reset.ObjectOpacity);
    }

    // --------------------------------------------------------------------------------- text

    [Fact]
    public void Text_content_weight_outline_curve_and_alignment_commit_as_typed_values()
    {
        var text = Text();
        var (editor, _) = NewEditor([text]);
        editor.SelectObject(text.Id);

        editor.SetTextContent("Первая строка\nвторая строка");
        editor.SetTextBold(true);
        editor.SetTextOutlineColor("#FFAA5500");
        editor.SetTextOutlineWidth(3);
        editor.SetTextCurve(24);
        editor.SetTextAlignment(TextAlignment.Right);
        editor.SetTextLetterSpacing(4);
        editor.SetTextLineHeight(1.6);

        var style = Find(editor, text.Id).Text!;
        Assert.Equal("Первая строка\nвторая строка", style.Content);
        Assert.Equal(700, style.FontWeight);
        Assert.Equal("#FFAA5500", style.OutlineColor);
        Assert.Equal(3, style.OutlineWidth);
        Assert.Equal(24, style.Curve);
        Assert.Equal(TextAlignment.Right, style.Alignment);
        Assert.Equal(4, style.LetterSpacing);
        Assert.Equal(1.6, style.LineHeight);
    }

    [Fact]
    public void An_invalid_colour_is_an_error_message_not_a_history_entry()
    {
        var text = Text();
        var (editor, _) = NewEditor([text]);
        editor.SelectObject(text.Id);

        editor.SetTextFillColor("зелёный");

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal("#FF20242B", Find(editor, text.Id).Text!.FillColor);
        Assert.NotEqual("", editor.InspectorError);

        editor.SetTextFillColor("#112233");
        Assert.Equal("#FF112233", Find(editor, text.Id).Text!.FillColor);
    }

    [Fact]
    public void Letter_spacing_is_clamped_to_the_documented_band()
    {
        var text = Text();
        var (editor, _) = NewEditor([text]);
        editor.SelectObject(text.Id);

        editor.SetTextLetterSpacing(500);

        Assert.Equal(40, Find(editor, text.Id).Text!.LetterSpacing);
    }

    // --------------------------------------------------------------------------------- line

    [Fact]
    public void Line_width_pattern_shadow_and_blur_commit_from_the_inspector()
    {
        var line = Line();
        var (editor, _) = NewEditor([line]);
        editor.SelectObject(line.Id);

        editor.SetStrokeWidth(14);
        editor.SetStrokePattern(StrokePattern.Dashed);
        editor.SetStrokeShadowStandard();
        editor.SetStrokeBlur(2);
        editor.SetStrokeColor("#FF663311");
        editor.SetStrokeOpacityPercent(70);

        var stroke = Find(editor, line.Id).Line!.Stroke;
        Assert.Equal(14, stroke.Width);
        Assert.Equal(StrokePattern.Dashed, stroke.Pattern);
        Assert.Equal(0.55, stroke.ShadowOpacity);
        Assert.Equal(2, stroke.Blur);
        Assert.Equal("#FF663311", stroke.Color);
        Assert.Equal(0.7, stroke.Opacity, precision: 12);
    }

    [Fact]
    public void Hue_and_saturation_apply_only_to_an_asset_stroke()
    {
        var plain = Line();
        var (editor, _) = NewEditor([plain]);
        editor.SelectObject(plain.Id);
        Assert.False(editor.SelectedStrokeHasAsset);

        editor.SetStrokeHueDegrees(45);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(0, Find(editor, plain.Id).Line!.Stroke.Hue);

        var textured = Line(new VectorStrokeStyle { AssetSha256 = StampSha });
        var (editor2, _) = NewEditor([textured]);
        editor2.SelectObject(textured.Id);
        Assert.True(editor2.SelectedStrokeHasAsset);

        editor2.SetStrokeHueDegrees(45);
        editor2.SetStrokeSaturationPercent(160);
        Assert.Equal(45, Find(editor2, textured.Id).Line!.Stroke.Hue);
        Assert.Equal(0.6, Find(editor2, textured.Id).Line!.Stroke.Saturation, precision: 12);
    }

    [Fact]
    public void Line_geometry_is_untouched_by_stroke_edits()
    {
        var line = Line();
        var (editor, _) = NewEditor([line]);
        editor.SelectObject(line.Id);
        var nodesBefore = Find(editor, line.Id).Line!.Nodes;

        editor.SetStrokeWidth(20);
        editor.SetStrokePattern(StrokePattern.Dotted);

        Assert.Equal(
            nodesBefore.Select(n => n.Id),
            Find(editor, line.Id).Line!.Nodes.Select(n => n.Id));
    }

    // -------------------------------------------------------------------------------- shape

    [Fact]
    public void Shape_fill_stroke_and_overall_effects_have_separate_resets()
    {
        var shape = Shape();
        var (editor, _) = NewEditor([shape]);
        editor.SelectObject(shape.Id);

        editor.SetShapeFillColor("#22AA33");
        editor.SetShapeFillOpacityPercent(45);
        editor.SetStrokeColor("#FF0000AA");
        editor.SetStrokeWidth(9);
        editor.SetStrokeBlend("Screen");
        editor.SetStrokeBlur(2);
        editor.SetSelectedOpacityPercent(60);

        var styled = Find(editor, shape.Id);
        Assert.Equal("#FF22AA33", styled.Shape!.Fill.Color);
        Assert.Equal(0.45, styled.Shape!.Fill.Opacity, precision: 12);

        editor.ResetShapeFill();
        var fillReset = Find(editor, shape.Id).Shape!;
        Assert.Equal(new ShapeFillStyle().Color, fillReset.Fill.Color);
        Assert.Equal(1, fillReset.Fill.Opacity);
        Assert.Equal("#FF0000AA", fillReset.Stroke.Color); // stroke untouched

        editor.ResetStrokeAppearance();
        var strokeReset = Find(editor, shape.Id).Shape!.Stroke;
        Assert.Equal(new VectorStrokeStyle().Color, strokeReset.Color);
        Assert.Equal(9, strokeReset.Width); // width is layout, not appearance
        Assert.Equal("Normal", strokeReset.Blend);
        Assert.Equal(0, strokeReset.Blur);

        editor.ResetShapeEffects();
        Assert.Equal(1.0, Find(editor, shape.Id).ObjectOpacity);
    }

    // -------------------------------------------------------------------------------- brush

    [Fact]
    public void The_brush_tip_is_tool_state_with_a_visible_name()
    {
        var (editor, _) = NewEditor([]);
        Assert.Null(editor.BrushTipName);

        var alpha = new byte[16 * 16];
        Array.Fill(alpha, (byte)255);
        editor.SetBrushTip(new BrushTipShape(16, 16, alpha), "test-brush-tip");

        Assert.Equal("test-brush-tip", editor.BrushTipName);
        Assert.NotNull(editor.BrushSettings.Tip);

        editor.ClearBrushTip();
        Assert.Null(editor.BrushTipName);
        Assert.Null(editor.BrushSettings.Tip);
    }

    [Fact]
    public void Beginning_a_brush_stroke_fixes_the_random_seed_before_the_command_exists()
    {
        var (editor, _) = NewEditor([]);
        editor.AddBrushLayer();
        editor.ActiveTool = EditorTool.Brush;
        editor.BrushSettings = editor.BrushSettings with { Scatter = 0.5 };

        Assert.True(editor.BeginStroke(400, 400));
        var seedOfFirst = editor.BrushSettings.Seed;
        editor.ContinueStroke(500, 400);
        editor.EndStroke();

        Assert.True(editor.BeginStroke(600, 600));
        Assert.NotEqual(seedOfFirst, editor.BrushSettings.Seed);
        editor.EndStroke();
    }
}
