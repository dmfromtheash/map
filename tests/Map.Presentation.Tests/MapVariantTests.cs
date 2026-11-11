using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R09e: named variants of the map - one click per variant, one Ctrl+Z, the chosen variant keeps the edits.</summary>
public sealed class MapVariantTests
{
    private static readonly LayerId Notes = LayerId.New();

    private static MapDocument Map()
    {
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        var ground = new LandLayer(LayerId.New(), "Суша", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor());
        var name = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Имя", 10, 10, 200, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Северные горы", FontSize = 40 } };
        var objects = new ObjectLayer(Notes, "Подписи", true, false, 1, [name]);
        return new MapDocument(DocumentId.New(), "Варианты", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2"), ground, objects])
        {
            Style = MapStyle.For(MapStylePreset.OldMapBlaeu) with { Enabled = true },
            Lighting = new MapLighting { Enabled = true, TimeOfDay = 0.5, Sources = [new MapLightSource(Guid.NewGuid(), 100, 100, "#FFFFC070", 200, 1, false)] },
        };
    }

    private static EditorViewModel Editor(MapDocument document) => new(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
        new FakeFilePicker(), new FakePrompts(), new FakeLog());

    private static Guid IdOf(EditorViewModel editor, string name) => editor.Session.Document.Variants.Items.Single(v => v.Name == name).Id;

    [Fact]
    public void The_starters_are_day_night_and_political_in_one_undo_step()
    {
        var editor = Editor(Map());
        var before = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.AddStarterMapVariants());
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var document = editor.Session.Document;
        Assert.Equal(["День", "Ночь", "Зима", "Политическая"], document.Variants.Items.Select(v => v.Name));
        Assert.Equal(IdOf(editor, "День"), document.Variants.Active);
        Assert.Equal(MapVariantEditing.DayTime, document.Lighting!.TimeOfDay);
        Assert.Equal(before.Style, document.Style);

        editor.Undo();
        Assert.True(editor.Session.Document.Variants.IsDefault);
        Assert.Equal(before.Lighting, editor.Session.Document.Lighting);
    }

    [Fact]
    public void A_click_shows_the_variant_and_one_undo_brings_the_former_look_back()
    {
        var editor = Editor(Map());
        editor.AddStarterMapVariants();
        var day = editor.Session.Document;
        var depth = editor.Session.UndoDepth;

        Assert.True(editor.SwitchMapVariant(IdOf(editor, "Ночь")));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(MapVariantEditing.NightTime, editor.Session.Document.Lighting!.TimeOfDay);
        Assert.Equal(IdOf(editor, "Ночь"), editor.Session.Document.Variants.Active);

        Assert.True(editor.SwitchMapVariant(IdOf(editor, "Политическая")));
        var political = editor.Session.Document;
        Assert.True(political.Timeline.PoliticalMode);
        Assert.False(political.Style!.Enabled);
        Assert.False(political.Lighting!.Enabled);

        editor.Undo();
        editor.Undo();
        Assert.Equal(day.Lighting, editor.Session.Document.Lighting);
        Assert.Equal(day.Style, editor.Session.Document.Style);
        Assert.False(editor.Session.Document.Timeline.PoliticalMode);
        Assert.Equal(IdOf(editor, "День"), editor.Session.Document.Variants.Active);
    }

    [Fact]
    public void The_chosen_variant_keeps_the_edits_of_its_look()
    {
        var editor = Editor(Map());
        editor.AddStarterMapVariants();
        editor.SwitchMapVariant(IdOf(editor, "Ночь"));
        // At night the names are hidden and the light is set to evening.
        editor.ToggleLayerVisibility(Notes);
        editor.SetTimeOfDay(0.6, true);

        editor.SwitchMapVariant(IdOf(editor, "День"));
        Assert.True(editor.Session.Document.FindLayer(Notes)!.Visible);
        Assert.Equal(MapVariantEditing.DayTime, editor.Session.Document.Lighting!.TimeOfDay);

        editor.SwitchMapVariant(IdOf(editor, "Ночь"));
        Assert.False(editor.Session.Document.FindLayer(Notes)!.Visible);
        Assert.Equal(0.6, editor.Session.Document.Lighting!.TimeOfDay, 3);
    }

    [Fact]
    public void The_fires_of_the_light_are_one_for_every_variant()
    {
        var editor = Editor(Map());
        editor.AddStarterMapVariants();
        var fires = editor.Session.Document.Lighting!.Sources;
        Assert.Single(fires);
        editor.SwitchMapVariant(IdOf(editor, "Ночь"));
        Assert.Equal(fires, editor.Session.Document.Lighting!.Sources);
        Assert.All(editor.MapVariants.Items, v => Assert.True(v.Lighting is null || v.Lighting.Sources.IsEmpty));
    }

    [Fact]
    public void Remembering_the_look_adds_a_chosen_variant_and_removing_keeps_the_map()
    {
        var editor = Editor(Map());
        Assert.True(editor.AddMapVariant());
        var own = editor.Session.Document.Variants.Items.Single();
        Assert.Equal("Вариант 1", own.Name);
        Assert.Equal(own.Id, editor.Session.Document.Variants.Active);

        Assert.True(editor.RenameMapVariant(own.Id, "  Осада  "));
        Assert.Equal("Осада", editor.Session.Document.Variants.Items.Single().Name);

        var look = editor.Session.Document;
        Assert.True(editor.RemoveMapVariant(own.Id));
        Assert.True(editor.Session.Document.Variants.IsDefault);
        Assert.Equal(look.Style, editor.Session.Document.Style);
        Assert.Equal(look.Lighting, editor.Session.Document.Lighting);
        editor.Undo();
        Assert.Equal("Осада", editor.Session.Document.Variants.Items.Single().Name);
    }

    [Fact]
    public void Choosing_the_shown_variant_again_is_no_step()
    {
        var editor = Editor(Map());
        editor.AddStarterMapVariants();
        var depth = editor.Session.UndoDepth;
        Assert.False(editor.SwitchMapVariant(IdOf(editor, "День")));
        Assert.Equal(depth, editor.Session.UndoDepth);
    }

    [Fact]
    public void The_picture_of_a_variant_shows_the_map_in_it_without_changing_the_map()
    {
        var editor = Editor(Map());
        editor.AddStarterMapVariants();
        var document = editor.Session.Document;
        var night = EditorViewModel.VariantPreview(document, IdOf(editor, "Ночь"));
        Assert.Equal(MapVariantEditing.NightTime, night.Lighting!.TimeOfDay);
        Assert.Same(document, editor.Session.Document);
        Assert.Equal(MapVariantEditing.DayTime, document.Lighting!.TimeOfDay);
    }
}
