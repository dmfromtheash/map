using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>R12: the style of the open sea, kept on the base colour layer; every change is one undo step.</summary>
public sealed partial class EditorViewModel
{
    private BaseColorLayer? Page => Session.Document.Layers.Count > 0 ? Session.Document.Layers[0] as BaseColorLayer : null;

    /// <summary>The sea style of the map, or null when the sea is the plain page colour.</summary>
    public SeaStyle? Sea => Page?.Sea;

    public static string SeaTitle(SeaManner manner) => SeaStyle.Manners.First(m => m.Manner == manner).Title;

    /// <summary>Applies a manner with its own colours; the strength and the reach chosen so far stay.</summary>
    public void ApplySeaManner(SeaManner manner)
    {
        var current = Sea;
        var next = SeaStyle.For(manner) with
        {
            Strength = current?.Strength ?? 1, Scale = current?.Scale ?? 1,
        };
        SetSea(next, "Море: " + SeaTitle(manner));
    }

    public void ClearSea() => SetSea(null, "Море без стиля");

    public void SetSeaStrength(double percent)
    {
        if (Sea is { } sea && double.IsFinite(percent)) SetSea(sea with { Strength = percent / 100 }, "Сила стиля моря");
    }

    public void SetSeaScale(double percent)
    {
        if (Sea is { } sea && double.IsFinite(percent)) SetSea(sea with { Scale = percent / 100 }, "Размах стиля моря");
    }

    private void SetSea(SeaStyle? sea, string name)
    {
        if (Page is not { } page) return;
        var bounded = sea?.Bounded();
        if (Equals(page.Sea, bounded)) return;
        Session.Execute(UpdateLayerCommand.Replace(page, page with { Sea = bounded }, name));
        Raise(nameof(Sea));
        AfterEdit();
    }
}
