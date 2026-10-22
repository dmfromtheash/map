using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R16 steps of native-hex-workshop. Run with --profile native-hex-workshop --output artifacts/r16/smoke.json.
    // The enclosing profile isolates all three application profile paths below its workspace.
    private async Task VerifyHexStructureR16()
    {
        HexLayer Layer() => _editor.ActiveHexLayer!;
        var hex = Layer().Board.Layout.FromOffset(new HexOffset(12, 8));
        _editor.OpenHexKey(hex);
        PressHex(_view, "HexModeKey"); await Settle();
        PressHex(_view, "HexPoiAddButton"); await Settle();
        PressHex(_view, "HexPoiAddButton"); await Settle();
        var index = _editor.HexPoints.Count - 1;
        var box = _view.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "HexPoiName_" + index);
        box.Text = "Скрытый храм";
        // Focus loss commits the same way as a person moving to the next control.
        box.Focus();
        _view.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "HexPoiPlayer_" + index).Focus();
        await Settle();
        var check = _view.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "HexPoiPlayer_" + index);
        check.IsChecked = false;
        await Settle();
        Check("R16: имя и видимость точки записаны из панели", _editor.HexPoints[index] is { Name: "Скрытый храм", PlayerVisible: false });
        PressHex(_view, "HexPoiUp_" + index); await Settle();
        Check("R16: порядок точек меняется", _editor.HexPoints[index - 1].Name == "Скрытый храм");
        PressHex(_view, "HexPoiRemove_" + (index - 1)); await Settle();
        _editor.Undo(); await Settle();
        Check("R16: удалённая точка возвращается через отмену", _editor.HexPoints[index - 1].Name == "Скрытый храм");
        _editor.AddHexRegion("Королевство"); await Settle();
        PressHex(_view, "HexModeRegion"); await Settle();
        var regions = Layer().Regions;
        var parent = regions.Last().Id;
        var child = regions.First().Id;
        var choice = _view.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "HexRegionParent_" + child);
        choice.SelectedIndex = choice.ItemCount - 1; await Settle();
        Check("R16: родитель назначен через панель", Layer().Region(child)!.ParentRegion == parent);
        Check("R16: обратная связь запрещена как цикл", !_editor.SetHexRegionParent(parent, child));
        _editor.Undo(); await Settle();
        Check("R16: отмена восстанавливает корень региона", Layer().Region(child)!.ParentRegion == 0);
        _editor.Redo(); await Settle();
        var document = _editor.Session.Document;
        var depth = _editor.Session.UndoDepth;
        var views = _view.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "HexPresentationChoice");
        for (var i = 0; i < 4; i++)
        {
            views.SelectedIndex = i; await Settle();
            Check("R16: представление " + i + " не изменяет карту", ReferenceEquals(document, _editor.Session.Document) && _editor.Session.UndoDepth == depth);
            Capture(_view, "r16-view-" + i);
        }
        views.SelectedIndex = 0; await Settle();
        var safeJson = HexJsonExporter.ToJson(new ExportScene(document, recipient: ExportRecipient.Players)).ToJsonString();
        Check("R16: пакет игрока без скрытой точки", !safeJson.Contains("Скрытый храм", StringComparison.Ordinal));
        var path = Path.Combine(_workspace, "r16-live.mapproj");
        MapProjectStore.Save(document, path, new SaveMetadata("r16", DateTimeOffset.UtcNow), PreviewPng());
        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single(l => l.Level == 0);
        Check("R16: POI и иерархия переживают сохранение", reopened.Board[hex]!.Equals(Layer().Board[hex])
            && reopened.Region(child)!.ParentRegion == parent);
    }
}
