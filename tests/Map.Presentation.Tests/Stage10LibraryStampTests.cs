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
public sealed class Stage10LibraryStampTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- library state

    [Fact]
    public void Gallery_preferences_survive_a_new_editor_and_sources_separate_user_imports()
    {
        EditorLocalPreferences? saved = null;
        var catalog = TrackCatalog(CatalogRoot());
        var editor = new EditorViewModel(TestData.Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
            assetCatalog: catalog, saveLocalPreferences: preferences => saved = preferences);
        catalog.Import(Source("свой дом", SKColors.Brown), new AssetImportOptions { Tags = ["Тема/Дома", "Стиль/Графика"] }, Stamp(1));
        editor.LibraryTileSize = 160;
        editor.LibraryShowNames = false;
        var reopened = new EditorViewModel(TestData.Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
            assetCatalog: catalog, localPreferences: saved);
        Assert.Equal(160, reopened.LibraryTileSize);
        Assert.False(reopened.LibraryShowNames);
        reopened.LibrarySource = AssetSourceFilter.Mine;
        Assert.Single(reopened.LibrarySets);
        reopened.SetLibraryTagSelected("Тема/Дома", true);
        reopened.SetLibraryTagSelected("Стиль/Графика", true);
        Assert.Single(reopened.LibrarySets);
        reopened.LibrarySource = AssetSourceFilter.BuiltIn;
        Assert.Empty(reopened.LibrarySets);
        reopened.ClearLibraryFilters();
        Assert.Single(reopened.LibrarySets);
        Assert.Empty(reopened.LibraryTags);
    }

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
        var reopened = TrackCatalog(CatalogRoot());
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
        Assert.Equal(["башня", "Мои горы"], editor.LibrarySets.Select(item => item.Name));

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

        // The reference carries the catalog thumbnail, so a map opened without the catalog still
        // has something to show for the asset.
        Assert.NotNull(Assert.Single(editor.Session.Document.Assets).FallbackThumbnailPng);
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

        editor.CancelPlacement();
        editor.SelectObject(before.Id);
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
            "Мои горы");

        Assert.NotNull(set);
        Assert.Equal(12, set!.VariantIds.Count);
        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal("1 / 12", editor.CurrentVariantLabel);
    }

    /// <summary>
    /// Owner decision: a scatter multiplies the chosen variant, it does not turn or mirror it.
    /// Both randomisations stay available as explicit toggles, but neither is on by default.
    /// </summary>
    [Fact]
    public void An_area_gesture_multiplies_the_variant_without_turning_or_mirroring_it()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.StampAreaRadius = 200;
        editor.StampDensity = 40;

        Assert.False(editor.StampRandomAngle);
        Assert.False(editor.StampRandomFlip);
        Assert.True(editor.PlaceCurrentVariantAt(700, 400));

        var placed = Stamps(editor);
        Assert.True(placed.Count > 100, $"Only {placed.Count} stamps were placed.");
        Assert.All(placed, item => Assert.Equal(0, item.RotationDegrees));
        Assert.All(placed, item => Assert.False(item.Stamp!.FlipHorizontal));
        Assert.All(placed, item => Assert.False(item.Stamp!.FlipVertical));

        // Turning them on is still one property away, so the ability is not lost.
        editor.StampRandomAngle = true;
        Assert.True(editor.PlaceCurrentVariantAt(700, 400));
        Assert.Contains(Stamps(editor), item => item.RotationDegrees != 0);
    }

    // ---------------------------------------------------------------- atomic gestures

    /// <summary>
    /// Two catalogs seeded from the same twelve files hold the same content hashes, so two fresh
    /// view models plan the same gesture. That is what makes the refusal tests below exact: the
    /// hash broken in the second catalog is provably one the second plan needs.
    /// </summary>
    [Fact]
    public void Two_fresh_sessions_with_the_same_history_plan_the_identical_gesture()
    {
        var first = AreaEditor("a", out _);
        var second = AreaEditor("b", out _);

        Assert.True(first.PlaceCurrentVariantAt(900, 500));
        Assert.True(second.PlaceCurrentVariantAt(900, 500));

        Assert.Equal(
            [.. Stamps(first).Select(Plan)],
            [.. Stamps(second).Select(Plan)]);
    }

    [Fact]
    public void One_missing_original_cancels_the_whole_area_gesture_and_changes_nothing()
    {
        var probe = AreaEditor("probe-missing", out _);
        Assert.True(probe.PlaceCurrentVariantAt(900, 500));
        var needed = Stamps(probe)[0].Stamp!.AssetSha256;
        Assert.Equal(12, Stamps(probe).Select(item => item.Stamp!.AssetSha256).Distinct().Count());

        var editor = AreaEditor("missing", out var catalog);
        File.Delete(Original(catalog, needed));
        var before = new Untouched(editor);

        Assert.False(editor.PlaceCurrentVariantAt(900, 500));

        before.AssertUnchanged(editor);
        Assert.Contains("отсутствует", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(catalog.Find(needed)!.DisplayName, editor.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void One_changed_original_cancels_the_whole_area_gesture_and_changes_nothing()
    {
        var probe = AreaEditor("probe-mismatch", out _);
        Assert.True(probe.PlaceCurrentVariantAt(900, 500));
        var needed = Stamps(probe)[0].Stamp!.AssetSha256;

        var editor = AreaEditor("mismatch", out var catalog);
        File.WriteAllBytes(Original(catalog, needed), File.ReadAllBytes(Source("intruder", SKColors.Fuchsia)));
        var before = new Untouched(editor);

        Assert.False(editor.PlaceCurrentVariantAt(900, 500));

        before.AssertUnchanged(editor);
        Assert.Contains("не совпадает", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(catalog.Find(needed)!.DisplayName, editor.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost of a gesture must follow the number of distinct variants it needs, not the number
    /// of stamps it places: a formula-planned five thousand stamps over twelve variants is twelve
    /// file resolutions, without relying on a product count ceiling.
    /// </summary>
    [Fact]
    public void Five_thousand_formula_planned_stamps_over_twelve_variants_resolve_twelve_originals()
    {
        var editor = AreaEditor("cost", out var catalog, out var counting);
        editor.StampAreaRadius = 400;
        editor.StampDensity = 5_000 * StampPlacement.DensityReferenceArea / (Math.PI * 400 * 400);
        Assert.Equal(5_000, editor.PlannedStampCount);

        Assert.True(editor.PlaceCurrentVariantAt(0, 0));

        Assert.Equal(5_000, Stamps(editor).Count);
        Assert.Equal(12, counting.Resolved.Count);
        Assert.Equal(12, counting.Resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(12, catalog.Sets.Single(set => set.VariantIds.Count == 12).VariantIds.Count);
    }

    [Fact]
    public void A_successful_one_placement_names_the_variant_it_placed_not_the_next_one()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.NextVariant();
        editor.StampPlacementMode = StampPlacementMode.One;
        editor.RandomVariantAfterPlacement = true;
        var placed = editor.CurrentVariantLabel;

        Assert.True(editor.PlaceCurrentVariantAt(300, 300));

        Assert.NotEqual(placed, editor.CurrentVariantLabel);
        Assert.Contains(placed, editor.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(editor.CurrentVariantLabel, editor.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variant_whose_format_is_not_supported_is_refused_at_catalog_admission_by_name()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(Source("gif-like", SKColors.Teal), new AssetImportOptions(), Stamp(0));

        // The catalog only imports PNG, JPEG and WebP, so an unsupported media type can only be
        // reached through an index that already claims one. It must still refuse cleanly.
        var document = new AssetLibraryDocument();
        document.Records.Add(record with { MediaType = "image/gif", DisplayName = "Чужой формат" });
        AssetLibraryStore.AddSingleton(document, document.Records[0]);
        var before = new Untouched(editor);

        var failure = Assert.Throws<ArgumentException>(() => catalog.ReplaceLibrary(document));

        before.AssertUnchanged(editor);
        Assert.Contains("формат", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Чужой формат", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_original_that_cannot_be_opened_refuses_the_gesture_instead_of_throwing()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(Source("locked", SKColors.Maroon), new AssetImportOptions(), Stamp(0));
        editor.RefreshLibrary();
        editor.SelectAssetSet(catalog.FindSetForContent(record.Sha256)!.Id);
        var before = new Untouched(editor);

        // Exclusive share: the file is there and unchanged, but nothing else may read it.
        using var exclusive = new FileStream(
            Original(catalog, record.Sha256), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(editor.PlaceCurrentVariantAt(100, 100));

        before.AssertUnchanged(editor);
        Assert.Contains("Ничего не размещено", editor.StatusDetail, StringComparison.Ordinal);

        // The reason has to be the concrete one, not a shrug: an unreadable original is reported
        // as an absent file rather than as "currently unavailable".
        Assert.Contains("отсутствует", editor.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The thumbnail travels with a placed asset as a convenience. Losing it must not stop the
    /// placement: the content itself is what the map needs.
    /// </summary>
    [Fact]
    public void A_missing_thumbnail_does_not_stop_a_placement()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(Source("no-thumb", SKColors.Navy), new AssetImportOptions(), Stamp(0));
        File.Delete(catalog.ThumbnailPath(record));
        editor.RefreshLibrary();
        editor.SelectAssetSet(catalog.FindSetForContent(record.Sha256)!.Id);

        Assert.True(editor.PlaceCurrentVariantAt(100, 100));

        var placed = Assert.Single(editor.Session.Document.Assets);
        Assert.Equal(record.Sha256, placed.Sha256);
        Assert.Null(placed.FallbackThumbnailPng);
    }

    [Fact]
    public void Repeating_an_embedded_asset_reuses_document_bytes_even_after_catalog_source_is_deleted()
    {
        var editor = AreaEditor("reuse-owned-payload", out var catalog, out var resolver);
        editor.StampPlacementMode = StampPlacementMode.One;
        editor.RandomVariantAfterPlacement = false;

        Assert.True(editor.PlaceCurrentVariantAt(100, 100));
        var owned = Assert.Single(editor.Session.Document.Assets);
        var record = catalog.Find(owned.Sha256)!;
        var resolveCount = resolver.Resolved.Count;
        File.Delete(Original(catalog, record.Sha256));

        Assert.True(editor.PlaceCurrentVariantAt(200, 200));

        Assert.Equal(resolveCount, resolver.Resolved.Count);
        Assert.Same(owned, Assert.Single(editor.Session.Document.Assets));
        Assert.Equal(2, Stamps(editor).Count);
    }

    [Fact]
    public void Area_plan_over_the_project_asset_budget_is_refused_before_resolving_any_original()
    {
        var catalog = TrackCatalog(Path.Combine(_root, "aggregate-preflight"));
        var document = new AssetLibraryDocument();
        var variantIds = new List<string>();
        // Nine distinct 60 MiB sources exceed the current 512 MiB project asset envelope.
        for (var index = 0; index < 9; index++)
        {
            var record = Synthetic(index) with
            {
                ByteLength = 60L * 1024 * 1024,
                DisplayName = $"heavy-{index}",
                OriginalFileName = $"heavy-{index}.png",
            };
            var variantId = $"var-heavy-{index}";
            document.Records.Add(record);
            document.Variants.Add(new AssetVariant
            {
                Id = variantId,
                Sha256 = record.Sha256,
                Label = record.DisplayName,
            });
            variantIds.Add(variantId);
        }

        document.Sets.Add(new AssetSet
        {
            Id = "set-heavy",
            Name = "Тяжёлый набор",
            VariantIds = variantIds,
            CoverVariantId = variantIds[0],
            ImportedUtc = DateTimeOffset.UnixEpoch,
        });
        catalog.ReplaceLibrary(document);
        var resolver = new CountingResolver(new CatalogAssetContentResolver(catalog));
        var editor = new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog,
            assetResolver: resolver);
        editor.SelectAssetSet("set-heavy");
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.RandomVariantAfterPlacement = true;
        editor.StampAreaRadius = 200;
        editor.StampDensity = 100;
        var before = new Untouched(editor);
        resolver.Resolved.Clear(); // Arming a preview is a separate earlier operation.

        Assert.False(editor.PlaceCurrentVariantAt(300, 300));

        before.AssertUnchanged(editor);
        Assert.Empty(resolver.Resolved);
        Assert.Contains("лимит", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_resolution_without_a_reference_is_never_treated_as_available()
    {
        Assert.False(AssetContentResolution.Failed(AssetAvailability.Missing).IsAvailable);
        Assert.False(AssetContentResolution.Failed(AssetAvailability.Available).IsAvailable);
        Assert.False(new AssetContentResolution(AssetAvailability.HashMismatch, null).IsAvailable);
    }

    [Fact]
    public void A_group_header_row_and_a_card_row_can_never_share_a_key()
    {
        var summary = new AssetSetSummary
        {
            SetId = "лес",
            Name = "Лес",
            VariantCount = 1,
            CoverVariantId = "var-1",
            CoverSha256 = "0".PadLeft(64, '0'),
            CoverThumbnailRelativePath = "thumbnails/a.png",
            Favorite = false,
            ImportedUtc = DateTimeOffset.UnixEpoch,
        };

        var header = AssetLibraryRow.Header("лес", "лес", 1);
        var card = AssetLibraryRow.ForCard("", summary);

        // The exact shape is the contract: a header is namespaced away from every card, and a card
        // repeated under two groups carries the group in its key so the two rows stay distinct.
        Assert.Equal("group:лес", header.Key);
        Assert.Equal("/лес", card.Key);
        Assert.Equal("лес/лес", AssetLibraryRow.ForCard("лес", summary).Key);
        Assert.NotEqual(header.Key, card.Key);
        Assert.True(header.IsHeader);
        Assert.False(card.IsHeader);
    }

    // ---------------------------------------------------------------- used in map

    [Fact]
    public void Used_in_the_current_map_finds_line_shape_fill_composition_and_filter_assets()
    {
        var catalog = TrackCatalog(CatalogRoot());
        var hashes = new List<string>();
        foreach (var name in new[] { "stamp", "line", "stroke", "fill", "nested", "filter", "unused" })
        {
            hashes.Add(catalog.Import(
                Source(name, Shade(hashes.Count * 7)), new AssetImportOptions(), Stamp(hashes.Count)).Sha256);
        }

        var editor = EditorFor(UsingAssets(hashes), catalog);
        Assert.Equal(7, editor.LibrarySets.Count);

        editor.LibraryUsedInMapOnly = true;

        var found = editor.LibrarySets
            .Select(card => catalog.FindVariant(card.CoverVariantId)!.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(6, found.Count);
        Assert.All(hashes.Take(6), sha => Assert.Contains(sha, found));
        Assert.DoesNotContain(hashes[6], found);
    }

    // ---------------------------------------------------------------- gesture seed

    /// <summary>
    /// Pins the seed at the place it is actually used. The golden below proves the mix itself is
    /// fixed; this proves the view model feeds that mix into the plan it commits, so swapping in
    /// a process-randomised hash would move every stamp.
    /// </summary>
    [Fact]
    public void The_gesture_the_view_model_commits_is_planned_from_the_stable_seed()
    {
        var editor = AreaEditor("seeded", out var catalog);
        var set = catalog.Sets.Single(item => item.VariantIds.Count == 12);
        Assert.Equal(0, editor.Session.Revision);

        Assert.True(editor.PlaceCurrentVariantAt(900, 500));

        var placed = Stamps(editor);
        var expected = StampPlacement.PlanArea(
            new StampPlacementRequest
            {
                CenterX = 900,
                CenterY = 500,
                AreaRadius = 200,
                Density = 40,
                Seed = GestureSeed.From(0, set.Id, 0, 1),
                RandomizeVariant = true,
                RandomAngle = true,
                RandomFlip = true,
                BaseWidth = placed[0].Width,
            },
            [
                .. set.VariantIds
                    .Select(id => catalog.Find(catalog.FindVariant(id)!.Sha256)!)
                    .Select(record => new StampVariantSource(record.Sha256, record.Width, record.Height)),
            ]);

        Assert.Equal(
            [.. expected.Select(item => string.Join('|', item.Sha256, item.X.ToString("F6"), item.Y.ToString("F6")))],
            [.. placed.Select(item => string.Join('|', item.Stamp!.AssetSha256, item.X.ToString("F6"), item.Y.ToString("F6")))]);
    }

    /// <summary>
    /// The seed has to be reproducible between processes: the framework string hash is randomised
    /// per run, so a golden value is the only thing that keeps a documented replay honest.
    /// </summary>
    [Fact]
    public void The_gesture_seed_is_a_stable_mix_rather_than_a_randomised_string_hash()
    {
        Assert.Equal(1_564_375_367, GestureSeed.From(7, "set-diagnostic", 3, 2));
        Assert.Equal(1_775_412_548, GestureSeed.From(0, null, 0, 1));

        // Every part of the tuple has to reach the result, or two gestures would replay alike.
        Assert.NotEqual(GestureSeed.From(7, "set-diagnostic", 3, 2), GestureSeed.From(8, "set-diagnostic", 3, 2));
        Assert.NotEqual(GestureSeed.From(7, "set-diagnostic", 3, 2), GestureSeed.From(7, "set-other", 3, 2));
        Assert.NotEqual(GestureSeed.From(7, "set-diagnostic", 3, 2), GestureSeed.From(7, "set-diagnostic", 4, 2));
        Assert.NotEqual(GestureSeed.From(7, "set-diagnostic", 3, 2), GestureSeed.From(7, "set-diagnostic", 3, 3));
    }

    // ---------------------------------------------------------------- grouped rows

    /// <summary>
    /// Grouping has to change the surface the user actually looks at, and it has to do so without
    /// building a second control tree: headers and cards are one flat, virtualizable sequence.
    /// </summary>
    [Fact]
    public void Grouping_by_collection_turns_the_result_into_header_and_card_rows()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        catalog.Import(Source("одиночка", SKColors.Sienna), new AssetImportOptions { Pack = "Другое" }, Stamp(5));
        editor.RefreshLibrary();

        Assert.All(editor.LibraryRows, row => Assert.False(row.IsHeader));
        Assert.Equal(2, editor.LibraryRows.Count);

        editor.LibraryGrouping = AssetGrouping.Collection;

        var headers = editor.LibraryRows.Where(row => row.IsHeader).ToList();
        Assert.Equal(2, headers.Count);
        Assert.Contains(headers, row => row.Title.Contains("Горы владельца", StringComparison.Ordinal));
        Assert.Contains(headers, row => row.Title.Contains("Другое", StringComparison.Ordinal));
        Assert.Equal(2, editor.LibraryRows.Count(row => !row.IsHeader));
        Assert.All(headers, row => Assert.Equal(1, row.Count));

        // Every header is immediately followed by the cards it covers.
        var first = editor.LibraryRows[0];
        Assert.True(first.IsHeader);
        Assert.False(editor.LibraryRows[1].IsHeader);
        Assert.Contains(editor.LibraryRows, row => row.Card?.SetId == set.Id);
    }

    [Fact]
    public void Grouping_by_tag_lists_a_card_under_each_of_its_tags_with_distinct_row_keys()
    {
        var editor = Editor(out var catalog);
        var set = catalog.ImportSet(
            [Source("двутег", SKColors.Teal)], "Двутеговый",
            new AssetImportOptions { Tags = ["лес", "камень"] }, Stamp(1));
        catalog.SetTags(set.Id, ["лес", "камень"]);
        editor.RefreshLibrary();

        editor.LibraryGrouping = AssetGrouping.Tag;

        var cards = editor.LibraryRows.Where(row => !row.IsHeader).ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, row => Assert.Equal(set.Id, row.Card!.SetId));
        Assert.Equal(2, cards.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Choosing_an_asset_places_it_one_at_a_time_after_an_area_was_used()
    {
        var catalog = TrackCatalog(CatalogRoot());
        var restored = new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog,
            localPreferences: new EditorLocalPreferences(Stamp: new StampToolSettings { Mode = StampPlacementMode.FillArea }));
        Assert.Equal(StampPlacementMode.One, restored.StampPlacementMode);

        var set = ImportTwelve(catalog);
        restored.RefreshLibrary();
        restored.StampPlacementMode = StampPlacementMode.FillArea;
        var card = restored.LibraryRows.ToList().FindIndex(row => !row.IsHeader);
        Assert.True(restored.SelectLibraryRow(card));
        Assert.Equal(set.Id, restored.SelectedSetId);
        Assert.Equal(StampPlacementMode.One, restored.StampPlacementMode);

        restored.StampPlacementMode = StampPlacementMode.Series;
        restored.UseSinglePlacementForNewAsset();
        Assert.Equal(StampPlacementMode.Series, restored.StampPlacementMode);
    }

    [Fact]
    public void A_header_row_can_never_become_the_selected_asset_set()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.LibraryGrouping = AssetGrouping.Collection;
        var header = editor.LibraryRows.ToList().FindIndex(row => row.IsHeader);
        var card = editor.LibraryRows.ToList().FindIndex(row => !row.IsHeader);

        Assert.True(editor.SelectLibraryRow(card));
        Assert.Equal(set.Id, editor.SelectedSetId);

        Assert.False(editor.SelectLibraryRow(header));
        Assert.Equal(set.Id, editor.SelectedSetId);
    }

    [Fact]
    public void A_card_that_survives_a_regroup_keeps_the_selection_and_its_row()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        catalog.Import(Source("сосед", SKColors.Olive), new AssetImportOptions { Pack = "Другое" }, Stamp(6));
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.NextVariant();

        editor.LibraryGrouping = AssetGrouping.Collection;

        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal("2 / 12", editor.CurrentVariantLabel);
        Assert.True(editor.SelectedLibraryRowIndex >= 0);
        Assert.Equal(set.Id, editor.LibraryRows[editor.SelectedLibraryRowIndex].Card!.SetId);
    }

    // ---------------------------------------------------------------- filters

    [Fact]
    public void The_tag_media_type_and_import_date_filters_all_narrow_the_cards()
    {
        var editor = Editor(out var catalog);
        // A one-file import is a singleton card, so it keeps the file's own name.
        var tagged = catalog.ImportSet(
            [Source("хвоя", SKColors.DarkGreen)], "Ель", new AssetImportOptions { Tags = ["лес"] }, Stamp(100));
        catalog.SetTags(tagged.Id, ["лес"]);
        catalog.Import(Source("камень", SKColors.Gray), new AssetImportOptions(), Stamp(500));
        editor.RefreshLibrary();
        Assert.Equal(2, editor.LibrarySets.Count);

        Assert.Contains("лес", editor.LibraryTagChoices);
        editor.LibraryTag = "лес";
        Assert.Equal(["хвоя"], editor.LibrarySets.Select(card => card.Name));

        editor.LibraryTag = null;
        Assert.Contains("image/png", editor.LibraryMediaTypeChoices);
        editor.LibraryMediaType = "image/jpeg";
        Assert.Empty(editor.LibrarySets);

        editor.LibraryMediaType = null;
        editor.LibraryImportedFromText = "1970-01-01";
        editor.LibraryImportedToText = "1970-01-01";
        Assert.False(editor.HasLibraryDateError);
        Assert.Equal(2, editor.LibrarySets.Count);

        editor.LibraryImportedFromText = "1971-01-01";
        Assert.Empty(editor.LibrarySets);

        editor.LibraryImportedFromText = "не дата";
        Assert.True(editor.HasLibraryDateError);
    }

    [Fact]
    public void Clearing_the_filters_restores_the_whole_library_at_once()
    {
        var editor = Editor(out var catalog);
        ImportTwelve(catalog);
        catalog.Import(Source("вторая", SKColors.Sienna), new AssetImportOptions(), Stamp(7));
        editor.RefreshLibrary();
        editor.LibrarySearchText = "горы";
        editor.LibraryFavoritesOnly = true;
        editor.LibraryUntaggedOnly = true;
        editor.LibraryTag = "горы";
        editor.LibraryMediaType = "image/webp";
        editor.LibraryImportedFromText = "1999-01-01";
        editor.LibraryCollectionId = AssetIdentity.CollectionId("Горы владельца");
        Assert.True(editor.HasActiveLibraryFilters);
        Assert.Empty(editor.LibrarySets);

        editor.ClearLibraryFilters();

        Assert.False(editor.HasActiveLibraryFilters);
        Assert.Equal(2, editor.LibrarySets.Count);
        Assert.Equal("", editor.LibrarySearchText);
        Assert.Null(editor.LibraryTag);
        Assert.Null(editor.LibraryMediaType);
        Assert.Null(editor.LibraryCollectionId);
        Assert.Equal("", editor.LibraryImportedFromText);
        Assert.False(editor.LibraryFavoritesOnly);
        Assert.False(editor.LibraryUntaggedOnly);
    }

    [Fact]
    public void The_active_filter_summary_names_what_is_switched_on()
    {
        var editor = Editor(out var catalog);
        ImportTwelve(catalog);
        editor.RefreshLibrary();

        Assert.False(editor.HasActiveLibraryFilters);
        Assert.Equal("Фильтры не заданы", editor.ActiveLibraryFilterSummary);

        editor.LibraryFavoritesOnly = true;
        editor.LibraryTag = "горы";

        Assert.True(editor.HasActiveLibraryFilters);
        Assert.Contains("избранное", editor.ActiveLibraryFilterSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("горы", editor.ActiveLibraryFilterSummary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- set management

    [Fact]
    public void Renaming_the_selected_set_refuses_blank_names_and_survives_a_restart()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);

        Assert.False(editor.RenameSelectedSet("   "));
        Assert.False(editor.RenameSelectedSet(""));
        Assert.Equal("Мои горы", editor.SelectedSet!.Name);

        Assert.True(editor.RenameSelectedSet("  Свой набор  "));

        Assert.Equal("Свой набор", editor.SelectedSet!.Name);
        Assert.Equal(set.Id, editor.SelectedSetId);
        Assert.Equal("Свой набор", TrackCatalog(CatalogRoot()).FindSet(set.Id)!.Name);
    }

    [Fact]
    public void Moving_the_current_variant_later_reorders_the_set_and_keeps_that_variant_current()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.NextVariant();
        var moving = editor.CurrentVariant!.Id;
        Assert.Equal(1, editor.CurrentVariantIndex);

        Assert.True(editor.MoveCurrentVariant(1));

        Assert.Equal(2, editor.CurrentVariantIndex);
        Assert.Equal(moving, editor.CurrentVariant!.Id);
        Assert.Equal(moving, TrackCatalog(CatalogRoot()).FindSet(set.Id)!.VariantIds[2]);

        Assert.True(editor.MoveCurrentVariant(-1));
        Assert.Equal(1, editor.CurrentVariantIndex);
        Assert.Equal(moving, TrackCatalog(CatalogRoot()).FindSet(set.Id)!.VariantIds[1]);
    }

    [Fact]
    public void The_first_variant_cannot_move_earlier_and_the_last_cannot_move_later()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);

        Assert.False(editor.CanMoveCurrentVariant(-1));
        Assert.False(editor.MoveCurrentVariant(-1));
        Assert.Equal(0, editor.CurrentVariantIndex);

        editor.SelectVariantAt(11);
        Assert.False(editor.CanMoveCurrentVariant(1));
        Assert.True(editor.CanMoveCurrentVariant(-1));
        Assert.Equal(set.VariantIds, [.. TrackCatalog(CatalogRoot()).FindSet(set.Id)!.VariantIds]);
    }

    [Fact]
    public void Making_the_current_variant_the_cover_survives_a_restart()
    {
        var editor = Editor(out var catalog);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.NextVariant();
        editor.NextVariant();
        var wanted = editor.CurrentVariant!.Id;
        Assert.False(editor.IsCurrentVariantTheCover);
        Assert.True(editor.CanMakeCurrentVariantCover);

        Assert.True(editor.MakeCurrentVariantCover());

        Assert.True(editor.IsCurrentVariantTheCover);
        Assert.False(editor.CanMakeCurrentVariantCover);
        Assert.Equal(wanted, editor.SelectedSet!.CoverVariantId);
        Assert.Equal(wanted, TrackCatalog(CatalogRoot()).FindSet(set.Id)!.CoverVariantId);

        // The cover moved, but the content and the order behind it did not.
        Assert.Equal(12, editor.SelectedSetVariants.Count);
        Assert.Equal(2, editor.CurrentVariantIndex);
    }

    [Fact]
    public void A_singleton_card_offers_no_reorder_and_no_cover_choice()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(Source("один", SKColors.Teal), new AssetImportOptions(), Stamp(0));
        editor.RefreshLibrary();
        editor.SelectAssetSet(catalog.FindSetForContent(record.Sha256)!.Id);

        Assert.True(editor.CanRenameSelectedSet);
        Assert.False(editor.CanMoveCurrentVariant(1));
        Assert.False(editor.CanMoveCurrentVariant(-1));
        Assert.False(editor.CanMakeCurrentVariantCover);
        Assert.False(editor.MoveCurrentVariant(1));
        Assert.False(editor.MakeCurrentVariantCover());
    }

    [Fact]
    public void Set_management_is_refused_outright_while_no_card_is_selected()
    {
        var editor = Editor(out _);

        Assert.False(editor.CanRenameSelectedSet);
        Assert.False(editor.RenameSelectedSet("Имя"));
        Assert.False(editor.MoveCurrentVariant(1));
        Assert.False(editor.MakeCurrentVariantCover());
    }

    // ---------------------------------------------------------------- visible rows

    /// <summary>
    /// The view reports the containers it really realised. The cost of that report must follow
    /// that handful of rows, not the ten thousand the result holds, or every layout pass would
    /// walk the whole library.
    /// </summary>
    [Fact]
    public void Reporting_the_realised_rows_costs_the_realised_rows_not_the_whole_result()
    {
        var editor = Editor(out var catalog);
        var document = new AssetLibraryDocument();
        for (var index = 0; index < 10_000; index++)
        {
            var record = Synthetic(index);
            document.Records.Add(record);
            AssetLibraryStore.AddSingleton(document, record);
        }

        catalog.ReplaceLibrary(document);
        editor.RefreshLibrary();
        Assert.Equal(10_000, editor.LibraryRows.Count);

        var counted = new CountingSequence(Enumerable.Range(4_000, 40));
        editor.ReportRealizedLibraryRows(counted);

        Assert.Equal(40, counted.Steps);
        Assert.Equal(4_000, editor.VisibleLibraryTileWindow.FirstIndex);
        Assert.Equal(40, editor.VisibleLibraryTiles.Count);
    }

    [Fact]
    public void Reporting_no_realised_rows_empties_the_visible_window()
    {
        var editor = Editor(out var catalog);
        ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.ReportRealizedLibraryRows(new CountingSequence([0]));
        Assert.Single(editor.VisibleLibraryTiles);

        editor.ReportRealizedLibraryRows(new CountingSequence([]));

        Assert.Empty(editor.VisibleLibraryTiles);
    }

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task Availability_reports_missing_and_changed_content_apart_from_a_healthy_card()
    {
        var editor = Editor(out var catalog);
        var healthy = catalog.Import(Source("цел", SKColors.Green), new AssetImportOptions(), Stamp(0));
        var gone = catalog.Import(Source("пропал", SKColors.Red), new AssetImportOptions(), Stamp(1));
        var changed = catalog.Import(Source("подменён", SKColors.Blue), new AssetImportOptions(), Stamp(2));
        File.Delete(Original(catalog, gone.Sha256));
        File.WriteAllBytes(Original(catalog, changed.Sha256), File.ReadAllBytes(Source("чужой", SKColors.Coral)));
        editor.RefreshLibrary();

        Assert.Equal(AssetAvailability.Unknown, editor.AvailabilityOf(healthy.Sha256));

        editor.ReportRealizedLibraryRows(Enumerable.Range(0, editor.LibraryRows.Count));
        await editor.RefreshVisibleAvailabilityAsync();

        Assert.Equal(AssetAvailability.Available, editor.AvailabilityOf(healthy.Sha256));
        Assert.Equal(AssetAvailability.Missing, editor.AvailabilityOf(gone.Sha256));
        Assert.Equal(AssetAvailability.HashMismatch, editor.AvailabilityOf(changed.Sha256));
        Assert.Contains("отсутствует", EditorViewModel.AvailabilityText(AssetAvailability.Missing),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не совпадает", EditorViewModel.AvailabilityText(AssetAvailability.HashMismatch),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ten thousand cards must not become ten thousand hashed files: only what the grid realised
    /// and the set the tool holds are ever verified.
    /// </summary>
    [Fact]
    public async Task Availability_is_checked_only_for_the_rows_in_view_and_the_selected_set()
    {
        var editor = AreaEditor("bounded", out var catalog, out var counting);
        editor.RefreshLibrary();
        for (var index = 0; index < 40; index++)
        {
            catalog.Import(Source($"extra-{index}", Shade(index + 30)), new AssetImportOptions(), Stamp(index));
        }

        editor.RefreshLibrary();
        Assert.Equal(41, editor.LibraryRows.Count);
        counting.Resolved.Clear();
        counting.Checked.Clear();

        editor.ReportRealizedLibraryRows(Enumerable.Range(0, 6));
        await editor.RefreshVisibleAvailabilityAsync();

        // Six visible covers plus the twelve variants of the card the stamp tool holds.
        Assert.True(counting.Checked.Count <= 18, $"Checked {counting.Checked.Count} originals.");
        Assert.Empty(counting.Resolved);
        Assert.All(editor.VisibleLibraryTiles, tile =>
            Assert.NotEqual(AssetAvailability.Unknown, editor.AvailabilityOf(tile.CoverSha256)));
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
        Assert.Equal("C", shortcuts.Get("stamp-area-up"));
        Assert.Equal("Z", shortcuts.Get("stamp-area-down"));
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

    /// <summary>
    /// A view model over its own catalog, already holding the twelve-variant card in Area mode
    /// with random variants on. Two of these differ only in which directory they wrote to.
    /// </summary>
    private EditorViewModel AreaEditor(string slot, out AssetCatalog catalog) =>
        AreaEditor(slot, out catalog, out _);

    private EditorViewModel AreaEditor(string slot, out AssetCatalog catalog, out CountingResolver resolver)
    {
        catalog = TrackCatalog(Path.Combine(_root, slot));
        resolver = new CountingResolver(new CatalogAssetContentResolver(catalog));
        var editor = new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog,
            assetResolver: resolver);
        var set = ImportTwelve(catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(set.Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.RandomVariantAfterPlacement = true;

        // Explicit rather than inherited: these tests exercise the randomised plan on purpose,
        // and must keep doing so whatever the product defaults are.
        editor.StampRandomAngle = true;
        editor.StampRandomFlip = true;
        editor.StampAreaRadius = 200;
        editor.StampDensity = 40;
        return editor;
    }

    private static EditorViewModel EditorFor(MapDocument document, AssetCatalog catalog)
    {
        var editor = new EditorViewModel(
            document, documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog);
        editor.RefreshLibrary();
        return editor;
    }

    private static string Original(AssetCatalog catalog, string sha256) =>
        Path.Combine(catalog.Root, "originals", sha256 + ".png");

    /// <summary>The part of a placed stamp that a replay must reproduce, minus its fresh id.</summary>
    private static string Plan(SceneObject item) => string.Join(
        '|',
        item.Stamp!.AssetSha256,
        item.X.ToString("F6"),
        item.Y.ToString("F6"),
        item.Width.ToString("F6"),
        item.Height.ToString("F6"),
        item.RotationDegrees.ToString("F6"),
        item.Stamp.FlipHorizontal,
        item.Stamp.FlipVertical);

    /// <summary>
    /// Everything a refused gesture must leave exactly as it found it. A partial placement would
    /// show up here as a changed object count, a new asset, a deeper history or a moved variant.
    /// </summary>
    private sealed class Untouched(EditorViewModel editor)
    {
        private readonly int _objects = Stamps(editor).Count;
        private readonly int _assets = editor.Session.Document.Assets.Count;
        private readonly int _undo = editor.Session.UndoDepth;
        private readonly int _redo = editor.Session.RedoDepth;
        private readonly ObjectId? _selected = editor.Session.SelectedObjectId;
        private readonly int _variant = editor.CurrentVariantIndex;
        private readonly int _recent = editor.RecentCount;
        private readonly bool _dirty = editor.Session.IsDirty;
        private readonly SaveState _saveState = editor.SaveState;

        public void AssertUnchanged(EditorViewModel editor)
        {
            Assert.Equal(_objects, Stamps(editor).Count);
            Assert.Equal(_assets, editor.Session.Document.Assets.Count);
            Assert.Equal(_undo, editor.Session.UndoDepth);
            Assert.Equal(_redo, editor.Session.RedoDepth);
            Assert.Equal(_selected, editor.Session.SelectedObjectId);
            Assert.Equal(_variant, editor.CurrentVariantIndex);
            Assert.Equal(_recent, editor.RecentCount);
            Assert.Equal(_dirty, editor.Session.IsDirty);
            Assert.Equal(_saveState, editor.SaveState);
        }
    }

    /// <summary>Counts how many originals a gesture really opened, in order.</summary>
    private sealed class CountingResolver(IAssetContentResolver inner) : IAssetContentResolver
    {
        public List<string> Resolved { get; } = [];

        public List<string> Checked { get; } = [];

        public AssetContentResolution Resolve(AssetRecord record)
        {
            Resolved.Add(record.Sha256);
            return inner.Resolve(record);
        }

        public AssetAvailability Check(AssetRecord record)
        {
            Checked.Add(record.Sha256);
            return inner.Check(record);
        }
    }

    /// <summary>
    /// Reports how far a consumer walked. A visible-range report that enumerated the whole result
    /// instead of the realised containers would show up here as ten thousand steps.
    /// </summary>
    private sealed class CountingSequence(IEnumerable<int> source) : IEnumerable<int>
    {
        public int Steps { get; private set; }

        public IEnumerator<int> GetEnumerator()
        {
            foreach (var value in source)
            {
                Steps++;
                yield return value;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A document that reaches an asset through every route the model allows: a plain stamp, a
    /// line stroke, a shape stroke, a shape texture fill, a nested composition item and a filter.
    /// </summary>
    private static MapDocument UsingAssets(List<string> hashes)
    {
        // Node ids are unique across the whole document, so the line and the shape get their own.
        static VectorNode[] Nodes(int count) =>
            [.. Enumerable.Range(0, count).Select(index => new VectorNode(VectorNodeId.New(), index, index))];

        var stamp = Shell(SceneObjectKind.Stamp) with { Stamp = new StampStyle(hashes[0]) };
        var line = Shell(SceneObjectKind.Line) with
        {
            Line = new LineStyle(Nodes(2), new VectorStrokeStyle { AssetSha256 = hashes[1] }),
        };
        var shape = Shell(SceneObjectKind.Shape) with
        {
            Shape = new ShapeStyle(
                [new ShapeRegion(ShapeRegionId.New(), ShapeCombineMode.Add, ShapePrimitiveKind.Polygon, Nodes(3))],
                new ShapeFillStyle { TextureAssetSha256 = hashes[3] },
                new VectorStrokeStyle { AssetSha256 = hashes[2] }),
        };
        var nested = Shell(SceneObjectKind.Composition) with
        {
            Composition = new EditableComposition(
                CompositionId.New(), DocumentId.New(), "вложенная", 10, 10,
                [Shell(SceneObjectKind.Stamp) with { Stamp = new StampStyle(hashes[4]) }]),
        };

        return new MapDocument(
            DocumentId.New(), "used", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, [stamp, line, shape, nested]),
                new FilterLayer(LayerId.New(), "Фильтр", true, false, 1.0,
                    new FilterSettings { TextureAssetSha256 = hashes[5] }),
            ]);
    }

    private static SceneObject Shell(SceneObjectKind kind) => new(
        ObjectId.New(), kind, kind.ToString(), 0, 0, 10, 10, 0, "#FFFFFFFF", "#00000000", 1, true, false);

    // ---------------------------------------------------------------- Stage 10D additions

    [Fact]
    public void A_placed_stamp_carries_its_reference_size_for_an_honest_100_percent()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.StampPlacementMode = StampPlacementMode.One;

        Assert.True(editor.PlaceCurrentVariantAt(700, 400));

        var stamp = Assert.Single(
            editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects));
        Assert.Equal(stamp.Width, stamp.Stamp!.ReferenceWidth);
        Assert.Equal(stamp.Height, stamp.Stamp!.ReferenceHeight);
    }

    [Fact]
    public void Replacing_the_asset_keeps_transform_effects_and_group_identity()
    {
        var editor = Editor(out var catalog);
        editor.RefreshLibrary();
        editor.SelectAssetSet(ImportTwelve(catalog).Id);
        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.True(editor.PlaceCurrentVariantAt(300, 300));
        Assert.True(editor.PlaceCurrentVariantAt(800, 300));

        var placed = editor.Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(l => l.Objects).ToList();
        editor.Session.SetSelection([placed[0].Id, placed[1].Id]);
        Assert.True(editor.GroupSelection("Замена"));

        editor.CancelPlacement();
        editor.ActiveTool = EditorTool.Select;
        editor.Session.SetSelection([placed[0].Id]);
        editor.SetStampHueDegrees(25);
        editor.SetStampFlip(vertical: true);
        editor.SetSelectedOpacityPercent(60);
        var styled = DocumentEditor.FindObject(editor.Session.Document, placed[0].Id)!;

        var replacement = catalog.Import(
            Source("replacement", SKColors.Crimson), new AssetImportOptions(), Stamp(9));
        editor.ReplaceSelectedStampAsset(replacement);

        var replaced = DocumentEditor.FindObject(editor.Session.Document, placed[0].Id)!;
        Assert.Equal(replacement.Sha256, replaced.Stamp!.AssetSha256);
        Assert.Equal(styled.X, replaced.X);
        Assert.Equal(styled.Width, replaced.Width);
        Assert.Equal(styled.Height, replaced.Height);
        Assert.Equal(styled.RotationDegrees, replaced.RotationDegrees);
        Assert.Equal(25, replaced.Stamp!.Hue);
        Assert.True(replaced.Stamp!.FlipVertical);
        Assert.Equal(0.6, replaced.ObjectOpacity, precision: 12);
        Assert.Equal(styled.GroupId, replaced.GroupId);
        Assert.Equal("Замена", replaced.GroupName);
        Assert.Equal(styled.Stamp!.ReferenceWidth, replaced.Stamp!.ReferenceWidth);
    }

    [Fact]
    public void A_library_asset_becomes_one_undoable_shape_fill_with_its_reference()
    {
        var editor = Editor(out var catalog);
        editor.AddShape(ShapePrimitiveKind.Rectangle);
        var shape = Assert.Single(
            editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects));
        editor.SelectObject(shape.Id);
        var record = catalog.Import(
            Source("shape-fill", SKColors.Goldenrod), new AssetImportOptions(), Stamp(10));
        var depthBefore = editor.Session.UndoDepth;

        editor.UseAssetAsSelectedShapeFill(record);

        Assert.Equal(depthBefore + 1, editor.Session.UndoDepth);
        Assert.Equal(record.Sha256,
            DocumentEditor.FindObject(editor.Session.Document, shape.Id)!.Shape!.Fill.TextureAssetSha256);
        Assert.Contains(editor.Session.Document.Assets, asset => asset.Sha256 == record.Sha256);

        Assert.True(editor.Session.Undo());
        Assert.Null(DocumentEditor.FindObject(editor.Session.Document, shape.Id)!.Shape!.Fill.TextureAssetSha256);
        Assert.DoesNotContain(editor.Session.Document.Assets, asset => asset.Sha256 == record.Sha256);
        Assert.True(editor.Session.Redo());
        Assert.Equal(record.Sha256,
            DocumentEditor.FindObject(editor.Session.Document, shape.Id)!.Shape!.Fill.TextureAssetSha256);
    }

    [Fact]
    public void A_library_asset_can_become_the_named_brush_tip_without_document_history()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(
            Source("brush-tip", SKColors.White), new AssetImportOptions(), Stamp(11));
        var depthBefore = editor.Session.UndoDepth;

        Assert.True(editor.UseAssetAsBrushTip(record));

        Assert.Equal(depthBefore, editor.Session.UndoDepth);
        Assert.Equal(record.DisplayName, editor.BrushTipName);
        Assert.Equal(record.Width, editor.BrushSettings.Tip!.Width);
        Assert.Equal(record.Height, editor.BrushSettings.Tip.Height);
    }

    [Fact]
    public void Damaged_optional_thumbnail_does_not_block_a_verified_original()
    {
        _ = Editor(out var catalog);
        var record = catalog.Import(
            Source("valid-original", SKColors.ForestGreen), new AssetImportOptions(), Stamp(12));
        File.WriteAllBytes(catalog.ThumbnailPath(record), []);

        var resolution = new CatalogAssetContentResolver(catalog).Resolve(record);

        Assert.True(resolution.IsAvailable);
        Assert.NotNull(resolution.Reference!.EmbeddedContent);
        Assert.Null(resolution.Reference.FallbackThumbnailPng);
    }

    [Fact]
    public void Legacy_single_stamp_placement_also_ignores_a_damaged_optional_thumbnail()
    {
        var editor = Editor(out var catalog);
        var record = catalog.Import(
            Source("legacy-valid-original", SKColors.DarkSeaGreen),
            new AssetImportOptions(), Stamp(13));
        File.WriteAllBytes(catalog.ThumbnailPath(record), [1, 2, 3, 4]);

        editor.PlaceStamp(record);

        var placed = Assert.Single(editor.Session.Document.Assets);
        Assert.Equal(record.Sha256, placed.Sha256);
        Assert.NotNull(placed.EmbeddedContent);
        Assert.Null(placed.FallbackThumbnailPng);
    }

    [Theory]
    [InlineData("../../outside.png")]
    [InlineData("thumbnails/bad\0name.png")]
    public void A_malformed_optional_thumbnail_path_does_not_block_a_verified_original(
        string thumbnailRelativePath)
    {
        var editor = Editor(out var catalog);
        var imported = catalog.Import(
            Source("valid-original-unsafe-thumb", SKColors.MediumSeaGreen),
            new AssetImportOptions(), Stamp(14));
        var malformed = imported with { ThumbnailRelativePath = thumbnailRelativePath };

        var resolution = new CatalogAssetContentResolver(catalog).Resolve(malformed);
        editor.PlaceStamp(malformed);

        Assert.True(resolution.IsAvailable);
        Assert.NotNull(resolution.Reference!.EmbeddedContent);
        Assert.Null(resolution.Reference.FallbackThumbnailPng);
        var placed = Assert.Single(editor.Session.Document.Assets);
        Assert.Equal(imported.Sha256, placed.Sha256);
        Assert.NotNull(placed.EmbeddedContent);
        Assert.Null(placed.FallbackThumbnailPng);
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = TrackCatalog(CatalogRoot());
        return new EditorViewModel(
            TestData.Document(), documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => Stamp(0), assetCatalog: catalog);
    }

    private readonly List<AssetCatalog> _catalogs = [];
    private AssetCatalog TrackCatalog(string root)
    {
        var catalog = new AssetCatalog(root);
        _catalogs.Add(catalog);
        return catalog;
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
        "Мои горы",
        new AssetImportOptions { Pack = "Горы владельца", Tags = ["горы"] },
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var catalog in _catalogs) await catalog.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
