using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    /// <summary>
    /// `native-map-categories`: the create dialog in its own off-screen window. Step 1 lists the
    /// categories with the standard constructor chosen, the create button is not reachable there;
    /// "Выбрать категорию" leads to the settings step, "Назад" returns; a map created from the
    /// standard category carries the standard id, saves at the pre-category schema with no
    /// `category` field and opens back as standard. No system input, nothing outside the workspace.
    /// </summary>
    private async Task VerifyMapCategories()
    {
        var wizard = new WizardWindow(_catalog)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
        };
        wizard.Show(); await Settle(); Capture(wizard, "01-category-step");

        var cards = wizard.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Name?.StartsWith("WizardCategory", StringComparison.Ordinal) == true).ToList();
        // Three since the city workshop (standard, hex, city); the list itself is the source of truth.
        Check("step 1 lists exactly the known categories", cards.Count == MapCategories.All.Count);
        Check("the standard constructor is chosen by default", cards[0].Name == "WizardCategoryStandard"
            && cards[0].IsChecked == true && cards[1].IsChecked != true
            && wizard.SelectedCategory == MapCategoryId.Standard && wizard.Step == 1);
        Check("the detail pane names the chosen category", Text(wizard, "WizardCategoryTitle") == MapCategories.Standard.Title
            && Text(wizard, "WizardCategoryDescription") == MapCategories.Standard.Description);
        Check("step 1 shows what is inside", wizard.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "WizardCategoryHighlights")
            .Children.Count == MapCategories.Standard.Highlights.Count);
        Check("step 1 has no create button and no size presets", !Visible(wizard, "CreateMapButton") && !Visible(wizard, "WizardPresetStandard")
            && Visible(wizard, "ChooseCategoryButton") && Visible(wizard, "CancelWizardButton"));
        Check("the dialog title says step 1", wizard.Title.Contains("шаг 1"));

        Click(wizard, "ChooseCategoryButton"); await Settle(); Capture(wizard, "02-settings-step");
        Check("step 2 shows the settings of the map", wizard.Step == 2 && Visible(wizard, "CreateMapButton")
            && Visible(wizard, "WizardPresetStandard") && Visible(wizard, "WizardBackButton") && !Visible(wizard, "ChooseCategoryButton"));
        Check("step 2 names the category in the header", Text(wizard, "WizardStepTitle").Contains(MapCategories.Standard.Title));
        Check("the standard category starts from its default size", wizard.GetVisualDescendants().OfType<ToggleButton>()
            .Single(b => b.Name == "WizardPresetStandard").IsChecked == true
            && wizard.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WizardWidthBox").Text == "1920");

        Click(wizard, "WizardBackButton"); await Settle();
        Check("back returns to the category step", wizard.Step == 1 && Visible(wizard, "ChooseCategoryButton") && !Visible(wizard, "CreateMapButton"));
        Click(wizard, "ChooseCategoryButton"); await Settle();
        wizard.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WizardTitleBox").Text = "Категория · проверка";
        Click(wizard, "CreateMapButton");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (wizard.CreatedDocument is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        var document = wizard.CreatedDocument ?? throw new InvalidOperationException("The wizard did not create the map.");
        wizard.Close();
        Check("the created map belongs to the standard category", document.Category == MapCategoryId.Standard
            && document.Category.IsStandard && document.Title == "Категория · проверка");

        var path = Path.Combine(_workspace, "category-standard.mapproj");
        MapProjectStore.Save(document, path, new SaveMetadata("smoke", DateTimeOffset.UtcNow), PreviewPng());
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
        {
            var root = manifest.RootElement;
            Check("a standard map saves at the pre-category schema without a category field",
                // The category itself adds nothing: no field. The version is what the content needs (a new
                // map's sea is v32), so it is "not below the base schema", not "exactly v4".
                root.GetProperty("schemaVersion").GetInt32() >= MapProjectFormat.SchemaVersion && !root.TryGetProperty("category", out _));
        }
        var reopened = MapProjectStore.Open(path).Document;
        Check("a standard map opens back as standard", reopened.Category == MapCategoryId.Standard && reopened.Title == document.Title);
    }

    private static bool Visible(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name) is { IsEffectivelyVisible: true };

    private static string Text(Control root, string name) =>
        root.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == name).Text ?? "";

    /// <summary>A valid 1×1 PNG for the container's preview entry.</summary>
    private static byte[] PreviewPng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1, 1);
        bitmap.SetPixel(0, 0, SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
