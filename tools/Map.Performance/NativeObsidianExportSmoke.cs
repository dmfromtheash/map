using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Export;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    /// <summary>
    /// R19 extends native-player-package: --output artifacts/r19/player-package.json.
    /// Clicks ExportObsidianButton for both recipients, checks complete folders and every generated
    /// link, then re-exports a renamed hex. Uses the profile/recovery/shortcuts under that workspace.
    /// No control/screen captures; Map.LiveCheck --obsidian-export true supplies PrintWindow shots.
    /// </summary>
    private async Task VerifyObsidianPackage(Window export, PlayerPackageDestination destination, MapDocument document, bool players)
    {
        var button = export.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ExportObsidianButton");
        button.BringIntoView(); await Settle();
        Check("R19 Obsidian button is visible and enabled for " + destination.Recipient, button.IsEffectivelyVisible && button.IsEnabled);
        Click(export, "ExportObsidianButton");
        await WaitUntil(() => Directory.Exists(destination.ObsidianFolder), "Obsidian " + destination.Recipient);
        var files = Directory.GetFiles(destination.ObsidianFolder, "*.md")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), File.ReadAllText);
        var text = string.Join('\n', files.Values);
        Check("R19 Obsidian folder has hexes regions YAML tags and index for " + destination.Recipient,
            files.Count == (players ? 22 : 83) && files.ContainsKey("index")
            && files.Values.All(t => t.StartsWith("---", StringComparison.Ordinal) && t.Contains("tags:", StringComparison.Ordinal))
            && text.Contains("#гекс/", StringComparison.Ordinal) && text.Contains("#регион/", StringComparison.Ordinal)
            && text.Contains("Знакомая переправа", StringComparison.Ordinal));
        var secrets = new[] { "GM_SECRET", "GM_TITLE", "HIDDEN_TEXT", "HIDDEN_TITLE", "HIDDEN_REGION", "secret.mapproj", "childMap" };
        Check("R19 Obsidian recipient privacy for " + destination.Recipient,
            players ? secrets.All(s => !text.Contains(s, StringComparison.Ordinal)) : secrets.All(s => text.Contains(s, StringComparison.Ordinal)));
        Check("R19 all Obsidian links resolve for " + destination.Recipient,
            files.Values.SelectMany(t => Regex.Matches(t, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]"))
                .All(m => files.ContainsKey(m.Groups[1].Value)));
        if (!players)
        {
            var layer = document.Layers.OfType<HexLayer>().Single();
            var hex = layer.Board.Layout.FromOffset(new(2, 2));
            var renamed = document.WithLayer(layer.Id, _ => layer with
            {
                Notes = layer.Notes.SetItem(hex, layer.Notes[hex] with { Title = "Новое название переправы" }),
            });
            var result = HexObsidianExporter.Write(new ExportScene(renamed), Path.Combine(_workspace, "renamed-obsidian"));
            Check("R19 renaming a hex preserves every filename", files.Keys.Order().SequenceEqual(
                Directory.GetFiles(result.Path, "*.md").Select(Path.GetFileNameWithoutExtension).Order()));
        }
        await Settle();
    }
}
