using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public class MapCategoryTests
{
    [Fact]
    public void The_build_offers_the_standard_hex_and_city_workshops_in_that_order()
    {
        Assert.Equal([MapCategories.Standard, MapCategories.Hex, MapCategories.City], MapCategories.All);
        Assert.Equal("city", MapCategories.City.Id.Value);
        Assert.True(MapCategories.IsCity(MapCategories.City.Id));
        Assert.False(MapCategories.IsHex(MapCategories.City.Id));
        var wizard = new NewMapWizardViewModel { WidthText = "1600", HeightText = "1600", Category = MapCategories.City.Id, Title = "Ильград" };
        var city = wizard.Create(DateTimeOffset.UnixEpoch)!;
        Assert.Equal("city", city.Category.Value);
        Assert.Contains(city.Layers.OfType<ObjectLayer>(), l => l.Name == "Город" && l.Objects.Count > 50);
        var hex = MapCategories.Hex;
        Assert.Equal("hex", hex.Id.Value);
        Assert.Equal(MapCategories.HexWorkshop, hex.Workshop);
        Assert.NotEqual(MapCategories.StandardWorkshop, hex.Workshop);
        Assert.True(MapCategories.IsHex(hex.Id));
        Assert.False(MapCategories.IsHex(MapCategoryId.Standard));
        Assert.NotEmpty(hex.Highlights);
        var standard = MapCategories.Standard;
        Assert.Equal(MapCategoryId.Standard, standard.Id);
        Assert.Equal("Стандартный Конструктор карт", standard.Title);
        Assert.Equal(MapCategories.StandardWorkshop, standard.Workshop);
        Assert.NotEmpty(standard.Highlights);
        Assert.True(MapCategories.IsKnown(MapCategoryId.Standard));
        Assert.False(MapCategories.IsKnown(new MapCategoryId("other-workshop")));
        Assert.Null(MapCategories.Find(new MapCategoryId("other-workshop")));
    }

    [Fact]
    public void The_wizard_writes_its_category_into_the_document()
    {
        var wizard = new NewMapWizardViewModel { WidthText = "800", HeightText = "600" };
        Assert.Equal(MapCategoryId.Standard, wizard.Create(DateTimeOffset.UnixEpoch)!.Category);

        wizard.Category = new MapCategoryId("other-workshop");
        Assert.Equal("other-workshop", wizard.Create(DateTimeOffset.UnixEpoch)!.Category.Value);
    }

    [Fact]
    public void A_map_of_an_unknown_category_is_refused_with_a_message_and_no_editor()
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var dashboard = new DashboardViewModel(store, recent, new FakeLog());
        var foreign = new MapDocument(
            DocumentId.New(), "Чужая мастерская", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(640, 480, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")],
            category: new MapCategoryId("other-workshop"));
        store.OnOpen = _ => new OpenedProject
        {
            Document = foreign, AppVersion = "test", ModifiedUtc = DateTimeOffset.UnixEpoch, PreviewPng = [],
        };

        Assert.Null(dashboard.TryOpen(@"C:\maps\foreign.mapproj"));
        Assert.Contains("other-workshop", dashboard.LastError);
        Assert.Contains("Чужая мастерская", dashboard.LastError);
        Assert.Contains("нет в этой сборке", dashboard.LastError);
        Assert.Empty(recent.Items);
        Assert.False(dashboard.AcceptsCategory(foreign));
    }
}
