using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class FilterColorPresetWorkflowTests
{
    [Fact]
    public void One_click_preset_strength_and_manual_edit_are_single_undo_steps()
    {
        var (editor, filterId) = Editor();
        editor.SelectLayer(filterId);

        Assert.True(editor.ApplyActiveFilterPreset("winter"));
        Assert.Equal("Зима", EditorLabels.FilterColorLook((FilterLayer)editor.ActiveLayer!));
        Assert.True(editor.SetActiveFilterColor(colorStrength: 0.4));
        Assert.Equal("Зима", EditorLabels.FilterColorLook((FilterLayer)editor.ActiveLayer!));
        Assert.True(editor.SetActiveFilterColor(sharpness: 0.3));
        Assert.Equal("Свой", EditorLabels.FilterColorLook((FilterLayer)editor.ActiveLayer!));
        Assert.False(editor.ApplyActiveFilterPreset("no-such-preset"));

        editor.Undo();
        Assert.Equal(0.4, ((FilterLayer)editor.ActiveLayer!).Settings.ColorStrength, 6);
        editor.Undo();
        Assert.Equal(1, ((FilterLayer)editor.ActiveLayer!).Settings.ColorStrength);
        editor.Undo();
        Assert.Equal("Нет", EditorLabels.FilterColorLook((FilterLayer)editor.ActiveLayer!));
    }

    [Fact]
    public async Task Exported_png_matches_the_idle_canvas_for_every_preset()
    {
        var temp = Directory.CreateTempSubdirectory("map-filter-presets-");
        try
        {
            var exporter = new ImageExportEngine();
            foreach (var preset in FilterColorEffects.Presets)
            {
                var (editor, filterId) = Editor();
                editor.SelectLayer(filterId);
                Assert.True(editor.ApplyActiveFilterPreset(preset.Id));
                var document = editor.Session.Document;
                var path = Path.Combine(temp.FullName, preset.Id + ".png");
                await exporter.ExportAsync(document, exporter.Plan(document, new ExportRequest(
                    ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 96, 64,
                    ExportOutputMode.SingleImage, TransparentBackground: false, IncludeGrid: false)), path, stamps: null);

                using var exported = SKBitmap.Decode(path);
                var info = new SKImageInfo(96, 64);
                using var surface = SKSurface.Create(info)!;
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions());
                using var canvas = new SKBitmap(info);
                surface.ReadPixels(info, canvas.GetPixels(), info.RowBytes, 0, 0);
                // Inside the coloured stripes, away from the sharpening edge, both paths agree.
                foreach (var (x, y) in new[] { (10, 32), (60, 32), (85, 10) })
                {
                    var a = exported.GetPixel(x, y); var b = canvas.GetPixel(x, y);
                    Assert.True(Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) <= 3,
                        $"{preset.Title}: export {a} vs canvas {b} at {x},{y}");
            }
        }
        }
        finally { temp.Delete(recursive: true); }
    }

    private static (EditorViewModel Editor, LayerId Filter) Editor()
    {
        var stripe = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "field", 40, 0, 40, 64, 0,
            "#FF3E8E5A", "#00000000", 0, true, false);
        var filter = new FilterLayer(LayerId.New(), "filter", true, false, 1, new FilterSettings());
        var document = new MapDocument(DocumentId.New(), "colour presets", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(96, 64, "px", "#FF4C90B8"),
            [new BaseColorLayer(LayerId.New(), "sea", true, false, 1, "#FF4C90B8"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [stripe]), filter]);
        var editor = new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        return (editor, filter.Id);
    }
}
