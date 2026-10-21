using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

/// <summary>R02 live UIA pass; screenshots are supplied by Program's PrintWindow-only capture.</summary>
internal static class PlayerPackageCheck
{
    public static int Run(Window window, string output, Action<string> log,
        Action<Window, string> shot, Action<AutomationElement, string> dump)
    {
        var missing = 0;
        var recipient = window.FindFirstDescendant(cf => cf.ByAutomationId("ExportRecipientComboBox"));
        if (recipient is null) { log("MISSING: ExportRecipientComboBox"); return 1; }
        var combo = recipient.AsComboBox();
        combo.Select(1);
        combo.Collapse();
        Thread.Sleep(900);
        // Avalonia's layout-only StackPanel has no UIA peer. The native smoke checks the
        // panel itself; the live pass checks its visible heading and all five content lines.
        var heading = window.FindFirstDescendant(cf => cf.ByName("Что попадёт в пакет"));
        if (heading is null || heading.IsOffscreen)
        {
            missing++;
            log("MISSING or offscreen: player package heading");
        }
        else log($"R02 package heading: {heading.Name}; bounds={heading.BoundingRectangle}");
        foreach (var id in new[] { "ExportPackagePreviewMapsText", "ExportPackagePreviewNotesText",
            "ExportPackagePreviewLayersText", "ExportPackagePreviewLabelsText", "ExportPackagePreviewFogText" })
        {
            var part = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (part is null || part.IsOffscreen)
            {
                missing++;
                log("MISSING or offscreen: " + id);
                continue;
            }
            log($"R02 {id}: {part.Name}; bounds={part.BoundingRectangle}");
            var expected = id switch
            {
                "ExportPackagePreviewNotesText" => "всего 3 · в пакете 1 · скрыто 2",
                "ExportPackagePreviewFogText" => "60 гексов, 1 заметок",
                _ => null,
            };
            if (expected is not null && !part.Name.Contains(expected, StringComparison.Ordinal))
            {
                missing++;
                log($"MISMATCH {id}: expected '{expected}', got '{part.Name}'");
            }
        }
        var ready = Retry.WhileNull(() =>
            {
                var start = window.FindFirstDescendant(cf => cf.ByAutomationId("StartExportButton"));
                return start is { IsEnabled: true } ? start : null;
            }, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250)).Result;
        if (ready is null)
        {
            missing++;
            log("NOT READY: StartExportButton is missing or disabled after selecting the player recipient");
        }
        else log("R02 StartExportButton enabled: true");
        dump(window, Path.Combine(output, "r02-player-package-tree.txt"));
        shot(window, Path.Combine(output, "r02-player-package.png"));
        log($"R02 live player package: {(missing == 0 ? "PASS" : "FAIL")}");
        return missing;
    }
}
