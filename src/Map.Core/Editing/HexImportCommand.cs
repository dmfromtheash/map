using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Replaces or adds a hex layer and grows the canvas in one reversible operation.</summary>
public sealed class HexImportCommand : IEditCommand
{
    private readonly MapDocument _before;
    private readonly MapDocument _after;
    public HexImportCommand(MapDocument before, HexLayer imported)
    {
        _before = before;
        var layers = before.Layers.ToList();
        var index = layers.FindIndex(l => l.Id == imported.Id);
        if (index >= 0) layers[index] = imported; else layers.Add(imported);
        var width = Math.Max(before.Canvas.Width, Math.Ceiling(imported.Board.PixelWidth));
        var height = Math.Max(before.Canvas.Height, Math.Ceiling(imported.Board.PixelHeight));
        if (width > CanvasDimensionPolicy.Maximum || height > CanvasDimensionPolicy.Maximum)
            throw new InvalidOperationException("Поле импорта превышает допустимый размер холста. Уменьшите размер гекса; карта не изменена.");
        var canvas = before.Canvas.Resized((int)width, (int)height);
        _after = new MapDocument(before.Id, before.Title, before.CreatedUtc, canvas, layers,
            before.Assets, before.Notes, before.Checkpoints, before.StampForeground, before.Category)
        { Atlas = before.Atlas, Lighting = before.Lighting, Style = before.Style, Entities = before.Entities, Labels = before.Labels };
        MeasuredWorkingBytes = EditCost.BaseBytes + (long)imported.Board.CellCount * 48;
    }
    public string Name => "Импорт JSON гексов";
    public long? MeasuredWorkingBytes { get; }
    public MapDocument Apply(MapDocument document) => _after;
    public MapDocument Revert(MapDocument document) => _before;
}
