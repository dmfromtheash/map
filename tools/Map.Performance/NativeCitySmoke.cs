using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] CityRequired = ["city-opened", "city-regenerated", "resources-disposed"];

    /// <summary>R34/R35: a generated city map opens in the city workshop; «Другой вариант» in the «Город» window rebuilds it. Whole town and a close-up captured.</summary>
    private async Task CityOnly()
    {
        var document = CityBuilder.NewCityMap("Ильград", 2400, new CitySettings { Name = "Ильград", Seed = 11, Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _report.Checks["city-opened"] = _editor.IsCityWorkshop && _editor.Session.Document.Layers.OfType<ObjectLayer>().Any(l => l.Name == "Город" && l.Objects.Count > 100);
        _view.Canvas.FitToCanvas(); await Task.Delay(600); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        await Click("CityButton"); await Settle();
        var window = _view.GetType().GetField("_cityWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Город» не открылось.");
        var before = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        var reseed = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Button>().Single(b => b.Name == "CityReseedButton");
        reseed.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Task.Delay(400); await Settle();
        var after = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        _report.Checks["city-regenerated"] = after != before && _editor.Session.UndoDepth == 1;
        window.Close(); await Settle();
        _editor.Session.Viewport.Set(900, 900, 1.4); await Task.Delay(600); await Settle();
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-close.png"));
        await Task.Delay(800); await Settle();
    }
}
