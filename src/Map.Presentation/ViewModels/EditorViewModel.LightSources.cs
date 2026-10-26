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
