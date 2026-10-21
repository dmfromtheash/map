using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    private string? _hexVttPath;

    /// <summary>R01 steps of native-hex-workshop: visible switches, undo/redo and two real dd2vtt exports.</summary>
    private async Task VerifyHexPathBlockingExports()
    {
        for (var level = 0; level >= -1; level--)
        {
            _editor.HexLevel = level;
            PressHex(_view, "HexModePath");
            PressHex(_view, "HexPathKindBorder");
            await Settle();
            var layer = _editor.ActiveHexLayer!;
            var layout = layer.Board.Layout;
            var a = layout.Center(layout.FromOffset(new HexOffset(level == 0 ? 4 : 18, 3)));
            var b = layout.Center(layout.FromOffset(new HexOffset(level == 0 ? 4 : 18, 8)));
            var from = _editor.Session.Viewport.WorldToScreen(a.X, a.Y);
            var to = _editor.Session.Viewport.WorldToScreen(b.X, b.Y);
            _view.Canvas.PressMoveReleaseForSmoke(from.X, from.Y, to.X, to.Y);
            await Settle();
            var border = _editor.SelectedHexPath;
            Check($"R01 new border on level {level} does not block", border is { BlocksSight: false, BlocksMove: false, BlocksLight: false });
            RevealHex(_view, "HexPathBlocksSightCheckBox");
            Toggle(_view, "HexPathBlocksSightCheckBox");
            Toggle(_view, "HexPathBlocksMoveCheckBox");
            Toggle(_view, "HexPathBlocksLightCheckBox");
            await Settle();
            Check($"R01 all three path switches on level {level} update the document", _editor.SelectedHexPath is
                { BlocksSight: true, BlocksMove: true, BlocksLight: true });
            _editor.Undo(); await Settle();
            var light = _view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "HexPathBlocksLightCheckBox");
            Check($"R01 undo refreshes the light switch on level {level}", _editor.SelectedHexPath?.BlocksLight == false && light.IsChecked == false);
            _editor.Redo(); await Settle();
            Capture(_view, $"r01-path-level-{level}");
        }

        _editor.HexLevel = 0;
        PressHex(_view, "HexPathSelect_1_Border");
        await Settle();
        var selectedSight = _view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "HexPathBlocksSightCheckBox");
        Check("R01 selecting an existing path immediately enables and refreshes its switches",
            selectedSight.IsEffectivelyEnabled && selectedSight.IsChecked == true);

        _editor.BeginExport();
        _editor.ExportOutputWidthText = "640";
        for (var level = 0; level >= -1; level--)
        {
            _editor.ExportHexLevel = level;
            await _editor.WaitForExportPreviewAsync();
            _hexVttPath = Path.Combine(_workspace, $"r01-level-{level}.dd2vtt");
            Check($"R01 dd2vtt export completes for level {level}", await _editor.StartVttExportAsync());
            using var json = JsonDocument.Parse(File.ReadAllBytes(_hexVttPath));
            var walls = json.RootElement.GetProperty("line_of_sight");
            var layer = _editor.Session.Document.Layers.OfType<HexLayer>().Single(l => l.Level == level);
            var path = layer.Paths.Single(p => p.BlocksSight);
            var (x, y) = layer.Board.Layout.Position(path.Vertices[0]);
            var cell = _editor.ExportVttGridSize;
            Check($"R01 dd2vtt walls belong only to level {level}", walls.GetArrayLength() == 1
                && Math.Abs(walls[0][0].GetProperty("x").GetDouble() - x / cell) < 0.0001
                && Math.Abs(walls[0][0].GetProperty("y").GetDouble() - y / cell) < 0.0001
                && json.RootElement.GetProperty("image").GetBytesFromBase64().Length > 0);
        }
        _editor.CloseExport();
        _editor.HexLevel = 0;
    }

    private sealed class HexVttDestination(Func<string?> path) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format) => Task.FromResult<string?>(null);
        public Task<string?> PickTilePackageAsync(string suggestedFolderName) => Task.FromResult<string?>(null);
        public Task<string?> PickVttAsync(string suggestedFileName) => Task.FromResult(path());
    }
}
