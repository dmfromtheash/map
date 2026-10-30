using Map.Core.Documents;
using Map.Core.Editing;
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
                // Each map of the book draws its pictures from its own file: embedded assets and the project folder.
                var files = new Dictionary<Guid, string?> { [current.Id.Value] = DocumentPath };
                if (atlas is not null && manifest is not null)
                {
                    foreach (var map in AtlasBook.Order(atlas))
                    {
                        if (map.Id == current.Id.Value) { maps.Add((map.Id, current)); continue; }
                        try
                        {
                            var file = AtlasStore.ResolveMap(manifest, map);
                            maps.Add((map.Id, MapProjectStore.Open(file).Document));
                            files[map.Id] = file;
                        }
                        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                        { _log.Error("ATLAS_BOOK_MAP_FAILED", ex); }
                    }
                }
                if (maps.Count == 0) maps.Add((current.Id.Value, current));
                LastBookHiddenByFog = players ? maps.Sum(m => PlayerScene.HiddenByFogCount(m.Document)) : 0;
                return AtlasBook.Write(path, current.Title, maps, atlas, players,
                    (id, document) => new ExportStampImageSource(document, files.GetValueOrDefault(id), _assetCatalog));
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
        if (pages > 0) StatusDetail = $"Книга-атлас собрана: {pages} стр."
            + (players ? $" Скрыто туманом: {LastBookHiddenByFog} {CardsWord(LastBookHiddenByFog)}." : "")
            + " После правок соберите её снова — номера страниц и указатель обновятся.";
        return pages;
    }

    /// <summary>DEC-077: the cards for players the last players' book left out because their places are under the fog.</summary>
    public int LastBookHiddenByFog { get; private set; }

    /// <summary>The cards with «Видно игрокам» of this map that the fog keeps from the players now.</summary>
    public int CardsHiddenByFog => PlayerScene.HiddenByFogCount(Session.Document);

    public static string CardsWord(int count) => (count % 100) is >= 11 and <= 14 ? "карточек"
        : (count % 10) switch { 1 => "карточка", 2 or 3 or 4 => "карточки", _ => "карточек" };
}
