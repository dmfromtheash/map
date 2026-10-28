using Avalonia.Controls;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] SectorRequired = ["sector-dressed", "system-from-window", "sector-imported", "resources-disposed"];

    /// <summary>R33 through «Сектор»: a hex map dressed as space, a system typed into the window, a subsector loaded from T5 text. Captured.</summary>
    private async Task SectorOnly()
    {
        var document = HexMapFactory.Create("Сектор Спинворд", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 46, 8, 10, "#FF8FB0BA");
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        await Click("WorldSectionButton"); await Click("StarSectorButton"); await Settle();
        var window = _view.GetType().GetField("_sectorWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Звёздный сектор» не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне «Звёздный сектор»");
        async Task Press(string name) { In<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle(); }

        await Press("SectorMakeButton");
        _report.Checks["sector-dressed"] = _editor.Session.Document.Layers.OfType<BaseColorLayer>().Single().Color == StarSector.Background;
        In<TextBox>("SectorHexBox").Text = "0203";
        In<TextBox>("SectorNameBox").Text = "Регина";
        In<TextBox>("SectorUwpBox").Text = "A788899-C";
        In<TextBox>("SectorBasesBox").Text = "NS";
        In<TextBox>("SectorStarsBox").Text = "F7 V";
        In<ComboBox>("SectorZoneBox").SelectedIndex = 0;
        await Press("SectorSaveButton");
        _report.Checks["system-from-window"] = _editor.SystemAt("0203")?.Name == "Регина";
        var count = _editor.ImportSectorData("Hex\tName\tUWP\tBases\tRemarks\tZone\tPBG\tAllegiance\tStars\n"
            + "0402\tЭфат\tA646930-D\tNS\tHi In\tA\t701\tImDd\tM0 V\n"
            + "0605\tИфиген\tC200423-7\t\tNa Ni\tR\t604\tImDd\tK2 V\n"
            + "0307\tБоуман\tB876743-9\t\tAg\t\t503\tImDd\tG4 V\n");
        _report.Checks["sector-imported"] = count == 3 && _editor.SectorSystems.Count == 4;
        _view.Canvas.FitToCanvas(); await Task.Delay(500); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        window.Close(); await Settle();
        await Task.Delay(800); await Settle();
    }
}
