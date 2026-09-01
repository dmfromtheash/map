using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage15StampToolParityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-stage15-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Future_stamp_settings_are_transient_and_commit_materialises_the_exact_resolved_plan()
    {
        var savedPreferences = new List<EditorLocalPreferences>();
        var editor = AssetEditor(out var catalog, savedPreferences.Add);
        var set = catalog.FindSetForContent(catalog.Records[0].Sha256)!;
        editor.SelectAssetSet(set.Id);
        var source = editor.Session.Document;
        var revision = editor.Session.Revision;
        var undo = editor.Session.UndoDepth;

        editor.SetStampScalePercent(175, commit: true);
        editor.SetStampRotationDegrees(359, commit: true);
        editor.SetStampSublayer(6);
        editor.SetStampOpacityPercent(65, commit: true);
        editor.SetStampFlip(horizontal: true, vertical: true);
        editor.SetStampBlend("ColorBurn");
        editor.SetStampBlur(12);
        editor.SetStampHueDegrees(-90);
        editor.SetStampSaturationPercent(175);
        editor.SetStampBrightnessPercent(55);
        editor.SetStampContrastPercent(140);
        editor.SetStampShadowStandard();
        editor.SetStampShadowColor("#8012ab34");
        editor.SetStampShadowPlacement(StampShadowPlacement.Layer);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.SetStampAreaRadius(120, persist: true);
        editor.SetStampDensity(80, persist: true);
        editor.RandomVariantAfterPlacement = true;
        editor.StampRandomAngleStep = StampRandomAngleStep.Degrees45;
        editor.StampRandomFlip = true;
        editor.UpdatePlacementPreview(500, 350);
        var pending = editor.PendingPlacement!;
        var preview = pending.PreviewObjects.ToArray();

        Assert.NotEmpty(preview);
        Assert.Same(source, editor.Session.Document);
        Assert.Equal(revision, editor.Session.Revision);
        Assert.Equal(undo, editor.Session.UndoDepth);
        Assert.All(preview, item =>
        {
            Assert.Equal(6, item.Sublayer);
            Assert.Equal(0.65 * 0.55, item.ObjectOpacity, 8);
            Assert.Equal(0, (item.RotationDegrees - 359) % 45);
            Assert.Equal("ColorBurn", item.Stamp!.Blend);
            Assert.Equal(StampShadowPlacement.Layer, item.Stamp.ShadowPlacement);
            Assert.Equal("#8012AB34", item.Stamp.ShadowColor);
        });
        Assert.Contains(preview, item => !item.Stamp!.FlipHorizontal || !item.Stamp.FlipVertical);

        Assert.True(editor.CommitPendingPlacement());
        var committed = Objects(editor).ToArray();
        Assert.Equal(pending.PlannedCount, committed.Length);
        Assert.Equal(undo + 1, editor.Session.UndoDepth);
        Assert.All(preview.Zip(committed), pair =>
        {
            Assert.Equal(pair.First.X, pair.Second.X);
            Assert.Equal(pair.First.Y, pair.Second.Y);
            Assert.Equal(pair.First.Width, pair.Second.Width);
            Assert.Equal(pair.First.Height, pair.Second.Height);
            Assert.Equal(pair.First.RotationDegrees, pair.Second.RotationDegrees);
            Assert.Equal(pair.First.Sublayer, pair.Second.Sublayer);
            Assert.Equal(pair.First.ObjectOpacity / 0.55, pair.Second.ObjectOpacity, 8);
            Assert.Equal(pair.First.Stamp, pair.Second.Stamp);
        });
        Assert.NotEmpty(savedPreferences);
    }

    [Fact]
    public void Pending_slider_persists_only_on_commit_and_cancel_never_changes_document()
    {
        var saves = new List<EditorLocalPreferences>();
        var editor = AssetEditor(out var catalog, saves.Add);
        editor.SelectAssetSet(catalog.FindSetForContent(catalog.Records[0].Sha256)!.Id);
        saves.Clear();
        var document = editor.Session.Document;

        editor.SetStampScalePercent(120, commit: false);
        editor.SetStampScalePercent(130, commit: false);
        Assert.Empty(saves);
        editor.SetStampScalePercent(140, commit: true);
        Assert.Single(saves);
        editor.UpdatePlacementPreview(200, 180);
        Assert.True(editor.CancelPlacement());

        Assert.Same(document, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Null(editor.PendingPlacement);
    }

    [Fact]
    public void Selected_stamp_slider_is_one_command_and_appearance_reset_preserves_transform_and_flips()
    {
        var editor = AssetEditor(out var catalog);
        editor.SelectAssetSet(catalog.FindSetForContent(catalog.Records[0].Sha256)!.Id);
        editor.UpdatePlacementPreview(300, 260);
        Assert.True(editor.CommitPendingPlacement());
        var placed = editor.Session.SelectedObject!;
        editor.ActiveTool = EditorTool.Select;
        var beforeSlider = editor.Session.UndoDepth;

        editor.SetStampScalePercent(120, commit: false);
        editor.SetStampScalePercent(135, commit: false);
        editor.SetStampScalePercent(150, commit: true);
        Assert.Equal(beforeSlider + 1, editor.Session.UndoDepth);
        editor.SetStampRotationDegrees(123);
        editor.SetStampSublayer(8);
        editor.SetStampFlip(horizontal: true, vertical: true);
        editor.SetStampOpacityPercent(40);
        editor.SetStampBlend("Exclusion");
        editor.SetStampBlur(10);
        var beforeReset = editor.Session.SelectedObject!;

        editor.ResetStampAppearance();
        var reset = editor.Session.SelectedObject!;

        Assert.Equal(beforeReset.X, reset.X);
        Assert.Equal(beforeReset.Y, reset.Y);
        Assert.Equal(beforeReset.Width, reset.Width);
        Assert.Equal(beforeReset.Height, reset.Height);
        Assert.Equal(123, reset.RotationDegrees);
        Assert.Equal(8, reset.Sublayer);
        Assert.True(reset.Stamp!.FlipHorizontal);
        Assert.True(reset.Stamp.FlipVertical);
        Assert.Equal(1, reset.ObjectOpacity);
        Assert.Equal(BlendModes.Normal, reset.Stamp.Blend);
        Assert.Equal(0, reset.Stamp.Blur);
    }

    [Fact]
    public void Stage15ClipMask_future_selected_and_foreground_edits_have_exact_history_boundaries()
    {
        var editor = AssetEditor(out var catalog);
        var set = catalog.Sets.Single();
        var masks = AssignStage15ClipMasks(catalog, set);
        editor.SelectAssetSet(set.Id);
        var originalDocument = editor.Session.Document;
        var originalRevision = editor.Session.Revision;

        editor.SetStampClipMaskEnabled(true);
        Assert.Same(originalDocument, editor.Session.Document);
        Assert.Equal(originalRevision, editor.Session.Revision);
        Assert.True(editor.StampToolSettings.Style!.ClipMaskEnabled);
        Assert.Equal(masks[0], editor.StampToolSettings.Style.MaskAssetSha256);
        Assert.All(editor.PendingPlacement!.PreviewObjects, preview =>
        {
            Assert.True(preview.Stamp!.ClipMaskEnabled);
            Assert.Equal(masks[0], preview.Stamp.MaskAssetSha256);
        });

        editor.UpdatePlacementPreview(300, 240);
        Assert.True(editor.CommitPendingPlacement());
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Contains(editor.Session.Document.Assets, asset => asset.Sha256 == masks[0]);
        Assert.True(editor.Session.SelectedObject!.Stamp!.ClipMaskEnabled);

        editor.ActiveTool = EditorTool.Select;
        editor.SetStampClipMaskEnabled(false);
        Assert.Equal(2, editor.Session.UndoDepth);
        Assert.False(editor.Session.SelectedObject!.Stamp!.ClipMaskEnabled);
        Assert.True(editor.Session.Undo());
        Assert.True(editor.Session.SelectedObject!.Stamp!.ClipMaskEnabled);

        var beforeForeground = editor.Session.UndoDepth;
        editor.CreateStampForegroundSurface();
        Assert.Equal(beforeForeground + 1, editor.Session.UndoDepth);
        editor.EditStampForegroundSurface();
        editor.BrushSettings = editor.BrushSettings with
        {
            Color = "#FFFF0000",
            Radius = 24,
            Spacing = 0.1,
        };
        Assert.True(editor.BeginStroke(40, 40));
        editor.ContinueStroke(60, 40);
        editor.EndStroke();
        Assert.Equal(beforeForeground + 2, editor.Session.UndoDepth);
        Assert.False(editor.Session.Document.StampForeground!.IsEmpty);

        var selected = editor.Session.SelectedObject!;
        editor.Session.Execute(new ReplaceObjectsCommand(
            [selected],
            [selected with
            {
                Stamp = selected.Stamp! with
                {
                    MaskAssetSha256 = new string('f', 64),
                    ClipMaskEnabled = true,
                },
            }],
            "missing clip mask"));
        Assert.Equal("Маска отсутствует или повреждена", editor.StampClipMaskStatus);
        Assert.True(editor.Session.SelectedObject!.Stamp!.ClipMaskEnabled);
    }

    [Fact]
    public void Stage15ClipMask_random_scatter_keeps_each_visual_paired_with_its_own_mask()
    {
        var editor = AssetEditor(out var catalog);
        var set = catalog.Sets.Single();
        var masks = AssignStage15ClipMasks(catalog, set);
        editor.SelectAssetSet(set.Id);
        editor.SetStampClipMaskEnabled(true);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.RandomVariantAfterPlacement = true;
        editor.SetStampAreaRadius(140, persist: true);
        editor.SetStampDensity(120, persist: true);
        editor.UpdatePlacementPreview(500, 350);

        var expected = set.VariantIds.Select((id, index) =>
                (Visual: catalog.FindVariant(id)!.Sha256, Mask: masks[index]))
            .ToDictionary(pair => pair.Visual, pair => pair.Mask, StringComparer.OrdinalIgnoreCase);
        Assert.All(editor.PendingPlacement!.PreviewObjects, preview =>
            Assert.Equal(expected[preview.Stamp!.AssetSha256], preview.Stamp.MaskAssetSha256));

        Assert.True(editor.CommitPendingPlacement());
        Assert.All(Objects(editor), placed =>
            Assert.Equal(expected[placed.Stamp!.AssetSha256], placed.Stamp.MaskAssetSha256));
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Stage15ClipMask_appearance_reset_preserves_mask_identity_and_enabled_state()
    {
        var editor = AssetEditor(out var catalog);
        var set = catalog.Sets.Single();
        var mask = AssignStage15ClipMasks(catalog, set)[0];
        editor.SelectAssetSet(set.Id);
        editor.SetStampClipMaskEnabled(true);
        editor.UpdatePlacementPreview(200, 160);
        Assert.True(editor.CommitPendingPlacement());
        editor.ActiveTool = EditorTool.Select;
        editor.SetStampBlur(12);

        editor.ResetStampAppearance();

        Assert.Equal(mask, editor.Session.SelectedObject!.Stamp!.MaskAssetSha256);
        Assert.True(editor.Session.SelectedObject.Stamp.ClipMaskEnabled);
        Assert.Equal(0, editor.Session.SelectedObject.Stamp.Blur);
    }

    private IReadOnlyList<string> AssignStage15ClipMasks(AssetCatalog catalog, AssetSet set)
    {
        var result = new List<string>();
        for (var index = 0; index < set.VariantIds.Count; index++)
        {
            var variant = catalog.FindVariant(set.VariantIds[index])!;
            var visual = catalog.Find(variant.Sha256)!;
            var path = Image(
                "clip-mask-" + index + "-" + Guid.NewGuid().ToString("N"),
                index == 0 ? SKColors.White : SKColors.Gray,
                visual.Width,
                visual.Height);
            result.Add(catalog.AssignVariantMask(
                variant.Id, path, DateTimeOffset.UnixEpoch.AddSeconds(index)).MaskSha256!);
        }
        return result;
    }

    private EditorViewModel AssetEditor(
        out AssetCatalog catalog, Action<EditorLocalPreferences>? savePreferences = null)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        var first = Image("tree-a-" + Guid.NewGuid().ToString("N"), SKColors.ForestGreen, 32, 24);
        var second = Image("tree-b-" + Guid.NewGuid().ToString("N"), SKColors.DarkOliveGreen, 24, 32);
        catalog.ImportSet([first, second], "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        return new EditorViewModel(
            Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch,
            assetCatalog: catalog, saveLocalPreferences: savePreferences);
    }

    private string Image(string name, SKColor color, int width, int height)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static IReadOnlyList<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToArray();

    private static MapDocument Document() => new(
        DocumentId.New(), "Stage 15", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1200, 800, "px", "#FF20242B"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []),
        ]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
