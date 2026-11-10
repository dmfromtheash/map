using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R14: one light source of the map as an object with its own settings - colour, radius, the dim
/// zone around it, brightness and flicker. Every finished change is one undo step; a dragged
/// slider previews and commits on release, like the other light settings.
/// </summary>
public sealed partial class EditorViewModel
{
    private Guid? _selectedLightId;

    /// <summary>The light chosen in the list of the Свет tab, or null.</summary>
    public MapLightSource? SelectedLight =>
        _selectedLightId is { } id ? ShownLighting.Sources.FirstOrDefault(s => s.Id == id) : null;

    public void SelectLight(Guid? id)
    {
        _selectedLightId = id is { } wanted && ShownLighting.Sources.Any(s => s.Id == wanted) ? wanted : null;
        Raise(nameof(SelectedLight));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// True while the settings of Свет are open: the canvas marks every light of the map, the chosen one brighter,
    /// and a left click on a mark chooses that light instead of placing a new one or acting with the tool.
    /// </summary>
    public bool LightMarkersShown { get; private set; }

    public void ShowLightMarkers(bool shown)
    {
        if (LightMarkersShown == shown) return;
        LightMarkersShown = shown;
        Raise(nameof(LightMarkersShown));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The light whose mark is nearest to a map point, within <paramref name="reach"/> map units; null if none.</summary>
    public MapLightSource? LightAt(double x, double y, double reach)
    {
        MapLightSource? best = null;
        var bestDistance = double.MaxValue;
        foreach (var source in ShownLighting.Sources)
        {
            var distance = Math.Sqrt((source.X - x) * (source.X - x) + (source.Y - y) * (source.Y - y));
            if (distance <= reach && distance < bestDistance) (best, bestDistance) = (source, distance);
        }
        return best;
    }

    /// <summary>A click on the canvas with the marks shown: chooses the light under it. False when no mark is there.</summary>
    public bool SelectLightAt(double x, double y, double reach)
    {
        if (!LightMarkersShown || LightAt(x, y, reach) is not { } hit) return false;
        SelectLight(hit.Id);
        return true;
    }

    private void ChangeSelectedLight(string name, Func<MapLightSource, MapLightSource> change, bool commit)
    {
        if (_selectedLightId is not { } id) return;
        ChangeLighting(name, l => l with { Sources = [.. l.Sources.Select(s => s.Id == id ? change(s).Normalized() : s)] }, commit);
        Raise(nameof(SelectedLight));
    }

    public void SetSelectedLightColor(string color)
    {
        if (AppearanceColor.Normalize(color) is { } normalized) ChangeSelectedLight("Цвет огня", s => s with { Color = normalized }, true);
    }

    public void SetSelectedLightRadius(double radius, bool commit) =>
        ChangeSelectedLight("Радиус огня", s => s with { Radius = radius }, commit);

    /// <summary>The dim zone in percent of the radius (0..200).</summary>
    public void SetSelectedLightDimZone(double percent, bool commit) =>
        ChangeSelectedLight("Тусклая зона огня", s => s with { DimZone = percent / 100 }, commit);

    /// <summary>Brightness in percent (0..200).</summary>
    public void SetSelectedLightIntensity(double percent, bool commit) =>
        ChangeSelectedLight("Яркость огня", s => s with { Intensity = percent / 100 }, commit);

    public void SetSelectedLightFlicker(bool flicker) =>
        ChangeSelectedLight(flicker ? "Огонь мерцает" : "Огонь ровный", s => s with { Flicker = flicker }, true);
}
