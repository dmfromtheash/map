using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

/// <summary>
/// R13: --map fixture.mapproj --poster-export true --out artifacts/r13/live-standard (or live-hex).
/// Drives both system destination dialogs, reads the PDF and PNG headers. Captures only Map
/// windows through Program's PrintWindow. All destinations and profiles stay under --out.
/// </summary>
internal static class PosterExportCheck
{
    public static int Run(Window main, Window export, string output, Action<string> log, Action<Window, string> shot)
    {
        AutomationElement? Find(string id) => export.FindFirstDescendant(cf => cf.ByAutomationId(id));
        var poster = Find("ExportPosterButton");
        if (poster is null || poster.IsOffscreen) poster = Find("ExportPackagePrintButton");
        if (poster is null || !poster.IsEnabled) { log("MISSING: poster button"); return 1; }
        log("R13 poster action: " + poster.AutomationId);
        poster.AsButton().Invoke();
        var print = Retry.WhileNull(() => export.ModalWindows.Concat(main.ModalWindows)
            .FirstOrDefault(w => w.AutomationId == "HexPrintWindow"), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        if (print is null) { log("MISSING: HexPrintWindow"); return 1; }
        var paper = print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintPaperComboBox"))!.AsComboBox();
        paper.Select(2); paper.Collapse();
        Number(print, "HexPrintBleedBox", "3"); Number(print, "HexPrintOverlapBox", "12");
        print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintCropMarksToggle"))!.AsCheckBox().IsChecked = true;
        var key = print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintKeyToggle"));
        if (key is not null) key.AsCheckBox().IsChecked = false;
        var mapWidth = print.FindFirstDescendant(cf => cf.ByAutomationId("PosterPrintMapWidthBox"));
        if (mapWidth is not null && !mapWidth.IsOffscreen) Number(print, "PosterPrintMapWidthBox", "400");
        Thread.Sleep(400); shot(print, Path.Combine(output, "r13-print-letter.png"));
        paper.Select(3); paper.Collapse();
        Number(print, "HexPrintWidthBox", "180"); Number(print, "HexPrintHeightBox", "240");
        Thread.Sleep(400); shot(print, Path.Combine(output, "r13-print-custom.png"));
        print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintSaveButton"))!.AsButton().Invoke();
        var pdf = Path.Combine(output, "poster.pdf");
        if (!Destination(main, export, print, "PDF для печати", pdf, log)) return 1;
        if (!Retry.WhileFalse(() => File.Exists(pdf), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)).Result)
        { log("MISSING: poster.pdf"); return 1; }
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        var pages = Regex.Matches(text, @"/Type\s*/Page\b").Count;
        var box = Regex.Match(text, @"/MediaBox\s*\[0 0 ([\d.]+) ([\d.]+)\]");
        if (!box.Success || pages < 2) { log("FAIL: PDF pages/MediaBox"); return 1; }
        var w = double.Parse(box.Groups[1].Value, CultureInfo.InvariantCulture) * 25.4 / 72;
        var h = double.Parse(box.Groups[2].Value, CultureInfo.InvariantCulture) * 25.4 / 72;
        if (Math.Abs(w - 180) > 0.05 || Math.Abs(h - 240) > 0.05) { log($"FAIL: PDF {w} x {h} mm"); return 1; }
        log($"R13 PDF read: {pages} pages, {w:0.###} x {h:0.###} mm; {pdf}");
        Thread.Sleep(400); shot(print, Path.Combine(output, "r13-print-written.png"));
        print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintCloseButton"))!.AsButton().Invoke();
        var aspect = Find("ExportAspectToggle");
        if (aspect?.Patterns.Toggle.IsSupported == true && aspect.Patterns.Toggle.Pattern.ToggleState == ToggleState.On) aspect.Patterns.Toggle.Pattern.Toggle();
        Find("ExportOutputWidthBox")!.AsTextBox().Text = "640";
        Find("ExportOutputHeightBox")!.AsTextBox().Text = "480";
        Thread.Sleep(700);
        Find("ExportLayersButton")!.AsButton().Invoke();
        var folder = Path.Combine(output, "layers");
        if (!Destination(main, export, null, "Новая папка PNG-слоёв", folder, log)) return 1;
        if (!Retry.WhileFalse(() => Directory.Exists(folder), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250)).Result)
        { log("MISSING: layer folder"); return 1; }
        var files = Directory.GetFiles(folder, "*.png").Order().ToArray();
        var valid = files.Length >= 2;
        foreach (var file in files)
        {
            var header = File.ReadAllBytes(file);
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
            valid &= header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && width == 640 && height == 480;
            log($"R13 PNG read: {Path.GetFileName(file)} {width} x {height}");
        }
        Thread.Sleep(400); shot(export, Path.Combine(output, "r13-layers-written.png"));
        log("R13 layers status: " + Find("ExportLayersResultText")?.Name);
        log("R13 live poster/layers: " + (valid ? "PASS" : "FAIL"));
        return valid ? 0 : 1;
    }

    private static void Number(Window window, string id, string value)
    {
        var control = window.FindFirstDescendant(cf => cf.ByAutomationId(id))!;
        var edit = control.ControlType == ControlType.Edit ? control : control.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (edit is null) throw new InvalidOperationException("No numeric editor: " + id);
        edit.AsTextBox().Text = value;
        Keyboard.Type(VirtualKeyShort.TAB);
    }

    private static bool Destination(Window main, Window export, Window? print, string title, string path, Action<string> log)
    {
        var picker = Retry.WhileNull(() => main.ModalWindows.Concat(export.ModalWindows).Concat(print?.ModalWindows ?? [])
            .FirstOrDefault(w => w.Name == title), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        if (picker is null) { log("MISSING: " + title); return false; }
        var input = picker.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByControlType(ControlType.Edit)))
            ?? picker.FindFirstDescendant(cf => cf.ByAutomationId("FileNameControlHost"))?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (input is null) { log("MISSING: destination field"); picker.Close(); return false; }
        Thread.Sleep(600); input.Focus(); input.AsTextBox().Text = path; Thread.Sleep(300);
        if (!string.Equals(input.AsTextBox().Text, path, StringComparison.Ordinal)) { log("FAIL: destination field"); picker.Close(); return false; }
        Keyboard.Type(VirtualKeyShort.TAB);
        // File rows also reuse id 1. Require the actual Save button.
        var save = picker.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)))?.AsButton();
        if (save is null) { log("MISSING: Save button"); picker.Close(); return false; }
        log("R13 destination: " + path + "; action=" + save.Name);
        save.Invoke(); return true;
    }
}
