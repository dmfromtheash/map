using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>Selection and undoable gameplay properties of a hex path; the document owns the flags.</summary>
public sealed partial class EditorViewModel
{
    private HexPathId? _selectedHexPathId;

    public HexPath? SelectedHexPath => _selectedHexPathId is { } id ? ActiveHexLayer?.Path(id) : null;
    public bool CanEditSelectedHexPath => SelectedHexPath is not null && ActiveHexLayer is { Locked: false };

    public void SelectHexPath(HexPathId id)
    {
        if (ActiveHexLayer?.Path(id) is null || _selectedHexPathId == id) return;
        _selectedHexPathId = id;
        Raise(nameof(SelectedHexPath));
    }

    public bool SetSelectedHexPathBlocking(bool? sight = null, bool? move = null, bool? light = null)
    {
        if (ActiveHexLayer is not { Locked: false } layer || SelectedHexPath is not { } before) return false;
        var after = before with
        {
            BlocksSight = sight ?? before.BlocksSight,
            BlocksMove = move ?? before.BlocksMove,
            BlocksLight = light ?? before.BlocksLight,
        };
        if (before.SameAs(after)) return false;
        ExecuteHexLayerEdit("Свойства блокировки пути", layer, layer with { Paths = layer.Paths.Replace(before, after) });
        Raise(nameof(SelectedHexPath));
        Raise(nameof(HexPaths));
        Raise(nameof(ExportVttSummary));
        return true;
    }
}
