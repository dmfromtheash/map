using System.Collections.Immutable;
using Map.Core.Editing;

namespace Map.Core.Documents;

public enum ExportRecipient { GameMaster, Players }

/// <summary>
/// One immutable choice of scene shared by picture, VTT and data exports. The source document
/// remains untouched. A null level includes all visible hex levels; a selected level never
/// includes another level. Player scenes contain only public hex data, including their render document.
/// </summary>
public sealed class ExportScene
{
    public ExportScene(MapDocument document, int? hexLevel = null,
        ExportRecipient recipient = ExportRecipient.GameMaster, CanvasRegion? region = null,
        IEnumerable<LayerId>? visibleLayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(recipient)) throw new ArgumentOutOfRangeException(nameof(recipient));
        HexLevel = hexLevel;
        Recipient = recipient;
        Region = region ?? new CanvasRegion(0, 0, document.Canvas.Width, document.Canvas.Height);
        var requested = visibleLayerIds?.ToImmutableHashSet();
        VisibleLayerIds = document.Layers.Where(layer => layer.Visible
            && (requested is null || requested.Contains(layer.Id))
            && (layer is not HexLayer hex || hexLevel is null || hex.Level == hexLevel))
            .Select(layer => layer.Id).ToImmutableHashSet();
        var selected = document.Layers.Where(layer => VisibleLayerIds.Contains(layer.Id)).ToList();
        Package = ExportPackagePreview.Create(document.Title, selected, Players);
        Document = Players ? document.WithLayers([.. document.Layers.Select(layer => layer is HexLayer hex
            ? ExportPlayerVisibility.Sanitize(hex) : layer)]) : document;
    }

    public MapDocument Document { get; }
    public MapCategoryId Category => Document.Category;
    public int? HexLevel { get; }
    public ExportRecipient Recipient { get; }
    public bool Players => Recipient == ExportRecipient.Players;
    public CanvasRegion Region { get; }
    public ImmutableHashSet<LayerId> VisibleLayerIds { get; }
    public ExportPackagePreview Package { get; }
    public IEnumerable<MapLayer> Layers => Document.Layers.Where(layer => VisibleLayerIds.Contains(layer.Id));

    /// <summary>Preserves mandatory layers and their order, hiding excluded layers for the renderer.</summary>
    public MapDocument RenderDocument() => Document.Layers.All(layer => layer.Visible == VisibleLayerIds.Contains(layer.Id))
        ? Document
        : Document.WithLayers([.. Document.Layers.Select(layer =>
            layer.Visible == VisibleLayerIds.Contains(layer.Id) ? layer : layer with { Visible = false })]);
}
