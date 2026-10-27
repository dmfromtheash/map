using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public class NewMapWizardTests
{
    [Theory]
    [InlineData("0", "1080")]
    [InlineData("-100", "1080")]
    [InlineData("1920", "0")]
    [InlineData("999999", "1080")]
    [InlineData("abc", "1080")]
    [InlineData("", "")]
    public void Nonsense_dimensions_are_refused_with_a_message(string width, string height)
    {
        var wizard = new NewMapWizardViewModel { WidthText = width, HeightText = height };

        Assert.False(wizard.Validate());
        Assert.NotEqual("", wizard.ValidationError);
        Assert.Null(wizard.Create(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void An_empty_title_is_refused()
    {
        var wizard = new NewMapWizardViewModel { Title = "   " };
        Assert.False(wizard.Validate());
    }

    [Fact]
    public void A_valid_wizard_creates_a_blank_document_with_one_layer()
    {
        var wizard = new NewMapWizardViewModel
        {
            Title = "  Моя карта  ",
            WidthText = "800",
            HeightText = "600",
            Background = "#FFF2E9DC",
        };

        var document = wizard.Create(DateTimeOffset.UnixEpoch);

        Assert.NotNull(document);
        Assert.Equal("Моя карта", document!.Title);
        Assert.Equal(800, document.Canvas.Width);
        Assert.Equal(600, document.Canvas.Height);
        Assert.Equal("#FFF2E9DC", document.Canvas.Background);
        Assert.Equal(2, document.Layers.Count);
        // DEC-069: the sea of a new map is «Рябь штрихом» with its ready colours.
        Assert.Equal(SeaStyle.For(SeaManner.Engraved), Assert.IsType<BaseColorLayer>(document.Layers[0]).Sea);
        Assert.Empty(document.Layers.OfType<ObjectLayer>().Single().Objects);
        Assert.Equal("", wizard.ValidationError);
    }

    [Fact]
    public void Large_map_profile_is_available_at_the_wizard_boundary()
    {
        var accepted = new NewMapWizardViewModel { WidthText = "131072", HeightText = "131072" };
        var refused = new NewMapWizardViewModel { WidthText = "131073", HeightText = "131072" };

        Assert.True(accepted.Validate());
        Assert.Equal(131_072, accepted.Create(DateTimeOffset.UnixEpoch)!.Canvas.Width);
        Assert.False(refused.Validate());
    }

    [Fact]
    public void Swap_orientation_swaps_the_dimensions()
    {
        var wizard = new NewMapWizardViewModel { WidthText = "1920", HeightText = "1080" };
        wizard.SwapOrientation();

        Assert.Equal("1080", wizard.WidthText);
        Assert.Equal("1920", wizard.HeightText);
    }
}

public class DashboardViewModelTests
{
    private static (DashboardViewModel Dashboard, FakeProjectStore Store, FakeRecentMaps Recent) New()
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        return (new DashboardViewModel(store, recent, new FakeLog()), store, recent);
    }

    [Fact]
    public void Recent_cards_carry_thumbnails_and_missing_files_are_marked_honestly()
    {
        var (dashboard, store, recent) = New();

        var document = TestData.Document("alive");
        var metadata = new SaveMetadata("t", DateTimeOffset.UnixEpoch);
        store.Save(document, @"C:\maps\alive.mapproj", metadata, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        recent.Touch(@"C:\maps\alive.mapproj", document.Id.Value, "alive", DateTimeOffset.UnixEpoch);
        recent.Items.Add(new RecentMapItem(@"C:\maps\gone.mapproj", "gone", DateTimeOffset.UnixEpoch, false));

        dashboard.Refresh();

        Assert.Equal(2, dashboard.RecentCards.Count);

        var alive = dashboard.RecentCards.Single(c => c.Title == "alive");
        Assert.True(alive.IsAvailable);
        Assert.NotNull(alive.ThumbnailPng);

        var gone = dashboard.RecentCards.Single(c => c.Title == "gone");
        Assert.False(gone.IsAvailable);
        Assert.Null(gone.ThumbnailPng);
        Assert.Equal("Файл не найден", gone.Subtitle);
    }

    [Fact]
    public void A_recent_entry_whose_container_is_now_corrupt_becomes_unavailable_not_a_crash()
    {
        var (dashboard, store, recent) = New();
        recent.Items.Add(new RecentMapItem(@"C:\maps\bad.mapproj", "bad", DateTimeOffset.UnixEpoch, true));
        store.OnOpen = _ => throw new MapProjectCorruptedException("scene.json does not match the checksum.");

        dashboard.Refresh();

        var card = Assert.Single(dashboard.RecentCards);
        Assert.False(card.IsAvailable);
    }

    [Fact]
    public void Opening_a_corrupt_file_produces_a_clear_error_and_no_editor()
    {
        var (dashboard, store, _) = New();
        store.OnOpen = _ => throw new MapProjectCorruptedException("scene.json does not match the checksum.");

        Assert.Null(dashboard.TryOpen(@"C:\maps\bad.mapproj"));
        Assert.Contains("повреждён", dashboard.LastError);
        Assert.Contains("проверку", dashboard.LastError);
    }

    [Fact]
    public void Opening_a_future_schema_file_names_the_version_and_promises_no_change()
    {
        var (dashboard, store, _) = New();
        store.OnOpen = _ => throw new UnsupportedSchemaVersionException(7);

        Assert.Null(dashboard.TryOpen(@"C:\maps\future.mapproj"));
        Assert.Contains("более новой версией", dashboard.LastError);
        Assert.Contains("не поддерживает", dashboard.LastError);
    }

    [Fact]
    public void A_vanished_file_is_removed_from_recent_with_a_clear_message()
    {
        var (dashboard, store, recent) = New();
        recent.Items.Add(new RecentMapItem(@"C:\maps\gone.mapproj", "gone", DateTimeOffset.UnixEpoch, true));
        store.OnOpen = _ => throw new FileNotFoundException();

        Assert.Null(dashboard.TryOpen(@"C:\maps\gone.mapproj"));
        Assert.Contains("не найден", dashboard.LastError);
        Assert.Empty(recent.Items);
    }

    [Fact]
    public void Recovery_cards_surface_candidates_and_discard_removes_them()
    {
        var (dashboard, store, _) = New();
        var candidate = new RecoveryCandidate
        {
            DocumentId = Guid.NewGuid(),
            TargetPath = @"C:\maps\doc.mapproj",
            CandidatePath = @"C:\maps\doc.mapproj.tmp-1",
            UpdatedUtc = DateTimeOffset.UnixEpoch,
            Title = "почти потерянная",
        };
        store.Candidates.Add(candidate);

        dashboard.Refresh();
        var card = Assert.Single(dashboard.RecoveryCards);
        Assert.Contains("почти потерянная", card.Description);

        dashboard.Discard(card);
        Assert.Empty(dashboard.RecoveryCards);
        Assert.Empty(store.Candidates);
    }

    [Fact]
    public void Successful_open_touches_recent_and_clears_the_error()
    {
        var (dashboard, store, recent) = New();
        var document = TestData.Document("opened");
        store.Save(document, @"C:\maps\opened.mapproj",
            new SaveMetadata("t", DateTimeOffset.UnixEpoch), [1, 2, 3]);
        dashboard.LastError = "старое сообщение";

        var opened = dashboard.TryOpen(@"C:\maps\opened.mapproj");

        Assert.NotNull(opened);
        Assert.Equal("", dashboard.LastError);
        Assert.Single(recent.Items);
    }
}
