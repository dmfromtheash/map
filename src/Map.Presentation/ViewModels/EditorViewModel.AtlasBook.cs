using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // ------------------------------------------------------------------ the atlas book

    /// <summary>
    /// The maps of the atlas (or this map alone) in one PDF: contents, a page per map with its
    /// detail sheets outlined and referred to, the keys of notes and cards, and the index of names.
    /// The open map goes in as it is now, unsaved edits included.
    /// </summary>
    public async Task<int> ExportAtlasBookAsync(string path, bool players)
    {
        var current = Session.Document;
        var atlas = Atlas;
        var manifest = AtlasPath;
        try
        {
            return await Task.Run(() =>
            {
                var maps = new List<(Guid Id, MapDocument Document)>();
                if (atlas is not null && manifest is not null)
                {
                    foreach (var map in AtlasBook.Order(atlas))
                    {
                        if (map.Id == current.Id.Value) { maps.Add((map.Id, current)); continue; }
                        try { maps.Add((map.Id, MapProjectStore.Open(AtlasStore.ResolveMap(manifest, map)).Document)); }
                        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                        { _log.Error("ATLAS_BOOK_MAP_FAILED", ex); }
                    }
                }
                if (maps.Count == 0) maps.Add((current.Id.Value, current));
                using var stamps = new ExportStampImageSource(current, DocumentPath, _assetCatalog);
                return AtlasBook.Write(path, current.Title, maps, atlas, players, stamps);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось собрать книгу: " + ex.Message;
            _log.Error("ATLAS_BOOK_FAILED", ex);
            return 0;
        }
    }

    public async Task<int> PickAndExportAtlasBookAsync(bool players)
    {
        if (await _filePicker.PickSaveAsync(SafeExportName(Session.Document.Title) + (players ? " - атлас для игроков.pdf" : " - атлас.pdf")) is not { } path) return 0;
        var pages = await ExportAtlasBookAsync(path, players);
        if (pages > 0) StatusDetail = $"Книга-атлас собрана: {pages} стр. После правок соберите её снова — номера страниц и указатель обновятся.";
        return pages;
    }
}
