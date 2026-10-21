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

        // «P» became the Land tool (editor-wide) in 2026-09; a gesture no editor command owns
        // shows the same rule: Mask and Stamp contexts never overlap, so one key serves both.
        settings.Set(EditorShortcutSettings.Commands.MaskMode, "Alt+M");
        settings.Set(EditorShortcutSettings.Commands.StampDensityUp, "Alt+M");
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
    public void Local_font_catalog_has_a_hard_deterministic_family_limit()
    {
        var normalized = SystemFontCatalog.Normalize(
            Enumerable.Range(0, 5_000).Reverse().Select(index => $"Family {index:D4}"));

        Assert.Equal(4_096, normalized.Count);
        Assert.Equal("Family 0000", normalized[0]);
        Assert.Equal("Family 4095", normalized[^1]);
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
    public void Shortcut_settings_store_requires_an_explicit_path()
    {
        Assert.Throws<ArgumentNullException>(() => new ShortcutSettingsStore(null!));
        Assert.Throws<ArgumentException>(() => new ShortcutSettingsStore("relative-shortcuts.json"));
    }

    [Fact]
    public void Test_mode_settings_path_is_absolute_isolated_and_fail_closed()
    {
        var production = AppPaths.ShortcutSettingsPath;
        var isolated = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "map-stage10f-isolated", "shortcuts.json");

        Assert.Equal(System.IO.Path.GetFullPath(production),
            AppPaths.ResolveShortcutSettingsPath(testMode: false, testOverride: null));
        Assert.Equal(System.IO.Path.GetFullPath(isolated),
            AppPaths.ResolveShortcutSettingsPath(testMode: true, isolated));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveShortcutSettingsPath(testMode: true, testOverride: null));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveShortcutSettingsPath(testMode: true, "relative-shortcuts.json"));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveShortcutSettingsPath(testMode: true, production));
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
    public void Oversized_nested_duplicate_and_overlong_profiles_fail_safely()
    {
        WithTempDirectory(directory =>
        {
            var path = System.IO.Path.Combine(directory, "shortcuts.json");
            var store = new ShortcutSettingsStore(path);

            var oversized = new byte[ShortcutSettingsStore.MaxEncodedBytes + 1];
            File.WriteAllBytes(path, oversized);
            var oversizedResult = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.NotNull(oversizedResult.Warning);
            Assert.Empty(oversizedResult.Bindings);
            Assert.Equal(oversized.Length, new FileInfo(path).Length);

            File.WriteAllText(path,
                "{\"version\":1,\"bindings\":{\"text\":[[[[[[[[[\"T\"]]]]]]]]]}}",
                Encoding.UTF8);
            var nestedResult = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.NotNull(nestedResult.Warning);
            Assert.Empty(nestedResult.Bindings);

            var duplicates = string.Join(",",
                Enumerable.Repeat("\"text\":\"T\"", ShortcutSettingsStore.MaxBindings + 1));
            File.WriteAllText(path, $"{{\"version\":1,\"bindings\":{{{duplicates}}}}}", Encoding.UTF8);
            var duplicateResult = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.NotNull(duplicateResult.Warning);
            Assert.Empty(duplicateResult.Bindings);

            File.WriteAllText(path,
                $"{{\"version\":1,\"bindings\":{{\"text\":\"{new string('Ж', 129)}\"}}}}",
                Encoding.UTF8);
            var overlongResult = store.Load(EditorShortcutSettings.KnownCommandIds);
            Assert.NotNull(overlongResult.Warning);
            Assert.Empty(overlongResult.Bindings);
        });
    }

    [Fact]
    public void Write_failure_is_reported_and_cannot_be_mistaken_for_success()
    {
        WithTempDirectory(directory =>
        {
            var blockedPath = System.IO.Path.Combine(directory, "blocked-shortcuts.json");
            Directory.CreateDirectory(blockedPath);
            var result = new ShortcutSettingsStore(blockedPath).Save(
                new Dictionary<string, string> { [EditorShortcutSettings.Commands.Text] = "Alt+T" },
                EditorShortcutSettings.KnownCommandIds);

            Assert.False(result.Succeeded);
            Assert.Contains("Не удалось сохранить", result.Error);
            Assert.True(Directory.Exists(blockedPath));
            Assert.Empty(Directory.EnumerateFiles(directory, "blocked-shortcuts.json.tmp-*"));
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
