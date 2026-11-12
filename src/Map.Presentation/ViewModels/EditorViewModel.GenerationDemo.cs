using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    /// <summary>
    /// The frames of a generation card on this map's page, ground and style - the area cards with the asset panel's chosen
    /// assets. Read on the UI thread; the returned work is pure and runs off it. Null when there is nothing real to show
    /// (an area card without a chosen asset).
    /// </summary>
    public Func<IReadOnlyList<GenerationDemoFrame>>? GenerationDemo(PanelDemoKind kind, out string signature)
    {
        var document = Session.Document;
        var background = document.Canvas.Background;
        var page = document.Layers.OfType<BaseColorLayer>().FirstOrDefault() is { } own
            ? own with { Id = LayerId.New() }
            : new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, background);
        var groundStyle = WithGroundTexture(LandLayer.CreateEmpty(LayerId.New(), "Земля").EdgeStyle, NewLandTextureId);
        var style = document.Style; var lighting = document.Lighting;
        if (kind.IsContinent())
        {
            signature = string.Join('|', "continent", background, NewLandTextureId, style?.GetHashCode(), lighting?.GetHashCode());
            var key = signature;
            return () =>
            {
                var land = Land(groundStyle, (x, y) => GenerationDemos.ContinentShape(x, y) > .5, GenerationDemos.ContinentWidth, GenerationDemos.ContinentHeight);
                var start = new MapDocument(DocumentId.New(), "Генератор материка", DateTimeOffset.UnixEpoch,
                    new CanvasDefinition(GenerationDemos.ContinentWidth, GenerationDemos.ContinentHeight, "px", background), [page, land])
                { Style = style, Lighting = lighting };
                return GenerationDemos.Continent(kind, start, land.Id, key);
            };
        }
        if (kind.IsArea())
        {
            // The chosen area's own assets; without an area, the asset panel's.
            IReadOnlyList<StampVariantSource> sources = SelectedEditableArea is { Variants.Length: > 0 } chosen ? [.. chosen.Variants]
                : TryStampInputs(_stampToolSettings, out _, out var panel, out _, out _) ? panel : [];
            signature = string.Join('|', "area", background, NewLandTextureId, style?.GetHashCode(), string.Join(',', sources.Select(s => s.Sha256)));
            if (sources.Count == 0) return null;
            // The area generator reads the assets' saved images from the document, as the real area's map has them.
            var shas = sources.Select(s => s.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assets = document.Assets.Where(a => shas.Contains(a.Sha256)).ToList();
            return () =>
            {
                var layer = new ObjectLayer(LayerId.New(), "Области", true, false, 1, []);
                var area = GenerationDemos.AreaStart(sources);
                var start = new MapDocument(DocumentId.New(), "Область", DateTimeOffset.UnixEpoch,
                    new CanvasDefinition(GenerationDemos.AreaWidth, GenerationDemos.AreaHeight, "px", background),
                    [page, Land(groundStyle, static (_, _) => true, GenerationDemos.AreaWidth, GenerationDemos.AreaHeight), layer], [.. assets])
                { Style = style, Lighting = lighting };
                start = new PlacementAreaCommand(layer.Id, area.Id, area, "Пример").Apply(start);
                return GenerationDemos.Area(kind, start, layer.Id);
            };
        }
        signature = "hex|" + background;
        var category = Services.MapCategories.Hex.Id;
        return () => GenerationDemos.Hex(kind, background, category);
    }
}
