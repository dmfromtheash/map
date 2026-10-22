using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Presentation.ViewModels;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R17: --profile native-hex-workshop --output artifacts/r17/smoke.json.
    // Изолированный профиль задаёт enclosing NativeRegionalSmoke; тяжёлые операции последовательны.
    private async Task VerifyHexGameplayR17()
    {
        HexLayer Layer() => _editor.ActiveHexLayer!;
        var h = Layer().Board.Layout.FromOffset(new HexOffset(12, 8));
        _editor.OpenHexKey(h); PressHex(_view!, "HexModeKey"); await Settle();
        _editor.SetHexKey(new HexNote("Истинное имя R17", gmText: "Секрет ведущего R17", playerText: "Истинный текст R17")
        { Rumor = "Говорят, здесь заброшенная обитель.", ApproximateLabel = "Старая обитель" });
        _editor.AddHexPoi("starter/ruins");
        var index = _editor.HexPoints.Count - 1;
        _editor.SetHexPoi(index, _editor.HexPoints[index] with { Name = "Истинная точка R17",
            Rumor = "По ночам в башне виден огонь.", ApproximateLabel = "Одинокая башня" });
        var region = Layer().Regions.First();
        _editor.SetHexRegionKnowledge(region.Id, "Поговаривают о пропавших караванах.", "Дальний край");
        await Settle();
        var reveal = _view!.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "HexRevealConditionBox");
        reveal.SelectedIndex = (int)RevealCondition.Manual; await Settle();
        _editor.RevealAllHexes(true);
        Check("R17: ручное условие скрывает ключ в пакете", !ExportPlayerVisibility.Sanitize(Layer()).Notes.ContainsKey(h));
        var manual = _view.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "HexRevealManualBox");
        manual.IsChecked = true; await Settle();
        Check("R17: раскрытие из панели показывает слух", ExportPlayerVisibility.Sanitize(Layer()).Notes[h].PlayerText.Contains("обитель"));
        _editor.Undo(); await Settle();
        Check("R17: отмена возвращает закрытое условие", !ExportPlayerVisibility.Sanitize(Layer()).Notes.ContainsKey(h));
        reveal.SelectedIndex = (int)RevealCondition.OnVisit; await Settle();
        _editor.RevealAllHexes(false);
        PressHex(_view, "HexModeParty"); await Settle();
        var radius = _view.GetLogicalDescendants().OfType<NumericUpDown>().Single(c => c.Name == "HexPartyRadiusBox");
        radius.Value = 1; await Settle();
        var depth = _editor.Session.UndoDepth;
        var p = Layer().Board.Layout.Center(h);
        _editor.BeginHexGesture(p.X, p.Y); _editor.EndHexGesture(); await Settle();
        Check("R17: шаг партии — одна отмена с раскрытием", _editor.Session.UndoDepth == depth + 1 && Layer().Board[h]!.Revealed
            && Layer().Party.History.SequenceEqual(new[] { h }) && ExportPlayerVisibility.Sanitize(Layer()).Notes.ContainsKey(h));
        _editor.Undo(); await Settle();
        Check("R17: отмена шага возвращает туман и посещение", Layer().Party.Position is null && !Layer().Board[h]!.Revealed && !Layer().Party.Visited.Contains(h));
        _editor.Redo(); await Settle();
        var next = h.Neighbors().First(Layer().Board.Contains); var q = Layer().Board.Layout.Center(next);
        _editor.HexMode = HexToolMode.Place;
        _editor.BeginHexGesture(p.X, p.Y); _editor.ContinueHexGesture(q.X, q.Y); _editor.EndHexGesture(); await Settle();
        Check("R17: маркер перетаскивается без рисования", Layer().Party.Position == next && Layer().Party.History.Count == 2);
        PressHex(_view, "HexModeParty"); await Settle();
        PressHex(_view, "HexPartyClearHistoryButton"); await Settle();
        Check("R17: очистка следа сохраняет знания", Layer().Party.History.Count == 0 && Layer().Party.Visited.Contains(h)
            && ExportPlayerVisibility.Sanitize(Layer()).Notes.ContainsKey(h));
        _editor.Undo(); await Settle();
        var publicJson = HexJsonExporter.ToJson(new ExportScene(_editor.Session.Document, recipient: ExportRecipient.Players))
            .ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Check("R17: игроку слухи без правды и секретов", publicJson.Contains("Старая обитель") && publicJson.Contains("обитель")
            && !publicJson.Contains("Истинное") && !publicJson.Contains("Истинная") && !publicJson.Contains("Секрет ведущего"));
        _editor.GoToHexKey(h); await Settle();
        var viewport = _editor.Session.Viewport;
        viewport.ZoomAt(viewport.ViewWidth / 2, viewport.ViewHeight / 2, 3);
        _editor.GoToHexKey(h); await Settle();
        Check("R17: ведущий сохраняет истинный ключ", _editor.HexKeyNote!.Title == "Истинное имя R17"
            && _editor.HexKeyNote.GmText == "Секрет ведущего R17");
        CaptureGameplayWindow(_window, "r17-gm");
        PressHex(_view, "HexModeParty"); await Settle();
        _view.Canvas.FitToCanvas(); await Settle(); CaptureGameplayWindow(_window, "r17-party");
        var player = new HexPlayerWindow(_editor) { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        player.Show(); await Settle(); await Task.Delay(300); await Settle();
        var labels = player.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Check("R17: окно игрока показывает карту знаний", labels.Any(t => t.Contains("Старая обитель"))
            && labels.Any(t => t.Contains("обитель.")) && !labels.Any(t => t.Contains("Истинное") || t.Contains("Секрет ведущего")));
        CaptureGameplayWindow(player, "r17-player"); player.Close();
        var presentation = _view.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "HexPresentationChoice");
        presentation.SelectedIndex = 3;
        viewport.ZoomAt(viewport.ViewWidth / 2, viewport.ViewHeight / 2, 3);
        _editor.GoToHexKey(h); await Settle(); CaptureGameplayWindow(_window, "r17-knowledge");
        presentation.SelectedIndex = 0;
        var path = Path.Combine(_workspace, "r17-live.mapproj");
        MapProjectStore.Save(_editor.Session.Document, path, new SaveMetadata("r17", DateTimeOffset.UtcNow), PreviewPng());
        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single(l => l.Level == 0);
        Check("R17: сохранение возвращает партию и знания", reopened.Party.Position == next && reopened.Party.Visited.Contains(h)
            && reopened.Party.History.SequenceEqual(Layer().Party.History) && reopened.Notes[h] == Layer().Notes[h]);
    }

    // Снимок только собственного HWND через PrintWindow. Основной дисплей и буфер экрана не читаются.
    private static void CaptureGameplayWindow(Window window, string name)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !R17GetWindowRect(handle, out var rect)) throw new InvalidOperationException("Нет окна для снимка R17.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var header = new R17BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dc = R17CreateCompatibleDC(IntPtr.Zero);
        var dib = R17CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var previous = R17SelectObject(dc, dib);
        try
        {
            if (dib == IntPtr.Zero || !R17PrintWindow(handle, dc, 2)) throw new InvalidOperationException("PrintWindow не создал снимок R17.");
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var pixmap = new SKPixmap(info, bits, width * 4);
            using var image = SKImage.FromPixels(pixmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, name + ".png"));
            data.SaveTo(file);
        }
        finally { R17SelectObject(dc, previous); R17DeleteObject(dib); R17DeleteDC(dc); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct R17Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct R17BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool R17GetWindowRect(IntPtr h, out R17Rect rect);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")] private static extern bool R17PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static extern IntPtr R17CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")] private static extern IntPtr R17CreateDIBSection(IntPtr dc, ref R17BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern IntPtr R17SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool R17DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] private static extern bool R17DeleteDC(IntPtr dc);
}
