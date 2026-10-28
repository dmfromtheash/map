using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Map.App.Views;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] PointcrawlRequired =
    [
        "places-by-clicks", "ways-by-clicks", "way-edited", "players-by-visit", "key-pdf", "resources-disposed",
    ];

    /// <summary>
    /// R32 through «Пойнткроул»: three places and two ways by clicks, a way made dangerous and
    /// one-way, the first place visited; the players see it, its way and a «?»; the PDF key is
    /// written. The map and the players' window are captured.
    /// </summary>
    private async Task PointcrawlOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        await Click("WorldSectionButton"); await Click("PointcrawlButton"); await Settle();
        var window = _view.GetType().GetField("_pointcrawlWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Пойнткроул» не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне «Пойнткроул»");
        async Task Press(string name) { In<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle(); }
        async Task At(double x, double y) { Point(x, y); await Task.Delay(300); await Settle(); }

        foreach (var (x, y) in new[] { (420.0, 300.0), (640.0, 360.0), (560.0, 520.0) })
        {
            await Press("PointcrawlPlaceButton");
            await At(x, y);
        }
        _report.Checks["places-by-clicks"] = _editor.Pointcrawl.Nodes.Length == 3;
        await Press("PointcrawlLinkButton"); await At(421, 301); await At(639, 361);
        await Press("PointcrawlLinkButton"); await At(641, 359); await At(561, 519);
        _report.Checks["ways-by-clicks"] = _editor.Pointcrawl.Edges.Length == 2;

        var second = _editor.Pointcrawl.Edges[1];
        _editor.SelectedPointcrawlEdgeId = second.Id; _view.GetType(); await Settle();
        await Press("PointcrawlWay_" + second.Id.ToString("N"));
        In<TextBox>("PointcrawlWayHoursBox").Text = "8";
        In<ComboBox>("PointcrawlWayDangerBox").SelectedIndex = 3;
        In<ToggleButton>("PointcrawlWayOneWayToggle").IsChecked = true;
        await Press("PointcrawlSaveWayButton");
        _report.Checks["way-edited"] = _editor.Pointcrawl.Edges[1] is { Hours: 8, Danger: 3, OneWay: true };

        var first = _editor.Pointcrawl.Nodes[0];
        _editor.EditPointcrawlNode(first.Id, "Деревня", "Таверна «Якорь»", visited: true, null); await Settle();
        var players = Map.Core.Editing.PlayerScene.Sanitize(_editor.Session.Document).Pointcrawl;
        _report.Checks["players-by-visit"] = players.Nodes.Select(n => n.Name).SequenceEqual(["Деревня", "?"]) && players.Edges.Length == 1;
        await Task.Delay(500); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        var player = new HexPlayerWindow(_editor) { ShowActivated = false, ShowInTaskbar = false, Width = 1100, Height = 720,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        player.Show(); await Settle(); await Task.Delay(600); await Settle();
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1100, 720)))
        {
            bitmap.Render(player);
            using var file = File.Create(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-player.png"));
            bitmap.Save(file);
        }
        player.Close(); await Settle();

        var pdf = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-key.pdf");
        var pages = await _editor.ExportPointcrawlKeyAsync(pdf, players: false);
        _report.Checks["key-pdf"] = pages >= 2 && File.Exists(pdf) && new FileInfo(pdf).Length > 2000;
        window.Close(); await Settle();
        await Task.Delay(800); await Settle();
    }
}
