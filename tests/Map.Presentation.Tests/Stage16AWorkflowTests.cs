using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Platform.Windows;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class Stage16AWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-stage16a", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Metadata_search_and_sort_do_not_inspect_or_write_project_files()
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var inspected = 0;
        store.OnInspect = _ => { inspected++; throw new InvalidOperationException(); };
        recent.Items.Add(new RecentMapItem("forest.mapproj", "Forest", DateTimeOffset.UnixEpoch,
            true, "World", 90, [1]));
        recent.Items.Add(new RecentMapItem("city.mapproj", "City", DateTimeOffset.UnixEpoch.AddDays(1),
            true, "Region", 120, [2]));
        var dashboard = new DashboardViewModel(store, recent, new FakeLog());

        dashboard.Refresh();
        Assert.Equal(2, dashboard.TotalRecentCount);
        dashboard.SearchText = "world";
        dashboard.SortOrder = DashboardSortOrder.SizeDescending;

        Assert.Equal("Forest", Assert.Single(dashboard.RecentCards).Title);
        dashboard.SearchText = "no matching map";
        Assert.Empty(dashboard.RecentCards);
        Assert.Equal(2, dashboard.TotalRecentCount);
        dashboard.SearchText = "";
        Assert.Equal(2, dashboard.RecentCards.Count);
        recent.Items.Clear();
        dashboard.Refresh();
        Assert.Empty(dashboard.RecentCards);
        Assert.Equal(0, dashboard.TotalRecentCount);
        Assert.Equal(0, inspected);
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_and_template_create_independent_document_ids(bool asTemplate)
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.mapproj");
        var destination = Path.Combine(_root, asTemplate ? "template-copy.mapproj" : "clone.mapproj");
        var source = TestData.Document("Source");
        var store = new FakeProjectStore();
        store.Save(source, sourcePath, new SaveMetadata("test", DateTimeOffset.UnixEpoch), [1]);
        var picker = new FakeFilePicker { NextSavePath = destination };
        var recent = new FakeRecentMaps();
        var dashboard = new DashboardViewModel(
            store, recent, new FakeLog(), picker, new FakePrompts(), new FakeProjectTrash(),
            () => DateTimeOffset.UnixEpoch.AddDays(2), "stage16-test");

        var result = await dashboard.CloneAsync(
            new RecentCard(sourcePath, source.Title, DateTimeOffset.UnixEpoch, true, [1]),
            asTemplate);

        Assert.Equal(destination, result);
        var copy = store.Saved[destination].Document;
        Assert.NotEqual(source.Id, copy.Id);
        Assert.NotEqual(source.Title, copy.Title);
        Assert.Equal(source.Canvas, copy.Canvas);
        Assert.Equal(source.Layers, copy.Layers);
        Assert.Empty(copy.Checkpoints);
    }

    [Fact]
    public async Task Delete_reaches_only_the_confirmed_synthetic_target()
    {
        var card = new RecentCard("synthetic.mapproj", "Synthetic", DateTimeOffset.UnixEpoch, true, [1]);
        var prompts = new FakePrompts();
        var trash = new FakeProjectTrash();
        var dashboard = new DashboardViewModel(
            new FakeProjectStore(), new FakeRecentMaps(), new FakeLog(),
            new FakeFilePicker(), prompts, trash);

        Assert.False(await dashboard.DeleteAsync(card));
        Assert.Empty(trash.Paths);

        prompts.NextDeleteProjectConfirmation = true;
        Assert.True(await dashboard.DeleteAsync(card));
        Assert.Equal([card.Path], trash.Paths);
        Assert.Equal(2, prompts.DeleteProjectAsked);
    }

    [Fact]
    public void Composition_package_roundtrip_keeps_hash_and_provenance()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.mapcomp");
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+qvOB0QAAAABJRU5ErkJggg==");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var asset = new MapAssetReference(
            hash, "owner stamp", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "owner-created", "CC0-1.0", bytes);
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "stamp", 0, 0, 32, 32, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(hash) };
        var source = new MapDocument(
            DocumentId.New(), "source", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(64, 64, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [stamp]),
            ], [asset]);
        var package = CompositionTransfer.Copy(source, [stamp.Id], name: "Owner package");

        CompositionPackageStore.Export(
            package, path, new SaveMetadata("stage16-test", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(source));
        var reopened = CompositionPackageStore.Import(path);

        var actual = Assert.Single(reopened.Assets);
        Assert.Equal(hash, actual.Sha256);
        Assert.Equal("owner-created", actual.Provenance);
        Assert.Equal("CC0-1.0", actual.License);
        Assert.Equal("Owner package", reopened.Composition.Name);
    }

    [Fact]
    public void Settings_roundtrip_clamps_values_and_reset_restores_services_defaults()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var store = new AppSettingsStore(path);

        var saved = store.Save(new AppSettings
        {
            Theme = LocalThemeMode.Light,
            UiScale = 9,
            AutosaveIntervalSeconds = 1,
            RecoveryItemBudget = 500,
            PerformanceMode = CanvasPerformanceMode.Performance,
            DefaultExportScalePercent = 4_000,
        });
        var loaded = store.Load();

        Assert.Equal(saved, loaded);
        Assert.Equal(AppSettings.MaxUiScale, loaded.UiScale);
        Assert.Equal(AppSettings.MinAutosaveSeconds, loaded.AutosaveIntervalSeconds);
        Assert.Equal(AppSettings.MaxRecoveryItems, loaded.RecoveryItemBudget);
        Assert.Equal(800, loaded.DefaultExportScalePercent);

        Assert.Equal(new AppSettings(), store.Reset());
        Assert.Equal(new AppSettings(), store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
