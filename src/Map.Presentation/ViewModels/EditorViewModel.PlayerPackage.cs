using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // Preserve the master's whole-map JSON; every player format uses the explicit export level.
    private ExportScene HexDataExportScene() => new(Session.Document, ExportHexPlayers ? ExportHexLevel : null,
        ExportHexPlayers ? ExportRecipient.Players : ExportRecipient.GameMaster);

    public ExportPackagePreview PlayerPackagePreview =>
        new ExportScene(Session.Document, ExportHexLevel, ExportRecipient.Players).Package;
}
