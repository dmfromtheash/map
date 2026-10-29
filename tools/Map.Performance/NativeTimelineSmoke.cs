using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] TimelineRequired =
    [
        "era-from-window", "year-hides", "border-blends", "political-legend", "gif-series", "resources-disposed",
    ];

    /// <summary>
    /// R31 through «Время»: a town is given years in the window; the slider hides it after its
    /// fall; a kingdom's border drawn for two eras blends in the gap; the political mode lists the
    /// states; the series by years is written as a GIF and decodes back. Window and map captured.
    /// </summary>
    private async Task TimelineOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        var town = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 300, 300, 160, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = LabelPresets.Apply(new TextStyle { Content = "Ильград", Alignment = TextAlignment.Center }, LabelPreset.City) };
        SceneObject Kingdom(double dx, Era era) => VectorGeometry.CreateFreeShape(ObjectId.New(), "Королевство",
            [new(250 + dx, 250), new(560 + dx, 230), new(600 + dx, 470), new(280 + dx, 500)], new ShapeFillStyle { Color = "#FF8A5A2B" },
            new VectorStrokeStyle { Color = "#FF5A3A1B", Width = 3 }) with { Era = era, MorphKey = "kingdom", Political = PoliticalRole.State };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [Kingdom(0, new Era(null, 900)), Kingdom(220, new Era(1000, null)), town]);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, objects]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        await Click("WorldSectionButton"); await Click("TimelineButton"); await Settle();
        var window = _view.GetType().GetField("_timelineWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Время» не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне «Время»");
        async Task Press(string name) { In<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle(); }

        _editor.Session.SelectedObjectId = town.Id; await Task.Delay(300); await Settle();
        In<TextBox>("SelectionEraFromBox").Text = "812";
        In<TextBox>("SelectionEraToBox").Text = "1040";
        await Press("SelectionEraButton");
        _report.Checks["era-from-window"] = _editor.Session.SelectedObject?.Era == new Era(812, 1040);
        _editor.Session.SelectedObjectId = null; await Settle();

        In<TextBox>("TimelineFirstBox").Text = "700"; In<TextBox>("TimelineLastBox").Text = "1200";
        await Press("TimelineSpanButton");
        var on = In<ToggleButton>("TimelineEnabledToggle"); on.IsChecked = true; await Settle();
        In<Slider>("TimelineYearSlider").Value = 1100; await Settle();
        _editor.SetTimelineYear(1100, commit: true); await Settle();
        int Count(string name) => _editor.RenderDocument.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Count(o => o.Name == name);
        _report.Checks["year-hides"] = Count("Ильград") == 0 && Count("Королевство") == 1;

        _editor.SetTimelineYear(950, commit: true); await Settle();
        var blended = _editor.RenderDocument.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Name == "Королевство").ToArray();
        _report.Checks["border-blends"] = blended.Length == 1 && blended[0].X + (blended[0].Width / 2) is > 480 and < 620;

        var political = In<ToggleButton>("TimelinePoliticalToggle"); political.IsChecked = true; await Task.Delay(300); await Settle();
        _report.Checks["political-legend"] = _editor.PoliticalLegend.Any(e => e.Name == "Королевство" && e.Role == PoliticalRole.State)
            && In<StackPanel>("PoliticalLegend").Children.Count > 0;
        await Task.Delay(600); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(640, 640)))
        {
            bitmap.Render(window);
            using var file = File.Create(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-window.png"));
            bitmap.Save(file);
        }

        var gif = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-series.gif");
        var frames = await _editor.ExportTimelineSeriesAsync(gif, 850, 1050, 25, 480, gif: true);
        using (var codec = SKCodec.Create(gif))
            _report.Checks["gif-series"] = frames == 9 && codec?.FrameCount == 9;
        window.Close(); await Settle();
        await Task.Delay(800); await Settle();
    }
}
