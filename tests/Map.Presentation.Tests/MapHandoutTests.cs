using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R09f: the handout is chosen in the new-map dialog and changed on «Экспорт» as one Undo step.</summary>
public sealed class MapHandoutTests
{
    [Fact]
    public void The_dialog_puts_the_handout_into_the_new_map_on_paper()
    {
        var wizard = new NewMapWizardViewModel { Title = "Свиток" };
        Assert.Equal(MapHandout.None, wizard.Create(DateTimeOffset.UnixEpoch)!.Handout);

        wizard.Handout = MapHandout.ScrollBone;
        var created = wizard.Create(DateTimeOffset.UnixEpoch)!;
        Assert.Equal(MapHandout.ScrollBone, created.Handout);
        Assert.Equal(NewMapWizardViewModel.PaperBackground, created.BaseColor.Color);
    }

    [Fact]
    public void A_page_colour_chosen_by_hand_stays_with_a_handout()
    {
        var wizard = new NewMapWizardViewModel { Title = "Лист", Background = "#FF223322" };
        wizard.Handout = MapHandout.Burnt;
        Assert.Equal("#FF223322", wizard.Create(DateTimeOffset.UnixEpoch)!.BaseColor.Color);
    }

    [Fact]
    public void Changing_the_handout_in_the_editor_is_one_undo_step()
    {
        var document = new NewMapWizardViewModel { Title = "Карта" }.Create(DateTimeOffset.UnixEpoch)!;
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var depth = editor.Session.UndoDepth;
        editor.Handout = MapHandout.PortolanSkin;
        Assert.Equal(MapHandout.PortolanSkin, editor.Session.Document.Handout);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        editor.Undo();
        Assert.Equal(MapHandout.None, editor.Handout);
    }
}
