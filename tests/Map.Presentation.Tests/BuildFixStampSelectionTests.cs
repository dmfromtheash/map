using System.Collections;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Map.App.Views;
using Map.Assets;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[Collection("Editor refresh")]
public sealed class BuildFixStampSelectionTests
{
    [Fact]
    public async Task Two_set_changes_before_layout_build_the_second_sets_variants()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-quick-sets-" + Guid.NewGuid().ToString("N"));
        var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
        try
        {
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project is not null && !File.Exists(Path.Combine(project.FullName, "Map.slnx"))) project = project.Parent;
            string Art(int index) => Path.Combine(project!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{index:00}.png");
            var first = catalog.ImportSet([Art(1), Art(2), Art(3)], "First set", new(), DateTimeOffset.UnixEpoch);
            var second = catalog.ImportSet([Art(4), Art(5)], "Second set", new(), DateTimeOffset.UnixEpoch);
            var editor = new EditorViewModel(TestData.Document(), null, false,
                new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog);
            await EditorUiThread.Run(() =>
            {
                var context = SynchronizationContext.Current;
                var view = UiProbe.Create(editor);
                try
                {
                    var strip = (ListBox)typeof(EditorView).GetField("_stampVariantStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
                    var sync = typeof(EditorView).GetMethod("SyncCompactVariantStrip", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    editor.SelectAssetSet(first.Id);
                    sync.Invoke(view, null);
                    var pendingRow = Assert.Single(((IEnumerable)strip.ItemsSource!).Cast<object>());
                    editor.SelectAssetSet(second.Id);

                    // Avalonia may realize an old row after the model changed, before its next layout.
                    // Execute that pending production template, then synchronize the current strip.
                    var pendingPanel = Assert.IsType<StackPanel>(strip.ItemTemplate!.Build(pendingRow));
                    Assert.Equal(3, pendingPanel.Children.Count);
                    var staleButton = Assert.IsType<ToggleButton>(pendingPanel.Children[2]);
                    Assert.Equal("First set · вариант 3 из 3", AutomationProperties.GetName(staleButton));
                    staleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(second.Id, editor.SelectedSetId);
                    sync.Invoke(view, null);
                    var currentRow = Assert.Single(((IEnumerable)strip.ItemsSource!).Cast<object>());
                    var panel = Assert.IsType<StackPanel>(strip.ItemTemplate.Build(currentRow));
                    Assert.Equal(second.VariantIds.Select(id => "StampVariant_" + id), panel.Children.Cast<ToggleButton>().Select(b => b.Name));
                    var button = Assert.IsType<ToggleButton>(panel.Children[0]);
                    Assert.Equal("Second set · вариант 1 из 2, текущий", AutomationProperties.GetName(button));
                    Assert.Equal(second.Id, editor.SelectedSetId);
                    panel.Children[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(second.VariantIds[1], editor.CurrentVariant!.Id);
                }
                finally
                {
                    UiProbe.Dispose(view);
                    SynchronizationContext.SetSynchronizationContext(context);
                }
            });
        }
        finally
        {
            await catalog.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
