using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The follow-up the owner asked for after the first object/layer pass: one way into placing an
/// asset instead of two, Земля directly under Ассеты, layer and level at the top of a selected
/// object as an explicit list and a whole-number slider, and a mixed selection that says so
/// instead of inventing a shared number.
/// </summary>
public sealed class ObjectSurfaceAndLevelSliderTests
{
    private static EditorViewModel NewEditor(MapDocument? document = null) => new(
        document ?? TestData.Document(), documentPath: null, startDirty: false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
        clock: () => new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

    private static EditorViewModel WithObjects(int count, out List<ObjectId> ids)
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        ids = [];
        for (var index = 0; index < count; index++)
        {
            editor.AddObject(SceneObjectKind.Rectangle);
            ids.Add(editor.Session.SelectedObject!.Id);
        }

        return editor;
    }

    private static void SelectAll(EditorViewModel editor, IReadOnlyList<ObjectId> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            editor.SelectObject(ids[index], additive: index > 0);
        }
    }

    // ------------------------------------------------------------------- the rail

    [Fact]
    public void Every_section_offers_selection_and_every_other_tool_lives_in_exactly_one_section()
    {
        // DEC-070: «Рисовать · Объекты · Мир · Оформление», as in the chosen layout: assets in Объекты, notes in Мир,
        // the drawing tools in Рисовать, only selection in Оформление (the ribbon is its tool).
        var sections = Enum.GetValues<EditorWorkspace>();
        foreach (var section in sections)
            Assert.Contains(EditorSurfaceLayout.Rail, entry => entry.Tool == EditorTool.Select && EditorSurfaceLayout.InWorkspace(entry, section));
        foreach (var entry in EditorSurfaceLayout.Rail.Concat(EditorSurfaceLayout.HexRail).Where(entry => entry.Tool != EditorTool.Select))
            Assert.Single(sections, section => EditorSurfaceLayout.InWorkspace(entry, section));
        Assert.Equal(EditorWorkspace.Objects, EditorSurfaceLayout.WorkspaceOf(EditorTool.Stamp));
        Assert.Equal(EditorWorkspace.World, EditorSurfaceLayout.WorkspaceOf(EditorTool.Notes));
        Assert.Equal(EditorWorkspace.Draw, EditorSurfaceLayout.WorkspaceOf(EditorTool.Land));
        Assert.Equal(EditorWorkspace.Draw, EditorSurfaceLayout.WorkspaceOf(EditorTool.Hex));
        Assert.DoesNotContain(EditorSurfaceLayout.Rail, entry => entry.Tool != EditorTool.Select
            && EditorSurfaceLayout.InWorkspace(entry, EditorWorkspace.Appearance));
    }

    [Fact]
    public void The_rail_has_no_separate_stamp_button_and_assets_is_the_only_way_into_placement()
    {
        // "Штамп" used to be a main tool of its own next to the library, so placing a tree had two
        // entries and only one of them worked on its own.
        Assert.DoesNotContain(EditorSurfaceLayout.Rail,
            entry => entry.AutomationId.Contains("Stamp", StringComparison.OrdinalIgnoreCase));

        var stampEntries = EditorSurfaceLayout.Rail.Where(entry => entry.Tool == EditorTool.Stamp).ToList();
        var assets = Assert.Single(stampEntries);
        Assert.Equal("AssetsLibraryButton", assets.AutomationId);
        Assert.Equal(EditorRailEntryKind.Library, assets.Kind);

        // And exactly one entry in the whole rail opens the library.
        Assert.Single(EditorSurfaceLayout.Rail, entry => entry.Kind == EditorRailEntryKind.Library);
    }

    [Fact]
    public void Land_sits_directly_under_assets_in_the_same_group()
    {
        var group = Assert.Single(
            EditorSurfaceLayout.RailGroups,
            item => item.Any(entry => entry.AutomationId == "AssetsLibraryButton"));

        Assert.Equal(
            ["AssetsLibraryButton", "LandToolButton"],
            group.Select(entry => entry.AutomationId).Take(2));

        var flattened = EditorSurfaceLayout.Rail.Select(entry => entry.AutomationId).ToList();
        Assert.Equal(
            flattened.IndexOf("AssetsLibraryButton") + 1,
            flattened.IndexOf("LandToolButton"));

        // Земля stays a tool of its own: it paints land, it is not an asset that gets placed.
        Assert.Equal(EditorRailEntryKind.Tool, EditorSurfaceLayout.EntryFor(EditorTool.Land)!.Kind);
    }

    [Fact]
    public void Every_rail_entry_is_named_and_appears_once()
    {
        var ids = EditorSurfaceLayout.Rail.Select(entry => entry.AutomationId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.EndsWith("Button", id, StringComparison.Ordinal));

        var tools = EditorSurfaceLayout.Rail.Select(entry => entry.Tool).ToList();
        Assert.Equal(tools.Count, tools.Distinct().Count());
    }

    [Fact]
    public void Choosing_an_asset_arms_placement_without_a_second_click_on_another_tool()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        Assert.False(editor.HasChosenAsset);

        var set = editor.LibrarySets.FirstOrDefault();
        if (set is null)
        {
            // No sample library in this environment: the arming path is covered by the native
            // smoke instead, and nothing here may pretend otherwise.
            return;
        }

        editor.SelectAssetSet(set.SetId);

        Assert.True(editor.HasChosenAsset);
        Assert.Equal(EditorTool.Stamp, editor.ActiveTool);
    }

    // ------------------------------------------- the first block of a selected object

    [Fact]
    public void The_object_block_opens_with_layer_and_level_before_the_frequent_properties()
    {
        var order = EditorSurfaceLayout.ObjectPrimaryOrder.ToList();

        Assert.Equal(
            [
                ObjectPrimaryProperty.Identity,
                ObjectPrimaryProperty.Layer,
                ObjectPrimaryProperty.Level,
                ObjectPrimaryProperty.Scale,
                ObjectPrimaryProperty.Opacity,
                ObjectPrimaryProperty.Rotation,
                ObjectPrimaryProperty.Ordering,
            ],
            order);

        Assert.True(order.IndexOf(ObjectPrimaryProperty.Layer) < order.IndexOf(ObjectPrimaryProperty.Scale));
        Assert.True(order.IndexOf(ObjectPrimaryProperty.Level) < order.IndexOf(ObjectPrimaryProperty.Opacity));
        Assert.True(order.IndexOf(ObjectPrimaryProperty.Level) < order.IndexOf(ObjectPrimaryProperty.Rotation));
    }

    [Fact]
    public void Only_an_asset_object_gets_the_bitmap_only_block()
    {
        Assert.Equal(ObjectKindSection.Asset, EditorSurfaceLayout.SectionFor(SceneObjectKind.Stamp));
        Assert.Equal(ObjectKindSection.Asset, EditorSurfaceLayout.SectionFor(SceneObjectKind.Placeholder));

        foreach (var kind in new[]
        {
            SceneObjectKind.Shape, SceneObjectKind.Line, SceneObjectKind.Text,
            SceneObjectKind.Rectangle, SceneObjectKind.Ellipse, SceneObjectKind.Composition,
        })
        {
            Assert.NotEqual(ObjectKindSection.Asset, EditorSurfaceLayout.SectionFor(kind));
        }

        // Exactly one block per kind: a shape can never end up carrying the asset settings too.
        Assert.Equal(ObjectKindSection.Shape, EditorSurfaceLayout.SectionFor(SceneObjectKind.Shape));
        Assert.Equal(ObjectKindSection.Text, EditorSurfaceLayout.SectionFor(SceneObjectKind.Text));
    }

    [Fact]
    public void The_shared_properties_work_on_a_shape_and_the_bitmap_percentage_does_not()
    {
        var editor = WithObjects(1, out var ids);
        editor.SelectObject(ids[0], additive: false);

        Assert.True(editor.SetSelectionSublayer(4));
        Assert.Equal(4, editor.Session.SelectedObject!.Sublayer);
        Assert.True(editor.SetSelectionOpacityPercent(60, commit: true));
        Assert.True(editor.SetSelectionRotationDegrees(30, commit: true));
        Assert.NotEmpty(editor.ObjectLayerTargets);

        // Scale % is the one property that means nothing without a bitmap's own reference size.
        Assert.False(editor.SelectionIsAllStamps);
        Assert.Null(editor.SelectionScalePercent);
    }

    [Fact]
    public void A_selection_spread_over_layers_is_named_rather_than_left_blank()
    {
        var editor = WithObjects(2, out var ids);
        editor.AddObjectLayer();
        var layers = editor.Session.Document.Layers.OfType<ObjectLayer>().ToList();
        Assert.Equal(2, layers.Count);

        editor.SelectObject(ids[0], additive: false);
        var elsewhere = editor.ObjectLayerTargets.First(target => !target.IsCurrent);
        Assert.True(editor.MoveSelectionToLayer(elsewhere.Id));

        SelectAll(editor, ids);
        Assert.Equal(2, editor.SelectionCount);

        // Two objects, two layers: no single target may claim to be the current one, which is what
        // the view turns into the words "Разные слои" instead of an empty-looking field.
        Assert.DoesNotContain(editor.ObjectLayerTargets, target => target.IsCurrent);
        Assert.NotEmpty(editor.ObjectLayerTargets);
    }

    // ------------------------------------------------------------ the layer selector

    [Fact]
    public void The_layer_selector_offers_named_layers_and_never_shows_an_internal_key()
    {
        var editor = WithObjects(1, out var ids);
        editor.SelectObject(ids[0], additive: false);

        var targets = editor.ObjectLayerTargets;
        Assert.NotEmpty(targets);
        Assert.Single(targets, target => target.IsCurrent);

        foreach (var target in targets)
        {
            Assert.False(string.IsNullOrWhiteSpace(target.Name));
            Assert.DoesNotContain(target.Id.Value.ToString("D"), target.Display, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(target.Id.Value.ToString("N"), target.Display, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            targets.Single(target => target.IsCurrent).Name,
            editor.SelectedOwnerLayerName);
    }

    [Fact]
    public void An_unavailable_layer_stays_listed_with_its_reason_instead_of_disappearing()
    {
        var editor = WithObjects(1, out var ids);
        editor.SelectObject(ids[0], additive: false);

        var target = new ObjectLayerTarget(
            editor.ObjectLayerTargets[0].Id, "Города", IsCurrent: false,
            Available: false, Unavailable: "заблокирован");

        Assert.Equal("Города · заблокирован", target.Display);
        Assert.DoesNotContain(target.Id.Value.ToString("N"), target.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Moving_a_whole_selection_to_another_layer_is_one_command_that_keeps_every_level()
    {
        var editor = WithObjects(3, out var ids);
        SelectAll(editor, ids);
        Assert.True(editor.SetSelectionSublayer(2));
        editor.SelectObject(ids[0], additive: false);
        Assert.True(editor.SetSelectionSublayer(-5));
        SelectAll(editor, ids);

        var source = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var levels = ids.ToDictionary(
            id => id,
            id => source.Objects.Single(item => item.Id == id).Sublayer);

        editor.AddObjectLayer();
        var target = editor.Session.Document.Layers.OfType<ObjectLayer>()
            .Single(layer => layer.Id != source.Id);
        SelectAll(editor, ids);
        var history = editor.Session.UndoDepth;

        Assert.True(editor.MoveSelectionToLayer(target.Id));

        Assert.Equal(history + 1, editor.Session.UndoDepth);
        Assert.Equal(3, editor.SelectionCount);
        var moved = editor.Session.Document.Layers.OfType<ObjectLayer>().Single(layer => layer.Id == target.Id);
        Assert.Equal(3, moved.Objects.Count);
        foreach (var id in ids)
        {
            Assert.Equal(levels[id], moved.Objects.Single(item => item.Id == id).Sublayer);
        }
    }

    // -------------------------------------------------------------- the level slider

    [Fact]
    public void The_level_band_is_minus_ten_to_plus_ten_in_whole_steps()
    {
        Assert.Equal(-10, EditorViewModel.MinimumSublayer);
        Assert.Equal(10, EditorViewModel.MaximumSublayer);

        var editor = WithObjects(1, out var ids);
        editor.SelectObject(ids[0], additive: false);

        editor.SetSelectionSublayer(999);
        Assert.Equal(10, editor.Session.SelectedObject!.Sublayer);
        editor.SetSelectionSublayer(-999);
        Assert.Equal(-10, editor.Session.SelectedObject!.Sublayer);

        for (var value = EditorViewModel.MinimumSublayer; value <= EditorViewModel.MaximumSublayer; value++)
        {
            editor.SetSelectionSublayer(value);
            Assert.Equal(value, editor.Session.SelectedObject!.Sublayer);
        }
    }

    [Theory]
    [InlineData(-10, "-10")]
    [InlineData(-1, "-1")]
    [InlineData(0, "0")]
    [InlineData(1, "+1")]
    [InlineData(10, "+10")]
    public void A_positive_level_is_read_with_its_sign(int value, string expected) =>
        Assert.Equal(expected, EditorViewModel.FormatSublayer(value));

    [Fact]
    public void One_slider_gesture_is_one_history_entry_however_many_previews_it_paints()
    {
        var editor = WithObjects(1, out var ids);
        editor.SelectObject(ids[0], additive: false);
        var history = editor.Session.UndoDepth;

        // A drag from 0 to +7: every intermediate stop previews, only the release commits.
        for (var value = 1; value <= 7; value++)
        {
            editor.SetSelectionSublayer(value, commit: false);
        }

        editor.SetSelectionSublayer(7, commit: true);

        Assert.Equal(7, editor.Session.SelectedObject!.Sublayer);
        Assert.Equal(history + 1, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Equal(0, editor.Session.SelectedObject!.Sublayer);
    }

    [Fact]
    public void A_selection_that_agrees_shows_the_shared_level_and_changes_together()
    {
        var editor = WithObjects(3, out var ids);
        SelectAll(editor, ids);
        var history = editor.Session.UndoDepth;

        Assert.False(editor.SelectionSublayerIsMixed);
        Assert.Equal("0", editor.SelectionSublayerReadout);

        Assert.True(editor.SetSelectionSublayer(-3));

        Assert.Equal(history + 1, editor.Session.UndoDepth);
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        Assert.All(ids, id => Assert.Equal(-3, layer.Objects.Single(item => item.Id == id).Sublayer));
    }

    [Fact]
    public void A_mixed_selection_says_so_and_shifts_by_a_shared_delta_in_one_command()
    {
        var editor = WithObjects(3, out var ids);
        editor.SelectObject(ids[0], additive: false);
        editor.SetSelectionSublayer(-2);
        editor.SelectObject(ids[1], additive: false);
        editor.SetSelectionSublayer(1);
        editor.SelectObject(ids[2], additive: false);
        editor.SetSelectionSublayer(5);
        SelectAll(editor, ids);

        Assert.True(editor.SelectionSublayerIsMixed);
        Assert.Null(editor.SelectionSublayer);
        Assert.Equal("разные", editor.SelectionSublayerReadout);

        var history = editor.Session.UndoDepth;
        Assert.True(editor.ShiftSelectionSublayer(3));

        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(1, layer.Objects.Single(item => item.Id == ids[0]).Sublayer);
        Assert.Equal(4, layer.Objects.Single(item => item.Id == ids[1]).Sublayer);
        Assert.Equal(8, layer.Objects.Single(item => item.Id == ids[2]).Sublayer);
        Assert.Equal(history + 1, editor.Session.UndoDepth);

        editor.Undo();
        var restored = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(-2, restored.Objects.Single(item => item.Id == ids[0]).Sublayer);
        Assert.Equal(5, restored.Objects.Single(item => item.Id == ids[2]).Sublayer);
    }

    [Fact]
    public void A_shift_that_would_squash_the_group_against_the_edge_is_refused_whole()
    {
        var editor = WithObjects(2, out var ids);
        editor.SelectObject(ids[0], additive: false);
        editor.SetSelectionSublayer(9);
        editor.SelectObject(ids[1], additive: false);
        editor.SetSelectionSublayer(3);
        SelectAll(editor, ids);

        var history = editor.Session.UndoDepth;

        // Only one step of room exists, so the shift is trimmed to it and the gap of 6 survives.
        Assert.True(editor.ShiftSelectionSublayer(5));

        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(10, layer.Objects.Single(item => item.Id == ids[0]).Sublayer);
        Assert.Equal(4, layer.Objects.Single(item => item.Id == ids[1]).Sublayer);
        Assert.Equal(history + 1, editor.Session.UndoDepth);

        // At the edge there is nothing left to give, and nothing is half-applied.
        Assert.False(editor.ShiftSelectionSublayer(1));
        Assert.Equal(history + 1, editor.Session.UndoDepth);
    }

    [Fact]
    public void The_level_readout_is_a_dash_when_nothing_is_selected()
    {
        var editor = WithObjects(1, out _);
        editor.DeselectObject();
        Assert.Equal("—", editor.SelectionSublayerReadout);
        Assert.False(editor.SelectionSublayerIsMixed);
    }

    // ----------------------------------------------------------------- terminology

    [Fact]
    public void The_user_never_meets_the_word_stamp_as_a_tool_a_kind_or_a_filter()
    {
        Assert.Equal("Размещение ассета", EditorLabels.Tool(EditorTool.Stamp));
        Assert.Equal("Ассет", EditorLabels.ObjectKind(SceneObjectKind.Stamp));
        Assert.Equal("Ассеты", ObjectTreeRows.FilterTitle(ObjectTypeFilter.Stamps));

        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            Assert.DoesNotContain("Штамп", EditorLabels.Tool(tool), StringComparison.Ordinal);
        }

        foreach (var kind in Enum.GetValues<SceneObjectKind>())
        {
            Assert.DoesNotContain("Штамп", EditorLabels.ObjectKind(kind), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_shortcut_the_user_can_read_calls_it_a_stamp()
    {
        // The earlier check only looked at EditorLabels and the object tree, so the word survived
        // in the shortcut sheet, in tooltips and in status lines - places the user reads far more
        // often than a filter caption.
        foreach (var definition in EditorShortcutSettings.Definitions)
        {
            Assert.DoesNotContain("штамп", definition.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("штамп", definition.Group, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("штамп", definition.Description, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var context in Enum.GetValues<ShortcutContext>())
        {
            Assert.DoesNotContain(
                "штамп", EditorShortcutSettings.ContextName(context), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_status_lines_a_placement_produces_call_it_an_asset()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);

        // Refusals are the strings a user meets most often while learning the tool.
        editor.ArmStampPlacement();
        editor.CommitPendingPlacement();
        Assert.DoesNotContain("штамп", editor.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("штамп", editor.PlacementRefusal ?? "", StringComparison.OrdinalIgnoreCase);

        foreach (var mode in Enum.GetValues<StampPlacementMode>())
        {
            editor.StampPlacementMode = mode;
            Assert.DoesNotContain("штамп", editor.StampAreaSummary ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_row_whose_name_already_says_the_kind_does_not_repeat_it()
    {
        // "Дерево · Ассет" next to a picture of a tree told the user nothing twice.
        Assert.Null(EditorLabels.SecondaryKind("Ассет", "Ассет"));
        Assert.Null(EditorLabels.SecondaryKind("Ассет 3", "Ассет"));
        Assert.Equal("Ассет", EditorLabels.SecondaryKind("Горы", "Ассет"));
        Assert.Equal("Фигура", EditorLabels.SecondaryKind("Озеро", "Фигура"));
    }
}
