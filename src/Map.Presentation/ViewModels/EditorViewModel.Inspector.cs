using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- inspector

    public string XText { get => _xText; set => SetField(ref _xText, value); }
    public string YText { get => _yText; set => SetField(ref _yText, value); }
    public string WidthText { get => _widthText; set => SetField(ref _widthText, value); }
    public string HeightText { get => _heightText; set => SetField(ref _heightText, value); }
    public string RotationText { get => _rotationText; set => SetField(ref _rotationText, value); }

    public string InspectorError
    {
        get => _inspectorError;
        private set => SetField(ref _inspectorError, value);
    }

    public bool HasEditableSelection => EditableSelection() is not null;

    /// <summary>The view funnels its local input-parse failures into the one visible error line.</summary>
    public void ReportInspectorError(string message) => InspectorError = message ?? "";

    public void RefreshInspector()
    {
        var selected = Session.SelectedObject;
        XText = selected is null ? "" : WholeGeometryValue(selected.X).ToString("0", CultureInfo.InvariantCulture);
        YText = selected is null ? "" : WholeGeometryValue(selected.Y).ToString("0", CultureInfo.InvariantCulture);
        WidthText = selected is null ? "" : WholeGeometryValue(selected.Width).ToString("0", CultureInfo.InvariantCulture);
        HeightText = selected is null ? "" : WholeGeometryValue(selected.Height).ToString("0", CultureInfo.InvariantCulture);
        RotationText = selected is null ? "" : WholeGeometryValue(selected.RotationDegrees).ToString("0", CultureInfo.InvariantCulture);
        InspectorError = "";
        Raise(nameof(HasEditableSelection));
    }

    /// <summary>
    /// Applies the numeric fields as ONE transform command. Invalid input produces a message and
    /// no command; unchanged values produce no history entry at all.
    /// </summary>
    public bool CommitInspector()
    {
        var before = EditableSelection();
        if (before is null)
        {
            InspectorError = "Выбранный объект нельзя редактировать.";
            return false;
        }

        if (!TryParseWhole(XText, out var x) || !TryParseWhole(YText, out var y)
            || !TryParseWhole(WidthText, out var width) || !TryParseWhole(HeightText, out var height)
            || !TryParseWhole(RotationText, out var rotation)
            || width <= 0 || height <= 0)
        {
            InspectorError = "Поля геометрии принимают только целые числа; ширина и высота должны быть больше нуля.";
            return false;
        }

        // Displaying legacy fractional data as integers is not an edit of the hidden precision.
        // Only a field whose entered value differs from its displayed baseline is changed.
        if (x == WholeGeometryValue(before.X)) x = before.X;
        if (y == WholeGeometryValue(before.Y)) y = before.Y;
        if (width == WholeGeometryValue(before.Width)) width = before.Width;
        if (height == WholeGeometryValue(before.Height)) height = before.Height;
        if (rotation == WholeGeometryValue(before.RotationDegrees)) rotation = before.RotationDegrees;

        // A locked stamp aspect fills in the untouched dimension from the touched one, so the
        // numeric fields and the proportional scale can never disagree about the shape.
        if (before.Stamp is not null && StampAspectLocked && before.Width > 0 && before.Height > 0)
        {
            if (width != before.Width && height == before.Height)
            {
                height = WholeGeometryValue(width * before.Height / before.Width);
            }
            else if (height != before.Height && width == before.Width)
            {
                width = WholeGeometryValue(height * before.Width / before.Height);
            }
        }

        var after = WithGeometry(before, x, y, width, height, rotation);

        if (after == before)
        {
            InspectorError = "";
            return true;
        }

        Session.Execute(new TransformObjectCommand(before, after));
        AfterEdit();
        return true;
    }

    private static bool TryParseWhole(string text, out double value) =>
        double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static double WholeGeometryValue(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized <= -180) normalized += 360;
        if (normalized > 180) normalized -= 360;
        return normalized;
    }

    private static double NormalizeUnsignedAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized < 0) normalized += 360;
        return normalized;
    }

    private static SceneObject WithGeometry(
        SceneObject source, double x, double y, double width, double height, double rotation) =>
        new SceneObject(
            source.Id, source.Kind, source.Name,
            QuantizeChanged(x, source.X), QuantizeChanged(y, source.Y),
            QuantizeChanged(width, source.Width, size: true),
            QuantizeChanged(height, source.Height, size: true),
            QuantizeChanged(rotation, source.RotationDegrees),
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            Decoration = source.Decoration,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        }.KeepingMeaningOf(source);

    private static double QuantizeChanged(double value, double original, bool size = false) =>
        value == original ? original
            : size ? Math.Max(1, WholeGeometryValue(value)) : WholeGeometryValue(value);

    private static IReadOnlyList<SceneObject> QuantizeTransformedGeometries(
        IReadOnlyList<SceneObject> before, IEnumerable<SceneObject> proposed)
    {
        var originals = before.ToDictionary(item => item.Id);
        return [.. proposed.Select(item =>
        {
            var original = originals[item.Id];
            return WithGeometry(item,
                QuantizeChanged(item.X, original.X), QuantizeChanged(item.Y, original.Y),
                QuantizeChanged(item.Width, original.Width, size: true),
                QuantizeChanged(item.Height, original.Height, size: true),
                QuantizeChanged(item.RotationDegrees, original.RotationDegrees));
        })];
    }

    private static SceneObject CopyObject(
        SceneObject source, bool? visible = null, bool? locked = null,
        string? fillColor = null, string? strokeColor = null, double? strokeWidth = null) =>
        new SceneObject(
            source.Id, source.Kind, source.Name,
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            fillColor ?? source.FillColor, strokeColor ?? source.StrokeColor,
            strokeWidth ?? source.StrokeWidth,
            visible ?? source.Visible, locked ?? source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            Decoration = source.Decoration,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        }.KeepingMeaningOf(source);

    // ------------------------------------------------- Stage 10D appearance inspector

    private SceneObject? _appearanceGestureBefore;
    private bool _stampAspectLocked = true;
    private string? _brushTipName;
    private string? _brushTextureName;
    private string? _brushTextureSha256;
    private BrushPresetKind _brushPresetKind = BrushPresetKind.RoundSoft;
    private IReadOnlyList<LocalBrushPreset> _brushPresets = [];
    private int _brushSeedCounter;

    /// <summary>
    /// The one editable single selection for a style edit. Unlike the pure object-flag check,
    /// this honours the layer too: an object on a hidden or locked layer refuses entirely.
    /// </summary>
    private SceneObject? EditableAppearanceSelection()
    {
        if (Session.SelectedObjectIds.Count != 1 || Session.SelectedObject is not { } selected)
        {
            return null;
        }
        if (PlacementOwnership.Owner(Session.Document, selected.Id) is not null)
        { StatusDetail = PlacementOwnership.DetachFirst; return null; }

        if (!IsObjectAvailable(selected))
        {
            StatusDetail = "Объект или его слой скрыт либо заблокирован — изменение отклонено.";
            return null;
        }

        return selected;
    }

    /// <summary>
    /// One appearance edit. commit=false previews live from the gesture's captured "before";
    /// commit=true lands the whole gesture (or a direct edit) as exactly ONE history entry.
    /// A change that returns the same value writes no history at all.
    /// </summary>
    private void EditAppearance(string name, bool commit, Func<SceneObject, SceneObject> change)
    {
        // A live gesture belongs to the object it started on. If the selection has moved on
        // since - or the gesture was never closed - the stale "before" would send this edit to the
        // previous object, which is exactly how a colour landed on the wrong shape.
        var selected = EditableAppearanceSelection();
        if (_appearanceGestureBefore is { } pending && pending.Id != selected?.Id)
        {
            _appearanceGestureBefore = null;
            DragPreview = null;
        }

        var before = _appearanceGestureBefore ?? selected;
        if (before is null)
        {
            return;
        }

        if (!commit)
        {
            _appearanceGestureBefore = before;
            DragPreview = new DragPreview([before], [change(before)], DragMode.Move, null, 0, 0, null);
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _appearanceGestureBefore = null;
        DragPreview = null;
        var after = change(before);
        if (ReferenceEquals(after, before) || after.Equals(before))
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return; // no-op: no history entry
        }

        Session.Execute(new ReplaceObjectsCommand([before], [after], name));
        AfterEdit();
    }

    /// <summary>Abandons a live slider gesture without touching the document or the history.</summary>
    public void CancelAppearanceGesture()
    {
        _appearanceGestureBefore = null;
        _selectionGestureBefore = null;
        DragPreview = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" → canonical "#AARRGGBB"; anything else is null.</summary>
    private static string? NormalizeColor(string? text) => AppearanceColor.Normalize(text);

    private static string? NormalizeSha256(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private string? RequireColorInput(string? text)
    {
        var color = NormalizeColor(text);
        if (color is null)
        {
            InspectorError = "Цвет задаётся как #RRGGBB или #AARRGGBB.";
        }

        return color;
    }

    private bool RequireSupportedBlend(string blend)
    {
        if (BlendModes.IsSupported(blend))
        {
            return true;
        }

        InspectorError = $"Режим смешивания «{blend}» не поддерживается.";
        return false;
    }

    // ------------------------------------------------------------- common object properties

    public void SetSelectedSublayer(int value) => EditAppearance("Object sublayer", commit: true,
        before => before.Sublayer == Math.Clamp(value, -10, 10)
            ? before
            : CopyObject(before) with { Sublayer = Math.Clamp(value, -10, 10) });

    public void SetSelectedOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return;
        }

        var opacity = Math.Clamp(percent, 0, 100) / 100.0;
        EditAppearance("Object opacity", commit, before => before.ObjectOpacity == opacity
            ? before
            : CopyObject(before) with { ObjectOpacity = opacity });
    }

    // ----------------------------------------------------------------------- stamp: scale

    public double? StampRotationDegrees => IsEditingFutureStamp
        ? NormalizeUnsignedAngle(WholeGeometryValue(_stampToolSettings.RotationDegrees))
        : Session.SelectedObject is { Stamp: not null } selected
            ? NormalizeUnsignedAngle(WholeGeometryValue(selected.RotationDegrees)) : null;

    public int? StampSublayer => IsEditingFutureStamp
        ? _stampToolSettings.Sublayer
        : Session.SelectedObject is { Stamp: not null } selected ? selected.Sublayer : null;

    public double? StampOpacityPercent => IsEditingFutureStamp
        ? _stampToolSettings.ObjectOpacity * 100
        : Session.SelectedObject is { Stamp: not null } selected ? selected.ObjectOpacity * 100 : null;

    public void SetStampRotationDegrees(double degrees, bool commit = true)
    {
        if (!double.IsFinite(degrees))
        {
            InspectorError = "Поворот задаётся числом 0..359 градусов.";
            return;
        }

        var normalized = NormalizeUnsignedAngle(WholeGeometryValue(degrees));
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { RotationDegrees = normalized }, commit);
            return;
        }

        EditAppearance("Stamp rotation", commit, before => before.Stamp is null
            ? before
            : WithGeometry(before, before.X, before.Y, before.Width, before.Height, normalized));
    }

    public void SetStampSublayer(int value, bool commit = true)
    {
        var bounded = Math.Clamp(value, -10, 10);
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { Sublayer = bounded }, persist: commit);
            return;
        }

        EditAppearance("Object sublayer", commit,
            before => before.Sublayer == bounded ? before : CopyObject(before) with { Sublayer = bounded });
    }

    public void SetStampOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return;
        }

        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(
                settings => settings with { ObjectOpacity = Math.Clamp(percent, 0, 100) / 100.0 }, commit);
            return;
        }

        SetSelectedOpacityPercent(percent, commit);
    }

    /// <summary>
    /// Scale against the stamp's 100 % reference. An old file without a reference reports its
    /// current size as the baseline, and the first scale edit adopts it permanently.
    /// </summary>
    public double? StampScalePercent
    {
        get
        {
            if (IsEditingFutureStamp)
            {
                return WholeGeometryValue(_stampToolSettings.ScalePercent);
            }

            if (Session.SelectedObject is not { Stamp: { } stamp } selected)
            {
                return null;
            }

            var reference = stamp.ReferenceWidth ?? selected.Width;
            return reference <= 0 ? null : WholeGeometryValue(selected.Width / reference * 100);
        }
    }

    /// <summary>Editor-only preference: exact W/H edits keep the stamp's proportions.</summary>
    public bool StampAspectLocked
    {
        get => _stampAspectLocked;
        set => SetField(ref _stampAspectLocked, value);
    }

    public void SetStampScalePercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Масштаб задаётся числом в процентах.";
            return;
        }

        var bounded = Math.Clamp(WholeGeometryValue(percent), 10, 400);
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { ScalePercent = bounded }, commit);
            return;
        }

        var factor = bounded / 100.0;
        EditAppearance("Stamp scale", commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var referenceWidth = stamp.ReferenceWidth ?? before.Width;
            var referenceHeight = stamp.ReferenceHeight ?? before.Height;
            var width = Math.Max(0.5, referenceWidth * factor);
            var height = Math.Max(0.5, referenceHeight * factor);
            if (width == before.Width && height == before.Height && stamp.ReferenceWidth is not null)
            {
                return before;
            }

            var centreX = before.X + (before.Width / 2);
            var centreY = before.Y + (before.Height / 2);
            return WithGeometry(
                before, centreX - (width / 2), centreY - (height / 2), width, height,
                before.RotationDegrees) with
            {
                Stamp = stamp with { ReferenceWidth = referenceWidth, ReferenceHeight = referenceHeight },
            };
        });
    }

    /// <summary>Exact width/height; the locked aspect fills in the other dimension.</summary>
    public void SetStampExactSize(double? width = null, double? height = null)
    {
        if (width is { } w && (!double.IsFinite(w) || w <= 0)
            || height is { } h && (!double.IsFinite(h) || h <= 0))
        {
            InspectorError = "Размер должен быть положительным целым числом.";
            return;
        }

        width = width is { } wholeWidth ? WholeGeometryValue(wholeWidth) : null;
        height = height is { } wholeHeight ? WholeGeometryValue(wholeHeight) : null;

        EditAppearance("Stamp size", commit: true, before =>
        {
            if (before.Stamp is null)
            {
                return before;
            }

            var aspect = before.Width <= 0 ? 1 : before.Height / before.Width;
            var newWidth = width ?? (StampAspectLocked && height is { } setH
                ? Math.Max(0.5, setH / Math.Max(0.0001, aspect))
                : before.Width);
            var newHeight = height ?? (StampAspectLocked && width is { } setW
                ? Math.Max(0.5, setW * aspect)
                : before.Height);
            if (newWidth == before.Width && newHeight == before.Height)
            {
                return before;
            }

            var centreX = before.X + (before.Width / 2);
            var centreY = before.Y + (before.Height / 2);
            return WithGeometry(
                before, centreX - (newWidth / 2), centreY - (newHeight / 2),
                newWidth, newHeight, before.RotationDegrees);
        });
    }

    // ------------------------------------------------------------- stamp: flips and effects

    private void EditStamp(string name, bool commit, Func<StampStyle, StampStyle> change)
    {
        if (IsEditingFutureStamp)
        {
            if (_stampToolSettings.Style is not { } style)
            {
                return;
            }

            UpdateStampToolSettings(settings => settings with { Style = change(style) }, commit);
            return;
        }

        EditAppearance(name, commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var changed = change(stamp);
            return changed.Equals(stamp) ? before : CopyObject(before) with { Stamp = changed };
        });
    }

    public void SetStampFlip(bool? horizontal = null, bool? vertical = null) =>
        EditStamp("Stamp flip", commit: true, stamp => stamp with
        {
            FlipHorizontal = horizontal ?? stamp.FlipHorizontal,
            FlipVertical = vertical ?? stamp.FlipVertical,
        });

    public void SetStampShadowNone() =>
        EditStamp("Stamp shadow", commit: true, stamp => stamp with { ShadowOpacity = 0 });

    public void SetStampShadowStandard() =>
        EditStamp("Stamp shadow", commit: true, stamp => stamp with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
            ShadowPlacement = StampShadowPlacement.Object,
            ShadowColor = AppearanceColor.OpaqueBlack,
        });

    public void SetStampShadowPlacement(StampShadowPlacement placement) =>
        EditStamp("Stamp shadow placement", commit: true, stamp => stamp with
        {
            ShadowPlacement = Enum.IsDefined(placement) ? placement : StampShadowPlacement.Object,
            ShadowOpacity = stamp.ShadowOpacity > 0 ? stamp.ShadowOpacity : ShadowPreset.StandardOpacity,
        });

    public void SetStampShadowColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditStamp("Stamp shadow colour", commit, stamp => stamp with { ShadowColor = color });
        }
    }

    public void SetStampShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditStamp("Stamp shadow", commit, stamp => stamp with
        {
            ShadowOffsetX = Finite(offsetX, stamp.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, stamp.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, stamp.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, stamp.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetStampBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditStamp("Stamp blend", commit: true, stamp => stamp with { Blend = blend });
        }
    }

    public void SetStampBlur(double sigma, bool commit = true) =>
        EditStamp("Stamp blur", commit, stamp => stamp with { Blur = Finite(sigma, stamp.Blur, 0, 32) });

    public void SetStampHueDegrees(double degrees, bool commit = true) =>
        EditStamp("Stamp hue", commit, stamp => stamp with { Hue = Finite(degrees, stamp.Hue, -180, 180) });

    public void SetStampSaturationPercent(double percent, bool commit = true) =>
        EditStamp("Stamp saturation", commit, stamp => stamp with
        {
            Saturation = PercentOffset(percent, stamp.Saturation),
        });

    public void SetStampBrightnessPercent(double percent, bool commit = true) =>
        EditStamp("Stamp brightness", commit, stamp => stamp with
        {
            Brightness = PercentOffset(percent, stamp.Brightness),
        });

    public void SetStampContrastPercent(double percent, bool commit = true) =>
        EditStamp("Stamp contrast", commit, stamp => stamp with
        {
            Contrast = PercentOffset(percent, stamp.Contrast),
        });

    /// <summary>Hue/Saturation/Brightness/Contrast back to neutral, one entry, nothing else.</summary>
    public void ResetStampColorAdjustments() =>
        EditStamp("Reset stamp colors", commit: true, stamp => stamp with
        {
            Hue = 0,
            Saturation = 0,
            Brightness = 0,
            Contrast = 0,
        });

    /// <summary>The whole appearance back to neutral in one entry; transform stays untouched.</summary>
    public void ResetStampAppearance() =>
        ResetStampAppearanceCore();

    private void ResetStampAppearanceCore()
    {
        if (IsEditingFutureStamp && _stampToolSettings.Style is { } toolStyle)
        {
            UpdateStampToolSettings(settings => settings with
            {
                ObjectOpacity = 1,
                Style = new StampStyle(toolStyle.AssetSha256)
                {
                    ReferenceWidth = toolStyle.ReferenceWidth,
                    ReferenceHeight = toolStyle.ReferenceHeight,
                    FlipHorizontal = toolStyle.FlipHorizontal,
                    FlipVertical = toolStyle.FlipVertical,
                    MaskAssetSha256 = toolStyle.MaskAssetSha256,
                    ClipMaskEnabled = toolStyle.ClipMaskEnabled,
                },
            }, persist: true);
            return;
        }

        EditAppearance("Reset stamp appearance", commit: true, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var neutral = new StampStyle(stamp.AssetSha256)
            {
                ReferenceWidth = stamp.ReferenceWidth,
                ReferenceHeight = stamp.ReferenceHeight,
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                MaskAssetSha256 = stamp.MaskAssetSha256,
                ClipMaskEnabled = stamp.ClipMaskEnabled,
            };
            return neutral.Equals(stamp) && before.ObjectOpacity == 1.0
                ? before
                : CopyObject(before) with { Stamp = neutral, ObjectOpacity = 1.0 };
        });
    }

    private double PercentOffset(double percent, double fallback)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Значение задаётся в процентах 0..200.";
            return fallback;
        }

        return ColorAdjustment.PercentToOffset(percent);
    }

    private static double Finite(double? value, double fallback, double min, double max) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    // ----------------------------------------------------------------------------- text

    private void EditText(string name, bool commit, Func<TextStyle, TextStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Text is not { } text)
            {
                return before;
            }

            var changed = change(text);
            return changed.Equals(text) ? before : CopyObject(before) with { Text = changed };
        });

    public void SetTextContent(string content) =>
        EditText("Text content", commit: true, text => text with { Content = content ?? "" });

    public void SetTextBold(bool bold) =>
        EditText("Text weight", commit: true, text => text with { FontWeight = bold ? 700 : 400 });

    public void SetTextFontSize(double size, bool commit = true) =>
        EditText("Text size", commit, text => text with { FontSize = Finite(size, text.FontSize, 1, 512) });

    public void SetTextFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text fill", commit, text => text with { FillColor = color });
        }
    }

    public void SetTextOutlineColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text outline", commit, text => text with { OutlineColor = color });
        }
    }

    public void SetTextOutlineWidth(double width, bool commit = true) =>
        EditText("Text outline", commit, text => text with
        {
            OutlineWidth = Finite(width, text.OutlineWidth, 0, 32),
        });

    public void SetTextShadowNone() =>
        EditText("Text shadow", commit: true, text => text with { ShadowOpacity = 0 });

    public void SetTextShadowStandard() =>
        EditText("Text shadow", commit: true, text => text with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
        });

    public void SetTextShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditText("Text shadow", commit, text => text with
        {
            ShadowOffsetX = Finite(offsetX, text.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, text.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, text.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, text.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetTextCurve(double value, bool commit = true) =>
        EditText("Text curve", commit, text => text with { Curve = Finite(value, text.Curve, -100, 100) });

    public void SetTextLetterSpacing(double value, bool commit = true) =>
        EditText("Text letter spacing", commit, text => text with
        {
            LetterSpacing = Finite(value, text.LetterSpacing, 0, 40),
        });

    public void SetTextLineHeight(double value, bool commit = true) =>
        EditText("Text line height", commit, text => text with
        {
            LineHeight = Finite(value, text.LineHeight, 0.5, 3),
        });

    public void SetTextAlignment(TextAlignment alignment) =>
        EditText("Text alignment", commit: true, text => text with { Alignment = alignment });

    public void SetTextFlip(bool? horizontal = null, bool? vertical = null) =>
        EditText("Text flip", commit: true, text => text with
        {
            FlipHorizontal = horizontal ?? text.FlipHorizontal,
            FlipVertical = vertical ?? text.FlipVertical,
        });

    public void SetTextBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditText("Text blend", commit: true, text => text with { Blend = blend });
        }
    }

    public void SetTextBlur(double sigma, bool commit = true) =>
        EditText("Text blur", commit, text => text with { Blur = Finite(sigma, text.Blur, 0, 32) });

    // -------------------------------------------------------- vector stroke (line or shape)

    /// <summary>True when the selected Line/Shape strokes with a repeating asset.</summary>
    public bool SelectedStrokeHasAsset =>
        (Session.SelectedObject?.Line?.Stroke ?? Session.SelectedObject?.Shape?.Stroke)
            ?.AssetSha256 is not null;

    private static SceneObject WithStroke(SceneObject before, VectorStrokeStyle stroke) =>
        before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, stroke, line.Portals, line.Closed) }
            : before.Shape is { } shape
                ? before with { Shape = new ShapeStyle(shape.Regions, shape.Fill, stroke, shape.Portals) }
                : before;

    private void EditStroke(string name, bool commit, Func<VectorStrokeStyle, VectorStrokeStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
            if (stroke is null)
            {
                return before;
            }

            var changed = change(stroke);
            return changed.Equals(stroke) ? before : WithStroke(before, changed);
        });

    public void SetStrokeColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditStroke("Stroke color", commit, stroke => stroke with { Color = color });
        }
    }

    public void SetStrokeWidth(double width, bool commit = true) =>
        EditStroke("Stroke width", commit, stroke => stroke with
        {
            Width = Finite(width, stroke.Width, 0.1, 256),
        });

    public void SetStrokeOpacityPercent(double percent, bool commit = true) =>
        EditStroke("Stroke opacity", commit, stroke => stroke with
        {
            Opacity = Finite(percent, stroke.Opacity * 100, 0, 100) / 100.0,
        });

    public void SetStrokeBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditStroke("Stroke blend", commit: true, stroke => stroke with { Blend = blend });
        }
    }

    public void SetStrokeBlur(double sigma, bool commit = true) =>
        EditStroke("Stroke blur", commit, stroke => stroke with { Blur = Finite(sigma, stroke.Blur, 0, 32) });

    public void SetStrokeOffset(double offset, bool commit = true) =>
        EditStroke("Stroke offset", commit, stroke => stroke with
        {
            Offset = Finite(offset, stroke.Offset, -128, 128),
        });

    public void SetStrokeEndCap(VectorCap cap) =>
        EditStroke("Stroke end cap", commit: true, stroke => stroke with { EndCap = cap });

    public void SetStrokeNodeCap(VectorCap cap) =>
        EditStroke("Stroke node cap", commit: true, stroke => stroke with { NodeCap = cap });

    public void SetStrokePattern(StrokePattern pattern) =>
        EditStroke("Stroke pattern", commit: true, stroke => stroke with { Pattern = pattern });

    public void SetStrokeShadowNone() =>
        EditStroke("Stroke shadow", commit: true, stroke => stroke with { ShadowOpacity = 0 });

    public void SetStrokeShadowStandard() =>
        EditStroke("Stroke shadow", commit: true, stroke => stroke with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
        });

    public void SetStrokeShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditStroke("Stroke shadow", commit, stroke => stroke with
        {
            ShadowOffsetX = Finite(offsetX, stroke.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, stroke.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, stroke.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, stroke.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetStrokeHueDegrees(double degrees, bool commit = true)
    {
        if (!SelectedStrokeHasAsset)
        {
            StatusDetail = "Оттенок доступен только для обводки из ассета.";
            return;
        }

        EditStroke("Stroke hue", commit, stroke => stroke with
        {
            Hue = Finite(degrees, stroke.Hue, -180, 180),
        });
    }

    public void SetStrokeSaturationPercent(double percent, bool commit = true)
    {
        if (!SelectedStrokeHasAsset)
        {
            StatusDetail = "Насыщенность доступна только для обводки из ассета.";
            return;
        }

        EditStroke("Stroke saturation", commit, stroke => stroke with
        {
            Saturation = PercentOffset(percent, stroke.Saturation),
        });
    }

    /// <summary>Stroke appearance back to defaults; width, offset, caps and asset survive.</summary>
    public void ResetStrokeAppearance() =>
        EditStroke("Reset stroke", commit: true, stroke => new VectorStrokeStyle
        {
            Width = stroke.Width,
            Offset = stroke.Offset,
            EndCap = stroke.EndCap,
            NodeCap = stroke.NodeCap,
            AssetSha256 = stroke.AssetSha256,
            AssetSpacing = stroke.AssetSpacing,
        });

    // -------------------------------------------------------------------------- shape fill

    private void EditShapeFill(string name, bool commit, Func<ShapeFillStyle, ShapeFillStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Shape is not { } shape)
            {
                return before;
            }

            var changed = change(shape.Fill);
            return changed.Equals(shape.Fill)
                ? before
                : before with { Shape = new ShapeStyle(shape.Regions, changed, shape.Stroke, shape.Portals) };
        });

    public void SetShapeFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditShapeFill("Shape fill", commit, fill => fill with { Color = color });
        }
    }

    public void SetShapeFillOpacityPercent(double percent, bool commit = true) =>
        EditShapeFill("Shape fill opacity", commit, fill => fill with
        {
            Opacity = Finite(percent, fill.Opacity * 100, 0, 100) / 100.0,
        });

    public void SetShapeFillTextureScale(double value, bool commit = true) =>
        EditShapeFill("Shape texture scale", commit, fill => fill with
        { TextureScale = Finite(value, fill.TextureScale, .05, 64) });

    public void SetShapeFillTextureRotation(double value, bool commit = true) =>
        EditShapeFill("Shape texture rotation", commit, fill => fill with
        { TextureRotation = Finite(value, fill.TextureRotation, -180, 180) });

    public void SetShapeFillTextureOffsetX(double value, bool commit = true) =>
        EditShapeFill("Shape texture offset X", commit, fill => fill with
        { TextureOffsetX = Finite(value, fill.TextureOffsetX, -4096, 4096) });

    public void SetShapeFillTextureOffsetY(double value, bool commit = true) =>
        EditShapeFill("Shape texture offset Y", commit, fill => fill with
        { TextureOffsetY = Finite(value, fill.TextureOffsetY, -4096, 4096) });

    public void ResetShapeFillTexture() =>
        EditShapeFill("Reset shape texture", commit: true, fill => fill with
        {
            TextureAssetSha256 = null, TextureScale = 1, TextureRotation = 0,
            TextureOffsetX = 0, TextureOffsetY = 0,
        });

    /// <summary>Fill back to its defaults; the texture reference is dropped with the rest.</summary>
    public void ResetShapeFill() =>
        EditShapeFill("Reset shape fill", commit: true, _ => new ShapeFillStyle());

    // ------------------------------------------------------- plain primitives (rectangle, ellipse)

    /// <summary>
    /// True for the objects whose colour lives on the object itself rather than in a Shape/Line
    /// style. They had no inspector block at all, so a placed rectangle kept its creation colour
    /// for ever - these three setters are what makes it editable.
    /// </summary>
    public bool SelectionIsPrimitive =>
        Session.SelectedObject is { Kind: SceneObjectKind.Rectangle or SceneObjectKind.Ellipse };

    public void SetPrimitiveFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditAppearance("Object fill", commit, before =>
                before.FillColor == color ? before : CopyObject(before, fillColor: color));
        }
    }

    public void SetPrimitiveStrokeColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditAppearance("Object stroke", commit, before =>
                before.StrokeColor == color ? before : CopyObject(before, strokeColor: color));
        }
    }

    public void SetPrimitiveStrokeWidth(double value, bool commit = true) =>
        EditAppearance("Object stroke width", commit, before =>
        {
            var width = Finite(value, before.StrokeWidth, 0, 64);
            return Math.Abs(before.StrokeWidth - width) < 0.0001
                ? before
                : CopyObject(before, strokeWidth: width);
        });

    // ------------------------------------------------------------------------ gradient fill

    /// <summary>
    /// The gradient of the single selected object, whichever kind it is: a Shape keeps it in its
    /// fill, a plain Rectangle/Ellipse keeps it on the object. The inspector reads one property so
    /// there is one gradient control instead of two that drift apart.
    /// </summary>
    public GradientFill? SelectedGradient =>
        Session.SelectedObject is { } selected
            ? selected.Shape?.Fill.Gradient ?? selected.FillGradient
            : null;

    /// <summary>True for the object kinds that actually have a fill a gradient can replace.</summary>
    public bool SelectionSupportsGradient =>
        Session.SelectedObject is { Kind: SceneObjectKind.Shape or SceneObjectKind.Rectangle
            or SceneObjectKind.Ellipse };

    /// <summary>The flat colour a new gradient starts from, so turning one on is not a jump.</summary>
    private string SelectedFlatFillColor() =>
        Session.SelectedObject is { } selected
            ? selected.Shape?.Fill.Color ?? selected.FillColor
            : AppearanceColor.OpaqueBlack;

    private void EditGradient(string name, bool commit, Func<GradientFill?, GradientFill?> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Shape is { } shape)
            {
                var changed = change(shape.Fill.Gradient);
                return Equals(changed, shape.Fill.Gradient)
                    ? before
                    : before with
                    {
                        Shape = new ShapeStyle(
                            shape.Regions, shape.Fill with { Gradient = changed },
                            shape.Stroke, shape.Portals),
                    };
            }

            if (before.Kind is SceneObjectKind.Rectangle or SceneObjectKind.Ellipse)
            {
                var changed = change(before.FillGradient);
                return Equals(changed, before.FillGradient)
                    ? before
                    : before with { FillGradient = changed };
            }

            return before;
        });

    /// <summary>
    /// One edit of a gradient that must already exist. A control that fires while the object has
    /// no gradient is a no-op rather than a silent creation the user did not ask for.
    /// </summary>
    private void EditExistingGradient(string name, bool commit, Func<GradientFill, GradientFill> change) =>
        EditGradient(name, commit, current => current is null ? null : change(current));

    /// <summary>
    /// The one entry point of the gradient toggle: null turns the gradient off, a kind turns it on
    /// (seeded from the current flat colour) or switches an existing one. Creating and choosing the
    /// kind in a single command means picking "radial" on a flat object is one undo, not two.
    /// </summary>
    public void SetGradientMode(GradientKind? kind)
    {
        if (!SelectionSupportsGradient)
        {
            StatusDetail = "Градиент доступен фигурам, прямоугольникам и эллипсам.";
            return;
        }

        var seed = SelectedFlatFillColor();
        EditGradient(kind is null ? "Disable gradient" : "Gradient mode", commit: true, current =>
            kind is not { } wanted
                ? null
                : current is null
                    ? GradientFill.Default(seed, wanted)
                    : current with { Kind = wanted });
    }

    public void SetGradientAngle(double degrees, bool commit = true) =>
        EditExistingGradient("Gradient angle", commit, current => new GradientFill(
            current.Kind, current.Stops,
            Finite(degrees, current.AngleDegrees, 0, 360),
            current.CenterX, current.CenterY, current.Radius));

    public void SetGradientCenterX(double value, bool commit = true) =>
        EditExistingGradient("Gradient centre X", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            Finite(value, current.CenterX, 0, 1), current.CenterY, current.Radius));

    public void SetGradientCenterY(double value, bool commit = true) =>
        EditExistingGradient("Gradient centre Y", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            current.CenterX, Finite(value, current.CenterY, 0, 1), current.Radius));

    public void SetGradientRadius(double value, bool commit = true) =>
        EditExistingGradient("Gradient radius", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            current.CenterX, current.CenterY, Finite(value, current.Radius, 0.05, 4)));

    public void SetGradientStopColor(int index, string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        EditExistingGradient("Gradient stop colour", commit, current =>
            index < 0 || index >= current.Stops.Count
                ? current
                : new GradientFill(
                    current.Kind,
                    [.. current.Stops.Select((stop, at) =>
                        at == index ? new GradientStopStyle(stop.Offset, color) : stop)],
                    current.AngleDegrees, current.CenterX, current.CenterY, current.Radius));
    }

    public void SetGradientStopOffset(int index, double offset, bool commit = true) =>
        EditExistingGradient("Gradient stop position", commit, current =>
            index < 0 || index >= current.Stops.Count
                ? current
                : new GradientFill(
                    current.Kind,
                    [.. current.Stops.Select((stop, at) => at == index
                        ? new GradientStopStyle(Finite(offset, stop.Offset, 0, 1), stop.Color)
                        : stop)],
                    current.AngleDegrees, current.CenterX, current.CenterY, current.Radius));

    /// <summary>
    /// Adds a stop in the widest gap, with the colour the ramp already shows there, so the new
    /// stop changes nothing until the user moves or recolours it.
    /// </summary>
    public void AddGradientStop()
    {
        if (SelectedGradient is { } current && current.Stops.Count >= GradientFill.MaxStops)
        {
            StatusDetail = $"У градиента не больше {GradientFill.MaxStops} точек цвета.";
            return;
        }

        EditExistingGradient("Add gradient stop", commit: true, gradient =>
        {
            var widest = 0;
            var widestGap = double.MinValue;
            for (var index = 0; index < gradient.Stops.Count - 1; index++)
            {
                var gap = gradient.Stops[index + 1].Offset - gradient.Stops[index].Offset;
                if (gap > widestGap)
                {
                    widestGap = gap;
                    widest = index;
                }
            }

            var left = gradient.Stops[widest];
            var right = gradient.Stops[widest + 1];
            var stops = gradient.Stops.ToList();
            stops.Insert(widest + 1, new GradientStopStyle(
                (left.Offset + right.Offset) / 2, MixColors(left.Color, right.Color)));
            return new GradientFill(gradient.Kind, stops, gradient.AngleDegrees,
                gradient.CenterX, gradient.CenterY, gradient.Radius);
        });
    }

    public void RemoveGradientStop(int index)
    {
        if (SelectedGradient is { } current && current.Stops.Count <= GradientFill.MinStops)
        {
            StatusDetail = "У градиента не меньше двух точек цвета.";
            return;
        }

        EditExistingGradient("Remove gradient stop", commit: true, gradient =>
            index < 0 || index >= gradient.Stops.Count
                ? gradient
                : new GradientFill(
                    gradient.Kind,
                    [.. gradient.Stops.Where((_, at) => at != index)],
                    gradient.AngleDegrees, gradient.CenterX, gradient.CenterY, gradient.Radius));
    }

    /// <summary>The midpoint of two canonical colours, channel by channel including alpha.</summary>
    private static string MixColors(string left, string right)
    {
        var a = uint.Parse(left.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = uint.Parse(right.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var mixed = 0u;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var channel = (byte)Math.Round(((((a >> shift) & 0xFF) + ((b >> shift) & 0xFF)) / 2.0));
            mixed |= (uint)channel << shift;
        }

        return "#" + mixed.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>Whole-object effects back to neutral: opacity, blend, blur.</summary>
    public void ResetShapeEffects() =>
        EditAppearance("Reset shape effects", commit: true, before =>
        {
            if (before.Shape is not { } shape)
            {
                return before;
            }

            var stroke = shape.Stroke with { Blend = "Normal", Blur = 0 };
            var unchanged = stroke.Equals(shape.Stroke) && before.ObjectOpacity == 1.0;
            return unchanged
                ? before
                : CopyObject(before) with
                {
                    ObjectOpacity = 1.0,
                    Shape = new ShapeStyle(shape.Regions, shape.Fill, stroke, shape.Portals),
                };
        });

    // ------------------------------------------------------------------- brush tip state

    public void SetBrushColor(string colorText)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        InspectorError = "";
        BrushSettings = BrushSettings with { Color = color };
        StatusDetail = $"Цвет кисти: {color}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Display name of the active tip, or null for the procedural round brush.</summary>
    public string? BrushTipName => _brushTipName;

    /// <summary>Sets the alpha tip the brush stamps with. Tool state, not a document edit.</summary>
    public void SetBrushTip(BrushTipShape tip, string name)
    {
        ArgumentNullException.ThrowIfNull(tip);
        BrushSettings = BrushSettings with { Tip = tip };
        _brushTipName = name;
        Raise(nameof(BrushTipName));
        StatusDetail = $"Отпечаток кисти: {name}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearBrushTip()
    {
        BrushSettings = BrushSettings with { Tip = null };
        _brushTipName = null;
        _brushPresetKind = BrushSettings.Softness > 0
            ? BrushPresetKind.RoundSoft
            : BrushPresetKind.RoundHard;
        Raise(nameof(BrushTipName));
        Raise(nameof(ActiveBrushPreset));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? BrushTextureName => _brushTextureName;

    public void ClearBrushTexture()
    {
        BrushSettings = BrushSettings with { Texture = null };
        _brushTextureName = null;
        _brushTextureSha256 = null;
        Raise(nameof(BrushTextureName));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool UseAssetAsBrushTexture(AssetRecord record)
    {
        if (_assetCatalog is null) return false;
        var resolution = _assetCatalog.ReadVerified(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Content is null)
        {
            StatusDetail = "Файл материала отсутствует или не совпадает с SHA-256.";
            return false;
        }
        try
        {
            using var bitmap = AssetImagePolicy.DecodeBitmap(resolution.Content, 1024, 1024);
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var alpha = color.Alpha / 255.0;
                var offset = ((y * bitmap.Width) + x) * 4;
                pixels[offset] = (byte)Math.Round(color.Red * alpha);
                pixels[offset + 1] = (byte)Math.Round(color.Green * alpha);
                pixels[offset + 2] = (byte)Math.Round(color.Blue * alpha);
                pixels[offset + 3] = color.Alpha;
            }
            BrushSettings = BrushSettings with { Texture = new BrushTexture(bitmap.Width, bitmap.Height, pixels), Eraser = false };
            _brushTextureName = record.DisplayName;
            _brushTextureSha256 = record.Sha256;
            if (CurrentVariant is { } variant && variant.Sha256 == record.Sha256)
                RememberCurrentVariant();
            Raise(nameof(BrushTextureName));
            StatusDetail = $"Материал кисти: {record.DisplayName}.";
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Материал кисти отклонён: " + error.Message;
            return false;
        }
    }

    /// <summary>
    /// Which tip shape is chosen right now. The panel used to offer seven identical buttons with
    /// no indication of which one was in effect; a list needs to know what to select.
    /// </summary>
    public BrushPresetKind ActiveBrushPreset => _brushPresetKind;

    /// <summary>
    /// The actual stamp of a tip, so the panel can show what the engine will print rather than a
    /// decorative icon. Null means the plain circle, whose edge comes from Softness instead.
    /// </summary>
    public static BrushTipShape? PreviewTip(BrushPresetKind preset) =>
        preset is BrushPresetKind.RoundHard or BrushPresetKind.RoundSoft ? null : PresetTip(preset);

    /// <summary>The edge softness a tip is applied with, for the same preview.</summary>
    public static double PreviewSoftness(BrushPresetKind preset) =>
        preset == BrushPresetKind.RoundSoft ? 0.75 : 0;

    public void ApplyBrushPreset(BrushPresetKind preset)
    {
        _brushPresetKind = preset;
        BrushSettings = preset switch
        {
            BrushPresetKind.RoundHard => BrushSettings with { Tip = null, Softness = 0 },
            BrushPresetKind.RoundSoft => BrushSettings with { Tip = null, Softness = 0.75 },
            _ => BrushSettings with { Tip = PresetTip(preset), Softness = 0 },
        };
        _brushTipName = preset switch
        {
            BrushPresetKind.RoundHard => "Круглая жёсткая",
            BrushPresetKind.RoundSoft => "Круглая мягкая",
            _ => preset.ToString(),
        };
        Raise(nameof(BrushTipName));
        Raise(nameof(ActiveBrushPreset));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BrushTipShape PresetTip(BrushPresetKind preset)
    {
        const int size = 64;
        var alpha = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nx = ((x + .5) / size * 2) - 1;
            var ny = ((y + .5) / size * 2) - 1;
            var inside = preset switch
            {
                BrushPresetKind.Ellipse => (nx * nx / 1.0) + (ny * ny / .36) <= 1,
                BrushPresetKind.Polygon => ny >= -1 && ny <= .85 && Math.Abs(nx) <= 1 - ((ny + 1) * .18),
                BrushPresetKind.EdgeBlock => Math.Abs(nx) >= .55 || Math.Abs(ny) >= .55,

                // Rectangle used to fall into the same "whole square" branch as GridBlock, so the
                // two tips stamped identical marks and one of them was a duplicate entry.
                BrushPresetKind.Rectangle => Math.Abs(ny) <= .5,
                _ => true,
            };
            if (inside) alpha[(y * size) + x] = 255;
        }
        return new BrushTipShape(size, size, alpha);
    }

    public IReadOnlyList<LocalBrushPreset> BrushPresets => _brushPresets.Where(p => p.Placement is null && p.Appearance is null).ToArray();

    public bool SaveBrushPreset(string name)
    {
        if (_brushPresetStore is null || string.IsNullOrWhiteSpace(name)) return false;
        var settings = BrushSettings;
        var preset = new LocalBrushPreset
        {
            Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), TextureSha256 = _brushTextureSha256,
            TipKind = _brushPresetKind.ToString(), Radius = settings.Radius, Softness = settings.Softness,
            Opacity = settings.Opacity, Color = settings.Color, Spacing = settings.Spacing,
            Scatter = settings.Scatter, AngleDegrees = settings.AngleDegrees,
            RandomFlipHorizontal = settings.RandomFlipHorizontal,
            RandomFlipVertical = settings.RandomFlipVertical,
            SizeJitter = settings.SizeJitter, OpacityJitter = settings.OpacityJitter,
            TextureScale = settings.TextureScale,
            TextureRotationDegrees = settings.TextureRotationDegrees,
            TextureOffsetX = settings.TextureOffsetX, TextureOffsetY = settings.TextureOffsetY,
            TextureOpacity = settings.TextureOpacity, HueDegrees = settings.HueDegrees,
            Saturation = settings.Saturation, Brightness = settings.Brightness, Contrast = settings.Contrast,
            PressureAffectsSize = settings.PressureAffectsSize,
            PressureAffectsOpacity = settings.PressureAffectsOpacity,
            PressureAffectsSoftness = settings.PressureAffectsSoftness,
        };
        _brushPresets = _brushPresetStore.Save([.. _brushPresets, preset]);
        Raise(nameof(BrushPresets)); StatusDetail = $"Пресет «{preset.Name}» сохранён локально."; return true;
    }

    public bool ApplyLocalBrushPreset(string id)
    {
        if (_brushPresets.FirstOrDefault(item => item.Id == id) is not { } preset) return false;
        if (!Enum.TryParse<BrushPresetKind>(preset.TipKind, out var kind)) kind = BrushPresetKind.RoundSoft;
        ApplyBrushPreset(kind);
        BrushSettings = BrushSettings with
        {
            Radius = preset.Radius, Softness = preset.Softness, Opacity = preset.Opacity,
            Color = preset.Color, Spacing = preset.Spacing, Scatter = preset.Scatter,
            AngleDegrees = preset.AngleDegrees,
            RandomFlipHorizontal = preset.RandomFlipHorizontal,
            RandomFlipVertical = preset.RandomFlipVertical,
            SizeJitter = preset.SizeJitter, OpacityJitter = preset.OpacityJitter,
            TextureScale = preset.TextureScale, TextureRotationDegrees = preset.TextureRotationDegrees,
            TextureOffsetX = preset.TextureOffsetX, TextureOffsetY = preset.TextureOffsetY,
            TextureOpacity = preset.TextureOpacity, HueDegrees = preset.HueDegrees,
            Saturation = preset.Saturation, Brightness = preset.Brightness, Contrast = preset.Contrast,
            PressureAffectsSize = preset.PressureAffectsSize,
            PressureAffectsOpacity = preset.PressureAffectsOpacity,
            PressureAffectsSoftness = preset.PressureAffectsSoftness,
        };
        if (preset.TextureSha256 is { } sha && _assetCatalog?.Find(sha) is { } record)
            UseAssetAsBrushTexture(record);
        else
            ClearBrushTexture();
        SceneChanged?.Invoke(this, EventArgs.Empty); return true;
    }

    public bool RenameBrushPreset(string id, string name)
    {
        if (_brushPresetStore is null || string.IsNullOrWhiteSpace(name)) return false;
        var changed = false;
        _brushPresets = _brushPresetStore.Save([.. _brushPresets.Select(item =>
        {
            if (item.Id != id) return item;
            changed = true; return item with { Name = name.Trim() };
        })]);
        if (changed) Raise(nameof(BrushPresets)); return changed;
    }

    public bool DeleteBrushPreset(string id)
    {
        if (_brushPresetStore is null || _brushPresets.All(item => item.Id != id)) return false;
        _brushPresets = _brushPresetStore.Save([.. _brushPresets.Where(item => item.Id != id)]);
        Raise(nameof(BrushPresets)); return true;
    }

    /// <summary>Loads a library asset as the brush tip: its alpha channel becomes the dab shape.</summary>
    public bool UseAssetAsBrushTip(AssetRecord record)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return false;
        }

        var resolution = _assetCatalog.ReadVerified(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Content is null)
        {
            StatusDetail = "Файл отпечатка отсутствует или не совпадает с его SHA-256.";
            return false;
        }

        SkiaSharp.SKBitmap bitmap;
        try
        {
            bitmap = AssetImagePolicy.DecodeBitmap(resolution.Content, 1024, 1024);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Отпечаток кисти должен быть изображением не больше 1024×1024.";
            return false;
        }

        using (bitmap)
        {
            var alpha = new byte[checked(bitmap.Width * bitmap.Height)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    alpha[(y * bitmap.Width) + x] = bitmap.GetPixel(x, y).Alpha;
                }
            }

            SetBrushTip(new BrushTipShape(bitmap.Width, bitmap.Height, alpha), record.DisplayName);
        }
        return true;
    }
}
