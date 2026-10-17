using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Platform.Windows;
using SkiaSharp;

namespace Map.Performance;

internal sealed record MountainStyle(string Id, string Name);
internal sealed record MountainAsset(string Id, string Style, string Name, string File, string[] Tags, double Width);
internal sealed record MountainManifest(MountainStyle[] Styles, MountainAsset[] Assets,
    string? Name = null, string? ProvenanceSource = null, string? GenerationTool = null);
internal sealed record MountainInstallReport(bool GatePassed, string Root, int Added, int PackAssets, int PackStyles);

// An explicit authoring/import command for this small pack, never called by application startup.
internal static class MountainFoundationPack
{
    internal static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("MAP_MOUNTAIN_PACK_ROOT")
        ?? Path.Combine(Environment.CurrentDirectory, "content", "Mountains.Foundation"));
    internal static MountainManifest Load() => JsonSerializer.Deserialize<MountainManifest>(
        File.ReadAllText(Path.Combine(Root, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Missing mountain manifest");

    internal static string ImagePath(MountainAsset asset)
    {
        var path = Path.GetFullPath(Path.Combine(Root, asset.File));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) throw new InvalidDataException("Missing pack image: " + asset.Id);
        return path;
    }

    internal static Dictionary<string, AssetRecord> Import(AssetCatalog catalog, MountainManifest pack)
    {
        if (catalog.Status == AssetLibraryStatus.Damaged) throw new InvalidDataException(catalog.Diagnostic);
        var originalHashes = catalog.Records.Select(r => r.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, AssetRecord>();
        foreach (var asset in pack.Assets)
        {
            var path = ImagePath(asset);
            using var image = SKBitmap.Decode(path) ?? throw new InvalidDataException("Cannot decode " + asset.Id);
            var pixels = image.Pixels;
            if (!pixels.Any(p => p.Alpha == 0) || !pixels.Any(p => p.Alpha == 255)
                || image.GetPixel(0, 0).Alpha != 0 || image.GetPixel(image.Width - 1, image.Height - 1).Alpha != 0)
                throw new InvalidDataException("Missing genuine transparent silhouette: " + asset.Id);
            var style = pack.Styles.Single(s => s.Id == asset.Style);
            var record = catalog.Import(path, new AssetImportOptions
            {
                Pack = "Горы · " + style.Name, Tags = asset.Tags,
                Provenance = new AssetProvenance
                {
                    Source = pack.ProvenanceSource ?? "map-ai-mountain-foundation-20260908",
                    Author = "Создано для владельца: " + (pack.GenerationTool ?? "imagegen") + "; маска в Photoshop",
                    LicenseId = "owner-generated-study", LicenseStatus = AssetLicenseStatus.UserOwned,
                    CommercialUseAllowed = false,
                },
            }, DateTimeOffset.UtcNow);
            result.Add(asset.Id, record);
            if (!originalHashes.Contains(record.Sha256))
                catalog.RenameSet(catalog.FindSetForContent(record.Sha256)!.Id, asset.Name);
        }
        if (result.Values.Select(r => r.Sha256).Distinct().Count() != pack.Assets.Length)
            throw new InvalidDataException("Pack images must be distinct original variants");

        // Keep all existing records and user metadata. Only group these newly imported collections.
        var library = AssetLibraryStore.Load(Path.Combine(catalog.Root, "catalog.json"), () => DateTimeOffset.UtcNow);
        var rootId = AssetIdentity.CollectionId("Горы · пробные стили");
        if (!library.Collections.Any(c => c.Id == rootId))
            library.Collections.Add(new AssetCollection { Id = rootId, Name = "Горы · пробные стили", Order = 0 });
        for (var i = 0; i < pack.Styles.Length; i++)
        {
            var id = AssetIdentity.CollectionId("Горы · " + pack.Styles[i].Name);
            var index = library.Collections.FindIndex(c => c.Id == id);
            if (index >= 0 && library.Collections[index].ParentId is null)
                library.Collections[index] = library.Collections[index] with { ParentId = rootId, Order = i };
        }
        catalog.ReplaceLibrary(library);
        return result;
    }

    internal static MountainInstallReport Install()
    {
        if (Process.GetProcessesByName("Map.App").Length != 0)
            throw new InvalidOperationException("Close Map before explicit pack import");
        if (Environment.GetEnvironmentVariable("MAP_UI_TEST_MODE") == "1")
            throw new InvalidOperationException("The owner import must not run in a test profile");
        var root = new AppSettingsStore().Load().LibraryRoot ?? Path.Combine(AppPaths.Root, "Assets");
        var catalog = new AssetCatalog(root);
        try
        {
            var before = catalog.Records.Count; var pack = Load(); var records = Import(catalog, pack);
            catalog.FlushMetadataAsync().GetAwaiter().GetResult();
            return new(true, catalog.Root, catalog.Records.Count - before, records.Count, pack.Styles.Length);
        }
        finally { catalog.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static MapAssetReference Reference(AssetRecord record, AssetCatalog catalog) => new(
        record.Sha256, record.DisplayName, record.MediaType, record.Extension, ProjectAssetStorage.Embedded,
        record.ImportedUtc, record.Provenance.Source, record.Provenance.LicenseId,
        File.ReadAllBytes(catalog.Resolve(record).Path!));

    internal static SceneObject Stamp(AssetRecord record, string name, double x, double y, double width)
    {
        var height = width * record.Height / record.Width;
        return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, name, x - width / 2, y - height / 2,
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(record.Sha256) { ReferenceWidth = width, ReferenceHeight = height, ShadowOpacity = 0 } };
    }

    internal static SceneObject Caption(string value, double x, double y, double width, double size = 28) => new(
        ObjectId.New(), SceneObjectKind.Text, value, x, y, width, size * 1.8, 0, "#FF41392E", "#00000000", 0, true, false)
    { Text = new TextStyle { Content = value, FontSize = size, FillColor = "#FF41392E", RequestedFontFamily = "Georgia" } };

    internal static MapDocument Template(MountainManifest pack, MountainStyle style, Dictionary<string, AssetRecord> records, AssetCatalog catalog)
    {
        var samples = pack.Assets.Where(a => a.Style == style.Id).ToArray();
        var first = records[samples[0].Id]; var second = records[samples[1].Id];
        var background = style.Id switch { "illustrated" => "#FFDDE2CC", "painted" => "#FFE0E5DD", _ => "#FFF0E6D2" };
        var layer = new ObjectLayer(LayerId.New(), "Горная цепь · узлы", true, false, 1, []);
        var objects = new ObjectLayer(LayerId.New(), "Отдельные формы", true, false, 1,
        [Stamp(second, samples[1].Name, 450, style.Id == "painted" ? 550 : 500, samples[1].Width * 1.3),
         Stamp(first, samples[0].Name, 875, 420, samples[0].Width),
         Stamp(second, samples[1].Name + " · малая группа", 790, 605, samples[1].Width * .72)]);
        var labels = new ObjectLayer(LayerId.New(), "Подписи", true, false, 1,
            [Caption(style.Name, 55, 30, 1080, 32), Caption("Горный перевал", 355, 352, 450, 23)]);
        var document = new MapDocument(DocumentId.New(), "Горы · " + style.Name, DateTimeOffset.UtcNow,
            new CanvasDefinition(1200, 800, "px", background),
            [new BaseColorLayer(LayerId.New(), "Цвет основы", true, false, 1, background), layer, objects, labels],
            [Reference(first, catalog), Reference(second, catalog)],
            [new MapNote(NoteId.New(), "Как продолжить горный шаблон",
                "Выберите путь «Горная цепь» и измените его узлы или шаг. Отдельные формы можно двигать и масштабировать. " +
                "Для новой карты используйте «Из шаблона» в библиотеке: исходник останется отдельным файлом. " +
                "Поворот и зеркальное отражение не нужны — сохраняйте направление света.", NoteAnchorKind.World)]);
        var path = new PlacementPath
        {
            Name = "Горная цепь", AssetSetId = catalog.FindSetForContent(first.Sha256)!.Id,
            VariantId = catalog.FindSetForContent(first.Sha256)!.CoverVariantId,
            Nodes = [new(Guid.NewGuid(), 160, 285), new(Guid.NewGuid(), 370, 185),
                new(Guid.NewGuid(), 570, 230), new(Guid.NewGuid(), 720, 310)],
            Variants = [new(first.Sha256, first.Width, first.Height)], Spacing = 138, Seed = 232408,
            Request = new StampPlacementRequest { BaseWidth = samples[0].Width, RandomAngleStep = StampRandomAngleStep.Off, RandomFlip = false },
            Style = new StampStyle(first.Sha256) { ShadowOpacity = 0 },
        };
        return new PlacementPathCommand(layer.Id, path.Id, path, "Горная цепь").Apply(document);
    }

    internal static MapDocument Overview(MountainManifest pack, Dictionary<string, AssetRecord> records, AssetCatalog catalog)
    {
        var columns = pack.Styles.Max(s => pack.Assets.Count(a => a.Style == s.Id));
        var canvasWidth = 70 + columns * 675;
        var canvasHeight = 140 + pack.Styles.Length * 405;
        var items = new List<SceneObject> { Caption(pack.Name ?? "ГОРЫ  /  ПЕРВЫЕ ЧЕТЫРЕ СТИЛЯ", 65, 32, canvasWidth - 130, 30) };
        for (var row = 0; row < pack.Styles.Length; row++)
        {
            var style = pack.Styles[row]; var samples = pack.Assets.Where(a => a.Style == style.Id).ToArray();
            items.Add(Caption(style.Name, 65, 115 + row * 405, 1100, 26));
            for (var col = 0; col < samples.Length; col++)
            {
                var asset = samples[col]; var record = records[asset.Id];
                var width = Math.Min(545, 280.0 * record.Width / record.Height);
                items.Add(Stamp(record, asset.Name, 370 + col * 675, 300 + row * 405, width));
                items.Add(Caption(asset.Name, 110 + col * 675, 460 + row * 405, 600, 21));
            }
        }
        return new MapDocument(DocumentId.New(), "Горы · сравнение пробных стилей", DateTimeOffset.UtcNow,
            new CanvasDefinition(canvasWidth, canvasHeight, "px", "#FFF4EFE3"),
            [new BaseColorLayer(LayerId.New(), "Основа", true, false, 1, "#FFF4EFE3"),
             new ObjectLayer(LayerId.New(), "Образцы и названия", true, false, 1, items)],
            records.Values.Select(r => Reference(r, catalog)).ToArray());
    }
}
