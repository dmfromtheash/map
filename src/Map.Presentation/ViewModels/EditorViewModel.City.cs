using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R34/R35: the city workshop - «Перестроить город» with its settings (one undo step, the hand-made
/// layers stay), and on any map «Город из точки»: a click makes a child city map in the atlas
/// around that place, its main streets along the roads coming in, the parent's rivers and roads
/// carried in, the town's name written on the parent.
/// </summary>
public sealed partial class EditorViewModel
{
    private bool _cityPick;
    private CitySettings? _citySettings;

    public bool IsCityWorkshop => MapCategories.IsCity(Session.Document.Category);

    public CitySettings CitySettings
    {
        // A city map is named after its title until the user names it otherwise.
        get => _citySettings ??= new CitySettings { Name = IsCityWorkshop ? Session.Document.Title : "Город" };
        set { _citySettings = value; Raise(nameof(CitySettings)); }
    }

    public bool CityPickArmed => _cityPick;

    /// <summary>The city generated again on its «Город» layer and its key notes; everything else stays.</summary>
    public bool RegenerateCity()
    {
        if (!CitySettings.IsValid) { StatusDetail = "Проверьте параметры города."; return false; }
        var document = Session.Document;
        var size = Math.Min(document.Canvas.Width, document.Canvas.Height);
        // A city on a shore keeps to its land when rebuilt.
        var land = TerrainOrder.Lands(document).Where(l => l.Visible).ToList();
        Func<double, double, bool>? isLand = land.Count == 0 ? null : (x, y) => land.Any(l => TerrainOrder.CoverageAt(l, x, y) >= 128);
        var plan = CityBuilder.Generate(document.Canvas.Width / 2.0, document.Canvas.Height / 2.0, CitySettings with { Radius = Math.Min(CitySettings.Radius, size * .42), IsLand = isLand });
        var layer = document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.Name == "Город");
        var after = (layer ?? new ObjectLayer(LayerId.New(), "Город", true, false, 1, [])) with { Objects = [.. plan.Objects], Roads = [.. plan.Streets] };
        var keyNotes = plan.Key.Select(CityBuilder.KeyNote).ToArray();
        var marked = document.Notes.Any(n => n.Origin == NoteOrigin.CityKey);
        var notes = document.Notes.Where(n => !IsKeyNote(n, marked)).Concat(keyNotes).ToArray();
        Session.Execute(new ReplaceCityCommand(layer, after, document.Notes, notes));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = $"Город перестроен: улиц {plan.Streets.Count}, кварталов {plan.Blocks.Count}, домов {plan.Houses}. Ctrl+Z вернёт прежний.";
        return true;
    }

    /// <summary>
    /// A note of the generator's key (R35b): marked so. A city saved before the mark (no marked note at all)
    /// falls back to the old rule - an empty note titled «N. …» - once; the rebuild marks its new key.
    /// </summary>
    private static bool IsKeyNote(MapNote note, bool marked) => marked
        ? note.Origin == NoteOrigin.CityKey
        : note.Markdown.Length == 0 && note.Title.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4 && char.IsDigit(note.Title[0]);

    /// <summary>The next click on the map makes a city there (a child map in the atlas).</summary>
    public void StartCityFromPoint()
    {
        if (DocumentPath is null) { StatusDetail = "Сначала сохраните карту: город станет её подкартой в атласе."; return; }
        _cityPick = true;
        StatusDetail = "Щёлкните на карте место города. Esc — отмена.";
        Raise(nameof(CityPickArmed));
    }

    public void CancelCityPick()
    {
        if (!_cityPick) return;
        _cityPick = false;
        Raise(nameof(CityPickArmed));
    }

    public bool PressCityPick(double x, double y)
    {
        if (!_cityPick) return false;
        _cityPick = false;
        Raise(nameof(CityPickArmed));
        _ = CreateCityAtAsync(x, y);
        return true;
    }

    /// <summary>
    /// A child city map around (x, y): the area is a square of about a tenth of the shorter side,
    /// the main streets go out along the parent's roads that cross its edge, the parent's rivers and
    /// roads are carried in (R28), and the town's name is put on the parent at the place.
    /// </summary>
    public async Task<string?> CreateCityAtAsync(double x, double y, string? childPath = null)
    {
        var parent = Session.Document;
        if (DocumentPath is null) { StatusDetail = "Сначала сохраните карту."; return null; }
        var side = Math.Max(256, Math.Min(parent.Canvas.Width, parent.Canvas.Height) * .1);
        var region = new AtlasTransform(Math.Clamp(x - (side / 2), 0, parent.Canvas.Width - side), Math.Clamp(y - (side / 2), 0, parent.Canvas.Height - side), side, side, 2400, 2400);
        var area = new WorldBounds(region.X, region.Y, region.X + side, region.Y + side);
        var (cx, cy) = (region.X + (side / 2), region.Y + (side / 2));
        // Where roads cross into the area, as directions from its middle.
        var entries = parent.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads)
            .SelectMany(r => ScaleSync.Clip([.. r.Nodes.Select(n => new VectorPoint(n.X, n.Y))], area).SelectMany(p => new[] { p[0], p[^1] }))
            .Where(p => Math.Abs(p.X - area.Left) < 1 || Math.Abs(p.X - area.Right) < 1 || Math.Abs(p.Y - area.Top) < 1 || Math.Abs(p.Y - area.Bottom) < 1)
            .Select(p => Math.Atan2(p.Y - cy, p.X - cx)).Take(8).ToList();
        var name = EntityNameGenerator.PlaceName(parent, LabelPreset.City, (int)(StableSeed(Guid.NewGuid()) & 0x7FFF));
        // R35: the shore of the region comes in as the city's land; the town keeps to it, the port goes to the water.
        var coast = await Task.Run(() => ScaleSync.Coast(parent, region, 2400, 2400));
        var hasSea = coast is not null && Enumerable.Range(0, 24).Any(i => TerrainOrder.CoverageAt(coast, 100 + (i % 6 * 440), 100 + (i / 6 * 700)) < 128);
        var sea = parent.Layers.OfType<BaseColorLayer>().FirstOrDefault()?.Color;
        var settings = CitySettings with { Name = name, Seed = (int)(StableSeed(Guid.NewGuid()) & 0x7FFF), EntryDirections = entries, Radius = 900, Port = CitySettings.Port || hasSea };
        var child = CityBuilder.NewCityMap(name, 2400, settings, new MapCategoryId(MapCategories.CityValue), _clock(), hasSea ? coast : null, sea);
        child = ScaleSync.Apply(child, ScaleSync.Plan(parent, child, region));
        childPath ??= Path.Combine(Path.GetDirectoryName(DocumentPath)!, SafeProjectFileName(name) + MapProjectFormat.Extension);
        if (File.Exists(childPath)) childPath = Path.Combine(Path.GetDirectoryName(childPath)!, SafeProjectFileName(name) + "-" + Guid.NewGuid().ToString("N")[..6] + MapProjectFormat.Extension);
        try
        {
            var preview = await Task.Run(() => ThumbnailRenderer.RenderPng(child));
            await SaveAtlasChildAsync(parent, child, childPath, preview, region, "region");
        }
        catch (Exception ex) when (ex is MapProjectSaveException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusDetail = "Не удалось создать город: " + ex.Message;
            _log.Error("CITY_FROM_POINT_FAILED", ex);
            return null;
        }
        // The town's name on the parent, where it was clicked.
        if (LabelLayer() is { } labels)
        {
            var style = LabelPresets.Apply(new TextStyle { Content = name, Alignment = TextAlignment.Center }, LabelPreset.City);
            var (w, h) = RegionLabels.Size(style);
            Session.Execute(new AddObjectCommand(labels.Id, new SceneObject(ObjectId.New(), SceneObjectKind.Text, name, x - (w / 2), y - h - 6, w, h, 0, "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style }));
            AfterEdit();
        }
        await RefreshAtlasAsync();
        StatusDetail = $"Город «{name}» создан подкартой: {Path.GetFileName(childPath)}. Главные улицы идут вдоль дорог региона.";
        return childPath;
    }
}
