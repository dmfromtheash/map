using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    /// <summary>
    /// `native-r09-live`: the live pass of R09 in the real editor window (placed off-screen, no system input). The small
    /// map with the owner's mountains (<see cref="StylePacksSheet.DemoMap"/>) goes through «Вид» as a user would: every
    /// style picture on the ribbon (winter and pixel ones included), the four style packs, the starting map variants
    /// («День», «Ночь», «Зима», «Политическая»), and «Экспорт» → «Сохранить хендаут…» for every sheet. Each click must be
    /// one undo step; each result is read from the canvas's own GPU frame once the map is finished
    /// (`&lt;output&gt;.r09-&lt;what&gt;.jpg`, handouts as `…r09-handout-&lt;id&gt;.png`).
    /// </summary>
    private async Task R09LiveOnly()
    {
        // The owner's mountains reach the canvas through the library, as in the program (DEC-076).
        var catalog = new Map.Assets.AssetCatalog(Path.Combine(_workspace, "r09-catalog"));
        catalog.ImportSet([.. Enumerable.Range(1, 6).Select(i => OwnerMountainFixtures.Path(i))], OwnerMountainFixtures.Names[0],
            new Map.Assets.AssetImportOptions(), DateTimeOffset.UnixEpoch);
        OpenEditor(StylePacksSheet.DemoMap(), null, catalog); await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1600, _view.Canvas.Bounds.Height / 1000));
        var bad = new List<string>();
        var timings = new List<string>();

        async Task<double> Shot(string key)
        {
            var watch = Stopwatch.StartNew();
            var asked = _view.Canvas.RequestFrameForSmoke();
            while (watch.Elapsed < TimeSpan.FromSeconds(60)
                   && !(_view.Canvas.CompletedSceneRevision >= asked && _view.Canvas.FrameSettled))
            { await Task.Delay(100); await Settle(); }
            var settled = watch.Elapsed.TotalMilliseconds;
            SKBitmap? captured = null;
            for (var attempt = 0; attempt < 3 && captured is null; attempt++)
                try { captured = await _view.Canvas.CaptureGpuFrameForSmokeAsync(); }
                catch (TimeoutException) { await Settle(); }
            if (captured is null) { bad.Add(key + ": кадр не снят"); return settled; }
            using var bitmap = captured;
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            var path = Path.ChangeExtension(Options.OutputPath, $"r09-{key}.jpg");
            using (var file = File.Create(path)) data.SaveTo(file);
            timings.Add($"{key}: {settled:0} ms");
            Console.WriteLine($"r09 {key}: settled in {settled:0} ms");
            if (settled >= 60000) bad.Add(key + ": карта не дорисовалась за 60 с");
            return settled;
        }
        void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button[] Named(string prefix) => [.. _view.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith(prefix, StringComparison.Ordinal) == true)];

        _view.ShowWorkspace(EditorWorkspace.Appearance, openWorldList: true); await Settle();
        await Shot("plain");
        // MAP_R09_HANDOUTS_ONLY=1 repeats only the handouts.
        if (Environment.GetEnvironmentVariable("MAP_R09_HANDOUTS_ONLY") != "1") await Look();
        await Handouts();
        foreach (var line in bad) Console.WriteLine("r09: " + line);
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "r09-timings.txt"), timings);
        _report.Checks["r09-live-pass"] = bad.Count == 0;

    async Task Look()
    {
        var plain = _editor.Session.Document;

        // Every style picture of «Вид» → «Стиль».
        var styles = Named("LookCard_style-").Select(b => b.Name!).ToArray();
        Console.WriteLine($"r09 style pictures: {styles.Length}");
        if (styles.Length < 20) bad.Add($"картинок стиля {styles.Length}");
        foreach (var name in styles)
        {
            var depth = _editor.Session.UndoDepth;
            var button = Named(name).Single(); button.BringIntoView(); await Settle();
            Press(button); await Settle();
            var preset = name["LookCard_style-".Length..];
            if (_editor.Session.Document.Style?.Preset.ToString() != preset || _editor.Session.UndoDepth != depth + 1)
                bad.Add($"{preset}: щелчок не дал стиль одним шагом");
            await Shot("style-" + preset);
            _editor.Undo(); await Settle();
            if (!ReferenceEquals(_editor.Session.Document, plain)) bad.Add($"{preset}: Ctrl+Z не вернул карту");
        }

        // The four style packs.
        foreach (var tile in Named("StylePack_").Select(b => b.Name!).ToArray())
        {
            var depth = _editor.Session.UndoDepth;
            Press(Named(tile).Single()); await Settle();
            if (_editor.Session.UndoDepth != depth + 1) bad.Add(tile + ": не один шаг");
            await Shot("pack-" + tile["StylePack_".Length..]);
            _editor.Undo(); await Settle();
        }

        // The starting map variants, «Зима» among them.
        Press(Find<Button>("MapVariantStarters")); await Settle();
        foreach (var variant in _editor.Session.Document.Variants.Items.ToArray())
        {
            var tile = Named("MapVariant_" + variant.Id.ToString("N")).SingleOrDefault();
            if (tile is null) { bad.Add("нет плитки варианта " + variant.Name); continue; }
            Press(tile); await Settle();
            if (_editor.Session.Document.Variants.Active != variant.Id) bad.Add("вариант не показан: " + variant.Name);
            await Shot("variant-" + MapHandoutSafe(variant.Name));
        }
        if (_editor.Session.Document.Variants.Items.All(v => v.Name != "Зима")) bad.Add("нет варианта «Зима»");
        _editor.Undo(); await Settle();
        while (_editor.Session.UndoDepth > 0) { _editor.Undo(); await Settle(); }

    }

    // «Экспорт» → «Сохранить хендаут…» for every sheet, in the export window itself.
    async Task Handouts()
    {
        _view.OpenExport(); await _editor.WaitForExportPreviewAsync(); await Settle();
        var window = _view.ExportWindowForSmoke!;
        T InExport<T>(string name) where T : Control
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
                foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded)) expander.IsExpanded = true;
                window.UpdateLayout();
            }
            throw new InvalidOperationException(name + " is not in the export window");
        }
        var exported = Path.Combine(_workspace, "river-export.png");
        foreach (var handout in MapHandouts.All.Where(h => h != MapHandout.None))
        {
            var combo = InExport<ComboBox>("ExportHandoutComboBox");
            combo.SelectedIndex = MapHandouts.All.ToList().IndexOf(handout); await Settle();
            if (File.Exists(exported)) File.Delete(exported);
            var watch = Stopwatch.StartNew();
            Press(InExport<Button>("ExportHandoutButton"));
            while (!File.Exists(exported) && watch.Elapsed < TimeSpan.FromSeconds(90)) { await Task.Delay(200); await Settle(); }
            await Task.Delay(300);
            if (!File.Exists(exported)) { bad.Add("хендаут не сохранён: " + MapHandouts.Name(handout) + " · " + _editor.StatusDetail); continue; }
            var target = Path.ChangeExtension(Options.OutputPath, $"r09-handout-{MapHandouts.Id(handout)}.png");
            File.Copy(exported, target, overwrite: true);
            Console.WriteLine($"r09 handout {MapHandouts.Id(handout)}: {watch.Elapsed.TotalMilliseconds:0} ms, {new FileInfo(target).Length / 1024} KB");
            timings.Add($"handout-{MapHandouts.Id(handout)}: {watch.Elapsed.TotalMilliseconds:0} ms");
        }

        window.Hide(); await Settle();
    }
    }


    private static string MapHandoutSafe(string name) => new([.. name.Where(char.IsLetterOrDigit)]);
}
