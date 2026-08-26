using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The set-aware stamp tool: which set and which variant are current, what the measurable Area
/// and Density controls do, and that a gesture becomes exactly one undoable command.
/// </summary>
public sealed class Stage10LibraryStampTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-vm", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- library state

    [Fact]
    public void The_library_lists_logical_cards_with_their_badge_count()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);

        editor.RefreshLibrary();

        var card = Assert.Single(editor.LibrarySets, item => item.SetId == set.Id);
        Assert.Equal(12, card.VariantCount);
        Assert.True(card.IsSet);
        Assert.Equal(AssetLibraryStatus.Ready, editor.LibraryStatus);
    }

    [Fact]
    public void Selecting_a_set_exposes_its_ordered_variants_and_starts_on_the_cover()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();

        editor.SelectAssetSet(set.Id);

        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal(12, editor.SelectedSetVariants.Count);
        Assert.Equal(0, editor.CurrentVariantIndex);
        Assert.Equal(set.CoverVariantId, editor.CurrentVariant!.Id);
        Assert.Equal("1 / 12", editor.CurrentVariantLabel);
        Assert.True(editor.IsCurrentVariantTheCover);
    }

    [Fact]
    public void Next_and_previous_variant_wrap_around_the_set_and_update_the_position_label()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);

        editor.NextVariant();
        Assert.Equal("2 / 12", editor.CurrentVariantLabel);
        Assert.False(editor.IsCurrentVariantTheCover);

        editor.PreviousVariant();
        editor.PreviousVariant();
        Assert.Equal("12 / 12", editor.CurrentVariantLabel);

        editor.NextVariant();
        Assert.Equal("1 / 12", editor.CurrentVariantLabel);
    }

    [Fact]
    public void A_singleton_card_reports_one_of_one_and_variant_navigation_stays_put()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(Source("alone", SKColors.Teal), new AssetImportOptions(), Stamp(0));
        editor.RefreshLibrary();

        editor.SelectAssetSet(catalog.FindSetForContent(record.Sha256)!.Id);
        editor.NextVariant();

        Assert.Equal("1 / 1", editor.CurrentVariantLabel);
        Assert.Equal(0, editor.CurrentVariantIndex);
    }

    [Fact]
    public void The_current_variant_of_a_set_is_remembered_across_selections()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        var other = catalog.Import(Source("other", SKColors.Olive), new AssetImportOptions(), Stamp(1));
        editor.RefreshLibrary();

        editor.SelectAssetSet(set.Id);
        editor.NextVariant();
        editor.NextVariant();
        Assert.Equal("3 / 12", editor.CurrentVariantLabel);

        editor.SelectAssetSet(catalog.FindSetForContent(other.Sha256)!.Id);
        editor.SelectAssetSet(set.Id);

        Assert.Equal("3 / 12", editor.CurrentVariantLabel);
    }

    [Fact]
    public void Marking_a_card_as_a_favourite_shows_it_under_the_favourites_filter_after_a_restart()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);

        editor.ToggleSelectedSetFavorite();

        Assert.True(editor.SelectedSet!.Favorite);
        var reopened = new AssetCatalog(CatalogRoot());
        Assert.True(reopened.FindSet(set.Id)!.Favorite);
    }

    [Fact]
    public void Search_sort_and_filters_run_over_logical_cards()
    {
        var editor = Editor(out var catalog);
        ImportTwelve(catalog);
        catalog.Import(Source("башня", SKColors.Sienna), new AssetImportOptions(), Stamp(50));
        editor.RefreshLibrary();

        editor.LibrarySearchText = "башня";
        Assert.Equal(["башня"], editor.LibrarySets.Select(item => item.Name));

        editor.LibrarySearchText = "";
        editor.LibrarySort = AssetSortOrder.NameAscending;
        Assert.Equal(["башня", "Диагностический набор"], editor.LibrarySets.Select(item => item.Name));

        editor.LibraryFavoritesOnly = true;
        Assert.Empty(editor.LibrarySets);
    }

    [Fact]
    public void A_visible_range_realises_only_the_tiles_the_grid_can_show()
    {
        var editor = Editor(out var catalog);
        var document = new AssetLibraryDocument();
        for (var index = 0; index < 5_000; index++)
        {
            var record = Synthetic(index);
            document.Records.Add(record);
            AssetLibraryStore.AddSingleton(document, record);
        }

        catalog.ReplaceLibrary(document);
        editor.RefreshLibrary();

        editor.SetLibraryVisibleRange(1_200, 40);

        Assert.Equal(5_000, editor.LibrarySets.Count);
        Assert.Equal(40, editor.VisibleLibraryTiles.Count);
        Assert.Equal(1_200, editor.VisibleLibraryTileWindow.FirstIndex);
    }

    // ---------------------------------------------------------------- area and density

    [Fact]
    public void Area_and_density_are_measurable_and_drive_the_planned_stamp_count()
    {
        var editor = Editor(out _);

        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.StampAreaRadius = 100;
        editor.StampDensity = 100;

        Assert.Equal(314, editor.PlannedStampCount);

        // In One mode the readout is what will really be placed, not what Area would place.
        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.Equal(1, editor.PlannedStampCount);
        editor.StampPlacementMode = StampPlacementMode.Area;

        editor.AdjustStampArea(1, largerStep: true);
        Assert.Equal(110, editor.StampAreaRadius);
        editor.AdjustStampArea(-1);
        Assert.Equal(109, editor.StampAreaRadius);

        editor.AdjustStampDensity(1, largerStep: true);
        Assert.Equal(110, editor.StampDensity);
        editor.AdjustStampDensity(-1);
        Assert.Equal(109, editor.StampDensity);
    }

    [Fact]
    public void Area_is_also_reported_in_grid_cells_once_a_grid_is_active()
    {
        var editor = Editor(out _);
        editor.StampAreaRadius = 256;

        Assert.Null(editor.StampAreaInCells);

        editor.AddGridLayer();

        Assert.Equal(2.0, editor.StampAreaInCells);
    }

    // ---------------------------------------------------------------- placement

    [Fact]
    public void One_placement_creates_exactly_one_stamp_with_the_current_variant_hash()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.NextVariant();
        var expected = editor.CurrentVariant!.Sha256;

        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.True(editor.PlaceCurrentVariantAt(700, 400));

        var stamp = Assert.Single(Stamps(editor));
        Assert.Equal(expected, stamp.Stamp!.AssetSha256);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Random_after_placement_moves_to_another_variant_only_once_the_command_succeeded()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.RandomVariantAfterPlacement = true;
        editor.StampPlacementMode = StampPlacementMode.One;

        // A locked layer makes the command fail; the current variant must not move.
        editor.ToggleActiveLayerLock();
        var before = editor.CurrentVariantIndex;
        Assert.False(editor.PlaceCurrentVariantAt(100, 100));
        Assert.Equal(before, editor.CurrentVariantIndex);

        editor.ToggleActiveLayerLock();
        Assert.True(editor.PlaceCurrentVariantAt(100, 100));
        Assert.NotEqual(before, editor.CurrentVariantIndex);
    }

    [Fact]
    public void Without_the_random_toggle_the_current_variant_stays_where_the_user_put_it()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.NextVariant();
        editor.NextVariant();
        editor.StampPlacementMode = StampPlacementMode.One;

        Assert.True(editor.PlaceCurrentVariantAt(300, 300));

        Assert.Equal(2, editor.CurrentVariantIndex);
    }

    [Fact]
    public void One_area_gesture_places_several_different_variants_as_a_single_history_entry()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.RandomVariantAfterPlacement = true;
        editor.StampAreaRadius = 200;
        editor.StampDensity = 40;

        Assert.True(editor.PlaceCurrentVariantAt(900, 500));

        var placed = Stamps(editor);
        Assert.Equal(editor.PlannedStampCount, placed.Count);
        Assert.True(placed.Count >= 3);
        var hashes = placed.Select(item => item.Stamp!.AssetSha256).Distinct().ToList();
        Assert.True(hashes.Count >= 3, $"Only {hashes.Count} distinct variants were placed.");
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal(hashes.Count, editor.Session.Document.Assets.Count);
    }

    [Fact]
    public void Undo_removes_the_whole_gesture_and_redo_returns_the_identical_plan()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.RandomVariantAfterPlacement = true;
        editor.StampAreaRadius = 180;
        editor.StampDensity = 30;
        Assert.True(editor.PlaceCurrentVariantAt(600, 600));
        var before = Snapshot(editor);

        editor.Undo();
        Assert.Empty(Stamps(editor));
        Assert.Empty(editor.Session.Document.Assets);

        editor.Redo();

        Assert.Equal(before, Snapshot(editor));
    }

    [Fact]
    public void Replacing_the_variant_of_a_selected_stamp_keeps_its_transform_and_style()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        var set = ImportTwelve(catalog);
        editor.SelectAssetSet(set.Id);
        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.True(editor.PlaceCurrentVariantAt(500, 500));
        var before = Assert.Single(Stamps(editor));

        editor.NextVariant();
        var replacement = editor.CurrentVariant!.Sha256;
        editor.ReplaceSelectedStampWithCurrentVariant();

        var after = Assert.Single(Stamps(editor));
        Assert.Equal(replacement, after.Stamp!.AssetSha256);
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.RotationDegrees, after.RotationDegrees);
        Assert.Equal(before.ObjectOpacity, after.ObjectOpacity);
        Assert.Equal(before.Stamp!.Blend, after.Stamp!.Blend);
        Assert.Equal(2, editor.Session.UndoDepth);
    }

    [Fact]
    public void Selecting_a_placed_stamp_finds_its_containing_set_back_by_hash()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.NextVariant();
        editor.NextVariant();
        var expectedIndex = editor.CurrentVariantIndex;
        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.True(editor.PlaceCurrentVariantAt(400, 400));
        var placed = Assert.Single(Stamps(editor));

        editor.SelectAssetSet(null);
        Assert.Null(editor.SelectedSetId);

        editor.SyncLibraryToSelection(placed.Stamp!.AssetSha256);

        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal(expectedIndex, editor.CurrentVariantIndex);
    }

    [Fact]
    public void A_placement_without_a_selected_set_reports_why_instead_of_doing_nothing_silently()
    {
        var editor = Editor(out _);

        Assert.False(editor.PlaceCurrentVariantAt(100, 100));
        Assert.Contains("набор", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_local_catalog_is_reported_and_placement_is_refused_without_a_crash()
    {
        Directory.CreateDirectory(CatalogRoot());
        File.WriteAllBytes(Path.Combine(CatalogRoot(), "catalog.json"), "{ \"schemaVersion\": 2, ["u8.ToArray());
        var editor = Editor(out _);

        editor.RefreshLibrary();

        Assert.Equal(AssetLibraryStatus.Damaged, editor.LibraryStatus);
        Assert.NotEmpty(editor.LibraryDiagnostic);
        Assert.Empty(editor.LibrarySets);
        Assert.False(editor.PlaceCurrentVariantAt(10, 10));
    }

    [Fact]
    public void Importing_many_files_at_once_creates_one_named_set_in_the_library()
    {
        var editor = Editor(out _);

        var set = editor.ImportAssetSet(
            [.. Enumerable.Range(0, 12).Select(index => Source($"v{index:D2}", Shade(index)))],
            "Диагностический набор");

        Assert.NotNull(set);
        Assert.Equal(12, set!.VariantIds.Count);
        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal("1 / 12", editor.CurrentVariantLabel);
    }

    // ---------------------------------------------------------------- shortcuts

    [Fact]
    public void The_stamp_tool_has_its_own_default_binding_and_context_keys()
    {
        var shortcuts = new EditorShortcutSettings();

        Assert.Equal("O", shortcuts.Get("stamp"));
        Assert.Equal("Alt+Right", shortcuts.Get("stamp-next-variant"));
        Assert.Equal("Alt+Left", shortcuts.Get("stamp-previous-variant"));
        Assert.Equal("Alt+Wheel", shortcuts.Get("stamp-cycle-variant"));
        Assert.Equal("F", shortcuts.Get("stamp-focus-library"));
        Assert.Equal("Q", shortcuts.Get("stamp-area-up"));
        Assert.Equal("A", shortcuts.Get("stamp-area-down"));
        Assert.Equal("Alt+Q", shortcuts.Get("stamp-density-up"));
        Assert.Equal("Alt+A", shortcuts.Get("stamp-density-down"));
    }

    [Fact]
    public void The_stamp_tool_takes_part_in_the_previous_tool_toggle_like_every_other_tool()
    {
        var editor = Editor(out _);
        editor.ActiveTool = EditorTool.Brush;
        editor.ActiveTool = EditorTool.Stamp;

        editor.SelectPreviousTool();
        Assert.Equal(EditorTool.Brush, editor.ActiveTool);

        editor.SelectPreviousTool();
        Assert.Equal(EditorTool.Stamp, editor.ActiveTool);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<SceneObject> Stamps(EditorViewModel editor) =>
    [
        .. editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Where(item => item.Stamp is not null),
    ];

    private static string[] Snapshot(EditorViewModel editor) =>
    [
        .. Stamps(editor).Select(item => string.Join(
            '|',
            item.Id,
            item.Stamp!.AssetSha256,
            item.X.ToString("F6"),
            item.Y.ToString("F6"),
            item.Width.ToString("F6"),
            item.Height.ToString("F6"),
            item.RotationDegrees.ToString("F6"),
            item.Stamp.FlipHorizontal,
            item.Stamp.FlipVertical)),
    ];

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(CatalogRoot());
        return new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog);
    }

    private string CatalogRoot() => Path.Combine(_root, "catalog");

    private static DateTimeOffset Stamp(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static SKColor Shade(int index) => new((byte)(15 + (index * 19)), (byte)(240 - (index * 11)), 90, 255);

    private static AssetRecord Synthetic(int index) => new()
    {
        Sha256 = index.ToString("x64"),
        DisplayName = $"card-{index:D5}",
        OriginalFileName = $"card-{index:D5}.png",
        MediaType = "image/png",
        Extension = "png",
        Width = 512,
        Height = 512,
        HasAlpha = true,
        ByteLength = 2_048,
        ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
        Provenance = new AssetProvenance(),
        ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
    };

    private AssetSet ImportTwelve(AssetCatalog catalog) => catalog.ImportSet(
        [.. Enumerable.Range(0, 12).Select(index => Source($"v{index:D2}", Shade(index)))],
        "Диагностический набор",
        new AssetImportOptions { Pack = "Диагностика", Tags = ["diagnostic"] },
        Stamp(2));

    private string Source(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        if (!File.Exists(path))
        {
            using var surface = SKSurface.Create(new SKImageInfo(64, 48))!;
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, encoded.ToArray());
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
