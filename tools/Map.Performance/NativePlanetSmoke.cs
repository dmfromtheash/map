using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Export;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] PlanetRequired =
    [
        "planet-on", "globe-drawn", "projections-written", "seam-ghost-shown", "resources-disposed",
    ];

    /// <summary>
    /// R39 through «Планета»: a two-to-one map with land by the western seam becomes a planet;
    /// the globe is drawn and turned; Mollweide, the icosahedral net and gores are written; the
    /// canvas past the right edge shows the left side of the world. All captured.
    /// </summary>
    private async Task PlanetOnly()
    {
        var page = "#FF8FB0BA";
        var west = CliffLand("Запад", 140, 300, 260, 220);
        var document = new MapDocument(DocumentId.New(), "Планета", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 1024, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), west, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        await Click("WorldSectionButton"); await Click("PlanetButton"); await Settle();
        var window = _view.GetType().GetField("_planetWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Планета» не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне «Планета»");
        In<ToggleButton>("PlanetEnabledToggle").IsChecked = true; await Task.Delay(400); await Settle();
        _report.Checks["planet-on"] = _editor.Planet.Enabled;

        var globe = In<Control>("PlanetGlobe");
        globe.GetType().GetMethod("Turn")!.Invoke(globe, [-150.0, 20.0]); await Task.Delay(300); await Settle();
        _report.Checks["globe-drawn"] = (bool)globe.GetType().GetProperty("HasSource")!.GetValue(globe)!;
        var directory = Path.GetDirectoryName(Options.OutputPath)!;
        var stem = Path.GetFileNameWithoutExtension(Options.OutputPath);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(660, 820)))
        {
            bitmap.Render(window);
            using var file = File.Create(Path.Combine(directory, stem + "-window.png"));
            bitmap.Save(file);
        }

        var written = 0;
        foreach (var projection in new[] { PlanetProjection.Mollweide, PlanetProjection.Icosahedral, PlanetProjection.Gores })
        {
            var path = Path.Combine(directory, $"{stem}-{projection}.png");
            if (await _editor.ExportProjectionAsync(path, projection, 1200, 0, 0, 12) && SKBitmap.DecodeBounds(path).Width == 1200) written++;
        }
        _report.Checks["projections-written"] = written == 3;

        // Past the right edge: the left side of the world, faint.
        _editor.Session.Viewport.Set(1500, 150, 1); await Task.Delay(500); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        using (var shot = SKBitmap.Decode(_report.Screenshot))
        {
            // The ghost of «Запад» lands right of the seam (world x 2048+140): lighter than the sea there.
            var origin = _view.Canvas.TranslatePoint(default, _view) ?? default;
            SKColor At(double x, double y)
            {
                var p = _editor.Session.Viewport.WorldToScreen(x, y);
                return shot.GetPixel(Math.Clamp((int)Math.Round(p.X + origin.X), 0, shot.Width - 1), Math.Clamp((int)Math.Round(p.Y + origin.Y), 0, shot.Height - 1));
            }
            var (ghost, sea) = (At(2048 + 140, 300), At(2048 + 500, 300));
            _report.Checks["seam-ghost-shown"] = ghost.Red + ghost.Green + ghost.Blue > sea.Red + sea.Green + sea.Blue + 30;
        }
        window.Close(); await Settle();
        await Task.Delay(800); await Settle();
    }
}
