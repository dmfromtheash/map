using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

/// <summary>R03 live pass. --map ... --export-wins true; or --library-wins fixture-folder --library-folder target-folder.
/// Only Map windows are captured via Program's PrintWindow. Fixtures are copied into the isolated profile.</summary>
internal static class ExportWinsCheck
{
    public static int Export(Window window, string output, Action<string> log, Action<Window, string> shot, Action<AutomationElement, string> dump)
    {
        var missing = 0;
        AutomationElement? Find(string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id));
        var unit = Find("ExportSizeUnitComboBox")?.AsComboBox();
        var dpi = Find("ExportDpiBox")?.AsTextBox();
        var width = Find("ExportOutputWidthBox")?.AsTextBox();
        var height = Find("ExportOutputHeightBox")?.AsTextBox();
        if (unit is null || dpi is null || width is null || height is null) { log("MISSING: R03 units/dimensions/DPI"); return 1; }
        unit.Select(1); unit.Collapse();
        var aspect = Find("ExportAspectToggle");
        if (aspect?.Patterns.Toggle.IsSupported == true && aspect.Patterns.Toggle.Pattern.ToggleState == ToggleState.On) aspect.Patterns.Toggle.Pattern.Toggle();
        dpi.Text = "300"; width.Text = "210"; height.Text = "297";
        Thread.Sleep(1100);
        dump(window, Path.Combine(output, "r03-export-tree.txt"));
        shot(window, Path.Combine(output, "r03-export-mm.png"));
        log($"R03 mm: width={width.Text}; height={height.Text}; DPI={dpi.Text}");
        if (width.Text != "210" || height.Text != "297" || dpi.Text != "300") missing++;
        unit.Select(2); unit.Collapse(); Thread.Sleep(600);
        log($"R03 inches: width={width.Text}; height={height.Text}; enabled={unit.IsEnabled}");
        if (!width.Text.StartsWith("8.267", StringComparison.Ordinal)) missing++;
        shot(window, Path.Combine(output, "r03-export-inches.png"));
        var printButton = Find("ExportPackagePrintButton");
        if (printButton is null) { log("MISSING: ExportPackagePrintButton"); return missing + 1; }
        printButton.AsButton().Invoke();
        var print = Retry.WhileNull(() => window.ModalWindows.FirstOrDefault(w => w.AutomationId == "HexPrintWindow"), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        if (print is null) { log("MISSING: HexPrintWindow"); return missing + 1; }
        var paper = print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintPaperComboBox"))!.AsComboBox();
        paper.Select(2); paper.Collapse();
        print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintCropMarksToggle"))!.AsCheckBox().IsChecked = true;
        SetNumber(print, "HexPrintBleedBox", "3"); Thread.Sleep(500);
        dump(print, Path.Combine(output, "r03-print-tree.txt")); shot(print, Path.Combine(output, "r03-print-letter.png"));
        log("R03 print: Letter selected, crop marks on, bleed 3 mm");
        paper.Select(3); paper.Collapse();
        SetNumber(print, "HexPrintWidthBox", "180"); SetNumber(print, "HexPrintHeightBox", "240");
        Thread.Sleep(500); shot(print, Path.Combine(output, "r03-print-custom.png"));
        if (!print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintWidthBox"))!.IsEnabled) missing++;
        log("R03 print: custom 180 x 240 mm enabled");
        print.FindFirstDescendant(cf => cf.ByAutomationId("HexPrintCloseButton"))!.AsButton().Invoke();
        log($"R03 live export/print: {(missing == 0 ? "PASS" : "FAIL")}");
        return missing;
    }

    private static void SetNumber(Window window, string id, string value)
    {
        var control = window.FindFirstDescendant(cf => cf.ByAutomationId(id))!;
        var edit = control.ControlType == ControlType.Edit ? control : control.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (edit is null) throw new InvalidOperationException("No numeric editor: " + id);
        edit.AsTextBox().Text = value;
    }

    public static int Library(Window window, string output, string? targetFolder, Action<string> log, Action<Window, string> shot, Action<AutomationElement, string> dump)
    {
        var missing = 0;
        var filter = window.FindFirstDescendant(cf => cf.ByAutomationId("LibraryCategoryFilterHome"))?.AsComboBox();
        if (filter is null) { log("MISSING: LibraryCategoryFilterHome"); return 1; }
        bool Badge(string label) => window.FindAllDescendants(cf => cf.ByName(label)).Any(e => !e.IsOffscreen && e.ControlType == ControlType.Text);
        if (!Badge("Стандартный") || !Badge("Гексовый")) { missing++; log("MISSING: visible category badges"); }
        shot(window, Path.Combine(output, "r03-library-home.png"));
        filter.Select(2); filter.Collapse(); Thread.Sleep(700);
        if (!Badge("Гексовый") || Badge("Стандартный")) missing++;
        shot(window, Path.Combine(output, "r03-library-filtered.png"));
        log("R03 home filter: hex badge visible; standard hidden=" + !Badge("Стандартный"));
        window.FindFirstDescendant(cf => cf.ByAutomationId("RailMapsButton"))!.Patterns.Toggle.Pattern.Toggle(); Thread.Sleep(500);
        var mapsFilter = window.FindFirstDescendant(cf => cf.ByAutomationId("LibraryCategoryFilterMaps"))!.AsComboBox();
        mapsFilter.Select(0); mapsFilter.Collapse(); Thread.Sleep(500);
        dump(window, Path.Combine(output, "r03-library-tree.txt")); shot(window, Path.Combine(output, "r03-library-maps.png"));
        var folder = window.FindFirstDescendant(cf => cf.ByAutomationId("LibraryFolderButton"));
        if (folder is null || !folder.IsEnabled) { missing++; log("MISSING: LibraryFolderButton"); }
        else if (targetFolder is not null)
        {
            folder.AsButton().Invoke();
            var picker = Retry.WhileNull(() => window.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
            if (picker is null) { missing++; log("MISSING: folder picker"); }
            else
            {
                dump(picker, Path.Combine(output, "r03-folder-picker-tree.txt"));
                var input = picker.FindFirstDescendant(cf => cf.ByAutomationId("1152"));
                if (input is null || input.ControlType != ControlType.Edit) input = input?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                input ??= picker.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).FirstOrDefault(e => !e.IsOffscreen && e.IsEnabled);
                if (input is null) { missing++; log("MISSING: folder picker path field"); picker.Close(); }
                else
                {
                    input.AsTextBox().Text = Path.GetFullPath(targetFolder);
                    picker.FindFirstDescendant(cf => cf.ByAutomationId("1"))!.AsButton().Invoke();
                    Thread.Sleep(1000);
                    var label = window.FindFirstDescendant(cf => cf.ByAutomationId("LibraryFolderLabel"));
                    if (label is null || !label.Name.Contains(Path.GetFullPath(targetFolder), StringComparison.OrdinalIgnoreCase)) { missing++; log("MISMATCH: library folder label=" + label?.Name); }
                    else log("R03 folder picker: selected target and list reloaded");
                    shot(window, Path.Combine(output, "r03-library-folder-changed.png"));
                }
            }
        }
        log($"R03 live library: {(missing == 0 ? "PASS" : "FAIL")}");
        return missing;
    }
}
