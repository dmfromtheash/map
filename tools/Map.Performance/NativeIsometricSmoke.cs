using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] IsometricRequired = ["iso-preview", "iso-turned", "resources-disposed"];

    /// <summary>R38: a generated town opened, «Изометрия» shows it standing up; «С запада» turns it. The window captured.</summary>
    private async Task IsometricOnly()
    {
        var document = CityBuilder.NewCityMap("Ильград", 2400, new CitySettings { Name = "Ильград", Seed = 11, Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        await Click("WorldSectionButton"); await Click("IsometricButton"); await Task.Delay(400); await Settle();
        var window = _view.GetType().GetField("_isometricWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Изометрия» не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне изометрии");
        var first = In<Image>("IsometricPreview").Source;
        _report.Checks["iso-preview"] = first is { Size.Width: > 1000 };
        In<Button>("IsometricSide1").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Task.Delay(400); await Settle();
        _report.Checks["iso-turned"] = _editor.IsometricSettings.Turn == 1 && !ReferenceEquals(first, In<Image>("IsometricPreview").Source);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        PanelAudit.Save((Control)window.Content!, _report.Screenshot);
        window.Close();
        await Task.Delay(800); await Settle();
    }
}
