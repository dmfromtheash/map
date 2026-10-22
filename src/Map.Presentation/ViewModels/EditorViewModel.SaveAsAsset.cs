using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// «Сохранить выделенное как ассет» (R10, S12): the selected objects - a group whole - are drawn
/// into one transparent picture and added to the local catalog, which builds its preview. The map
/// itself does not change, so there is nothing to undo.
/// </summary>
public sealed partial class EditorViewModel
{
    public AssetRecord? SaveSelectionAsCatalogAsset(string? name = null)
    {
        if (_assetCatalog is null) { StatusDetail = "Локальный каталог ассетов недоступен."; return null; }
        var ids = SelectionWithWholeGroups();
        if (ids.Count == 0 || Session.SelectedObject is not { } primary) { StatusDetail = "Выделите объекты, из которых собрать ассет."; return null; }
        var title = SafeAssetName(string.IsNullOrWhiteSpace(name) ? primary.GroupName ?? primary.Name : name);
        var package = CompositionTransfer.Copy(Session.Document, ids, new CompositionCaptureOptions(), title);
        var source = new SceneObject(ObjectId.New(), SceneObjectKind.Composition, title, 0, 0,
            package.Composition.SourceWidth, package.Composition.SourceHeight, 0, "#00000000", "#00000000", 0, true, false)
        { Composition = package.Composition };
        var flattened = CompositionFlattener.Flatten(source, StampImages);
        var folder = Path.Combine(Path.GetTempPath(), "map-asset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, title + ".png");
            File.WriteAllBytes(path, flattened.Png);
            var imported = _assetCatalog.Import(path, new AssetImportOptions
            {
                Linked = false,
                Provenance = new AssetProvenance { Source = "собрано из объектов карты", LicenseStatus = AssetLicenseStatus.Unknown },
            }, _clock());
            Raise(nameof(Assets));
            StatusDetail = $"Ассет «{imported.DisplayName}» сохранён в каталог: {flattened.PixelWidth} × {flattened.PixelHeight} px.";
            return imported;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось сохранить ассет: " + ex.Message;
            _log.Error("Save selection as asset failed", ex);
            return null;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string SafeAssetName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Trim().Select(c => invalid.Contains(c) ? '_' : c)]).Trim('.', ' ');
        return cleaned.Length == 0 ? "Ассет" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
