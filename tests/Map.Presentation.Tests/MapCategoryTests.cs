using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public class MapCategoryTests
{
    [Fact]
    public void The_standard_constructor_is_the_only_category_of_this_build()
    {
        var standard = Assert.Single(MapCategories.All);
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
