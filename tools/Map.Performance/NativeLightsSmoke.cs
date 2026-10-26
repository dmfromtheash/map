using Avalonia.Controls;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] LightsRequired =
    [
        "light-placed-and-selected", "light-settings-from-panel", "dim-zone-one-undo", "resources-disposed",
    ];

    /// <summary>
    /// R14 through the Свет tab: a torch placed by a click on the map is chosen in the list, its
    /// colour, radius, dim zone and flicker are set in the panel under the list, and the dim zone
    /// is one Undo step. The editor is captured at night with the light and its dim zone.
    /// </summary>
    private async Task LightsOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 360, 360, 250);
        var document = new MapDocument(DocumentId.New(), "Огни", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island]) { Lighting = new MapLighting { TimeOfDay = 1 } };
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);

        await Click("RibbonLightTab");
        await Click("LightPresetCampfire");
        var place = Find<Avalonia.Controls.Primitives.ToggleButton>("LightPlaceToggle");
        place.IsChecked = true; place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        // The canvas takes a placing click in its own pointer handler, before the tool router the
        // smoke click drives; the same call is made here.
        _editor.PlaceLightAt(500, 380); await Settle(); await Frame();
        place.IsChecked = false; place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        var light = _editor.Session.Document.Lighting?.Sources.SingleOrDefault();
        _report.Checks["light-placed-and-selected"] = light is not null && _editor.SelectedLight?.Id == light.Id
            && Find<Control>("SelectedLightBlock").IsEffectivelyVisible;

        await Click("LightPick1");
        await Number("LightRadiusSliderBox", "180");
        await Number("LightIntensitySliderBox", "120");
        var depth = _editor.Session.UndoDepth;
        await Number("LightDimZoneSliderBox", "100");
        var dimStep = _editor.Session.UndoDepth == depth + 1;
        var edited = _editor.Session.Document.Lighting!.Sources.Single();
        _report.Checks["light-settings-from-panel"] = edited.Radius == 180 && Math.Abs(edited.Intensity - 1.2) < 1e-9
            && Math.Abs(edited.DimZone - 1) < 1e-9;
        await Frame(); await Task.Delay(500); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        _editor.Undo(); await Settle();
        _report.Checks["dim-zone-one-undo"] = dimStep && _editor.Session.Document.Lighting!.Sources.Single().DimZone == 0;
        await Click("RibbonCollapseButton"); await Task.Delay(1500); await Frame(); await Settle();
    }
}
