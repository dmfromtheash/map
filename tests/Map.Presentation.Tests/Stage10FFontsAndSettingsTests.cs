using System.Text;
using Map.Platform.Windows;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class Stage10FFontsAndSettingsTests
{
    [Fact]
    public void Default_registry_is_conflict_free_and_allows_only_non_overlapping_reuse()
    {
        var settings = new EditorShortcutSettings();

        Assert.Empty(settings.GetConflicts());

        settings.Set(EditorShortcutSettings.Commands.MaskMode, "P");
        settings.Set(EditorShortcutSettings.Commands.StampDensityUp, "P");
        Assert.Empty(settings.GetConflicts()); // Mask and Stamp tools cannot be active together.

        settings.Set(EditorShortcutSettings.Commands.SidePanel, "V");
        var conflict = Assert.Single(settings.GetConflicts());
        Assert.Equal("V", conflict.Gesture);
        Assert.Contains(EditorShortcutSettings.Commands.Select,
            new[] { conflict.First.Id, conflict.Second.Id });
    }

    [Fact]
    public void Unassign_and_resets_are_explicit_and_deterministic()
    {
        var settings = new EditorShortcutSettings();
        settings.Set(EditorShortcutSettings.Commands.Text, "Alt+T");
        settings.Set(EditorShortcutSettings.Commands.FontNext, "");

        Assert.Equal("Alt+T", settings.Get(EditorShortcutSettings.Commands.Text));
        Assert.Equal("", settings.Get(EditorShortcutSettings.Commands.FontNext));

        settings.Reset(EditorShortcutSettings.Commands.Text);
        Assert.Equal("T", settings.Get(EditorShortcutSettings.Commands.Text));

        settings.ResetAll();
        Assert.Equal("]", settings.Get(EditorShortcutSettings.Commands.FontNext));
        Assert.Empty(settings.GetOverrides());
    }

    [Fact]
    public void Local_font_catalog_deduplicates_case_insensitively_and_sorts_stably()
    {
        var normalized = SystemFontCatalog.Normalize(
            [" Zulu ", "arial", "Arial", null, "", "beta", "Beta"]);

        Assert.Equal(["arial", "beta", "Zulu"], normalized);
    }

    [Fact]
    public void Requested_font_stays_visible_and_one_selection_is_exactly_undoable()
    {
        var editor = NewEditor(["Zulu", "Arial", "Beta"]);
        editor.AddText();

        editor.SetSelectedTextFontFamily("Missing Map Font");
        Assert.Equal("Missing Map Font", editor.Session.SelectedObject!.Text!.RequestedFontFamily);
        Assert.Equal("Missing Map Font", editor.FontFamiliesForSearch("beta")[0]);
        Assert.True(editor.SelectedTextFontResolution!.UsedFallback);

        editor.SetSelectedTextFontFamily("Beta");
        Assert.Equal("Beta", editor.Session.SelectedObject.Text!.RequestedFontFamily);
        editor.SetSelectedTextFontFamily("Beta"); // no-op: must not create another history entry

        editor.Undo();
        Assert.Equal("Missing Map Font", editor.Session.SelectedObject.Text!.RequestedFontFamily);
        editor.Redo();
        Assert.Equal("Beta", editor.Session.SelectedObject.Text!.RequestedFontFamily);
        Assert.Equal("Arial", editor.Session.SelectedObject.Text.FallbackFontFamily);

        var textId = editor.Session.SelectedObject.Id;
        editor.AddObject(Map.Core.Documents.SceneObjectKind.Rectangle);
        var rectangleId = editor.Session.SelectedObject!.Id;
        editor.Session.SetSelection([textId, rectangleId], textId);
        editor.SetSelectedTextFontFamily("Zulu");
        Assert.Equal("Beta", editor.Session.SelectedObject!.Text!.RequestedFontFamily);
    }

    [Fact]
    public void Font_cycle_uses_the_stable_local_order_and_wraps()
    {
        var editor = NewEditor(["Zulu", "Arial", "Beta"]);
        editor.AddText();

        editor.SelectAdjacentTextFont(-1);
        Assert.Equal("Zulu", editor.Session.SelectedObject!.Text!.RequestedFontFamily);
        editor.SelectAdjacentTextFont(1);
        Assert.Equal("Arial", editor.Session.SelectedObject.Text!.RequestedFontFamily);
    }

    [Fact]
    public void Shortcut_settings_round_trip_and_restart_with_only_known_ids()
    {
        WithTempDirectory(directory =>
        {
            var path = System.IO.Path.Combine(directory, "shortcuts.json");
            var store = new ShortcutSettingsStore(path);
            var settings = new EditorShortcutSettings();
            settings.Set(EditorShortcutSettings.Commands.Text, "Alt+T");
            settings.Set(EditorShortcutSettings.Commands.FontNext, "");

            var saved = store.Save(settings.GetOverrides(), EditorShortcutSettings.KnownCommandIds);
            Assert.True(saved.Succeeded, saved.Error);

            var loaded = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.Null(loaded.Warning);
            var restarted = new EditorShortcutSettings();
            Assert.Equal(2, restarted.ApplyPersisted(loaded.Bindings));
            Assert.Equal("Alt+T", restarted.Get(EditorShortcutSettings.Commands.Text));
            Assert.Equal("", restarted.Get(EditorShortcutSettings.Commands.FontNext));
            Assert.Empty(restarted.GetConflicts());
        });
    }

    [Fact]
    public void Unknown_malformed_entry_is_ignored_without_losing_a_known_binding()
    {
        WithTempDirectory(directory =>
        {
            var path = System.IO.Path.Combine(directory, "shortcuts.json");
            File.WriteAllText(path,
                """{"version":1,"bindings":{"text":"Alt+T","future-command":{"broken":true}}}""",
                Encoding.UTF8);

            var result = new ShortcutSettingsStore(path).Load(EditorShortcutSettings.KnownCommandIds);

            Assert.Null(result.Warning);
            Assert.Equal("Alt+T", Assert.Single(result.Bindings).Value);
        });
    }

    [Fact]
    public void Damaged_settings_are_not_changed_on_load_and_are_backed_up_on_explicit_save()
    {
        WithTempDirectory(directory =>
        {
            var path = System.IO.Path.Combine(directory, "shortcuts.json");
            var damaged = Encoding.UTF8.GetBytes("{ definitely broken");
            File.WriteAllBytes(path, damaged);
            var store = new ShortcutSettingsStore(path);

            var loaded = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.NotNull(loaded.Warning);
            Assert.Empty(loaded.Bindings);
            Assert.Equal(damaged, File.ReadAllBytes(path));

            var saved = store.Save(
                new Dictionary<string, string> { [EditorShortcutSettings.Commands.Text] = "Alt+T" },
                EditorShortcutSettings.KnownCommandIds);
            Assert.True(saved.Succeeded, saved.Error);
            Assert.Equal(damaged, File.ReadAllBytes(path + ".bak"));
        });
    }

    private static EditorViewModel NewEditor(IReadOnlyList<string> fonts) => new(
        TestData.Document(), documentPath: null, startDirty: false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
        new FakePrompts(), new FakeLog(), availableFontFamilies: fonts);

    private static void WithTempDirectory(Action<string> action)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "map-stage10f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
