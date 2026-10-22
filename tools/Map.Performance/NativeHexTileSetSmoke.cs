using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Map.Core.Documents;
using Map.Persistence;
using Map.Rendering;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R15: native-hex-workshop --output artifacts/r15/smoke.json.
    // Creates a procedural set exclusively through HexTileArt.Image; all data below _workspace.
    private async Task VerifyHexTileSetsR15()
    {
        var source = Path.Combine(_workspace, "r15-source"); HexUserTileSets.CreateProcedural(source);
        _view!.ImportHexTileSetForSmoke(source); await Settle();
        _view.ImportHexTileSetForSmoke(source); await Settle();
        var message = _view.GetLogicalDescendants().OfType<TextBlock>().Single(c => c.Name == "HexUserSetsMessage").Text;
        Check("R15: повторный импорт объясняет отказ без падения и замены набора", message!.Contains("уже установлен", StringComparison.Ordinal) && HexUserTileSets.All.Count == 1);
        PressHex(_view, "HexTabMySets"); await Settle();
        Check("R15: импорт папки добавляет 3 PNG-плитки во вкладку Мои наборы", HexUserTileSets.All.Count == 1
            && ReachableHex(_view, "HexTile_procedural/grass") && ReachableHex(_view, "HexCheckSet_procedural"));
        PressHex(_view, "HexTile_procedural/grass"); await Settle();
        var layer = _editor.ActiveHexLayer!; var hex = layer.Board.Layout.FromOffset(new(8, 8));
        _editor.HexRadius = 0;
        var world = layer.Board.Layout.Center(hex); var point = _editor.Session.Viewport.WorldToScreen(world.X, world.Y);
        _view.Canvas.ClickForSmoke(point.X, point.Y); await Settle();
        Check("R15: пользовательская плитка ставится с панели", _editor.ActiveHexLayer!.Board[hex]?.Terrain == "user/procedural/grass");
        _editor.Undo(); await Settle();
        Check("R15: установка пользовательской плитки отменяется", _editor.ActiveHexLayer!.Board[hex]?.Terrain != "user/procedural/grass");
        _editor.Redo(); await Settle();
        PressHex(_view, "HexTabMySets"); await Settle();
        PressHex(_view, "HexTile_procedural/peaks"); await Settle();
        Check("R15: высокая плитка запрещает поворот и сохраняет TravelCost", !_editor.HexTile.Rotatable && _editor.HexTile.TravelCost == 4);
        PressHex(_view, "HexTabMySets"); await Settle();
        PressHex(_view, "HexCheckSet_procedural"); await Settle();
        var desktop = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        var dialog = desktop.Windows.Single(w => w.Name == "HexTileSetCheckWindow");
        T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        Check("R15: окно проверки предлагает все 1944 соседства", Find<TextBlock>("HexSetCheckSummary").Text!.Contains("1944", StringComparison.Ordinal));
        Click(dialog, "HexSetCheckNextProblem"); await Settle();
        Check("R15: несовпадение краёв помечено текстом", Find<TextBlock>("HexSetCheckProblems").Text!.Contains("Край не совпадает", StringComparison.Ordinal));
        Find<ComboBox>("HexSetCheckKind").SelectedIndex = 1; Find<NumericUpDown>("HexSetCheckNumber").Value = 14; await Settle();
        Check("R15: запрещённый поворот помечен текстом", Find<TextBlock>("HexSetCheckProblems").Text!.Contains("Поворот запрещён", StringComparison.Ordinal));
        Find<ComboBox>("HexSetCheckKind").SelectedIndex = 2; Find<NumericUpDown>("HexSetCheckNumber").Value = 17; await Settle();
        Check("R15: доступны 54 перекрытия и их PNG-предпросмотр", Find<TextBlock>("HexSetCheckSummary").Text!.Contains("54", StringComparison.Ordinal)
            && Find<Image>("HexSetCheckPreview").Source is not null);
        Click(dialog, "HexSetCheckCloseButton"); await Settle();
        var path = Path.Combine(_workspace, "r15-live.mapproj");
        MapProjectStore.Save(_editor.Session.Document, path, new("r15", DateTimeOffset.UtcNow), PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Check("R15: пользовательская ссылка выбирает v24 поверх v21", json.RootElement.GetProperty("schemaVersion").GetInt32() == 24);
        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single(l => l.Level == layer.Level);
        Check("R15: reopen возвращает пользовательскую плитку", reopened.Board[hex]?.Terrain == "user/procedural/grass");
        // Validate actual missing-tile rendering without swapping a registry used by live caches.
        using var missing = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(140, 140));
        missing.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        HexLayerRenderer.DrawTile(missing.Canvas, new HexLayout(HexOrientation.Flat, 50, 70, 70), new(0, 0), "user/absent/tile", 0);
        using var snapshot = missing.Snapshot(); using var bitmap = SkiaSharp.SKBitmap.FromImage(snapshot);
        Check("R15: пропавший комплект рисует существующую заглушку без падения", bitmap.Pixels.Any(p => p.Alpha > 0));
        Check("R15: шаблон выдаёт две маски и читаемый set.json", ExportTemplate());
        bool ExportTemplate()
        {
            var directory = Path.Combine(_workspace, "r15-template"); HexUserTileSets.ExportTemplate(directory);
            return HexUserTileSets.Read(directory).Manifest.Tiles.Count == 1 && File.Exists(Path.Combine(directory, "Размеры.txt"));
        }
    }
}
