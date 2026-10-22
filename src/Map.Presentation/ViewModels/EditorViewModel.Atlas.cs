using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Persistence;
using Map.Rendering;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public sealed record AtlasChild(AtlasMap Map, AtlasLink Link, byte[]? Preview);

public sealed partial class EditorViewModel
{
    // Storage metadata, like DocumentPath: registering an atlas does not add a scene edit.
    private AtlasLocator? _atlasLocator;
    public MapAtlas? Atlas { get; private set; }
    public string? AtlasPath { get; private set; }
    public string AtlasMessage { get; private set; } = "";
    public ImmutableArray<AtlasChild> AtlasChildren { get; private set; } = [];
    public ImmutableArray<AtlasMap> AtlasBreadcrumbs => Atlas?.Breadcrumbs(Session.Document.Id.Value) ?? [];
    public event EventHandler<string>? AtlasMapRequested;

    private MapDocument AtlasSnapshot(MapDocument document, string? target = null)
    {
        var locator = _atlasLocator ?? document.Atlas;
        if (locator is null) return document;
        var original = AtlasPath ?? (DocumentPath is null ? null : AtlasStore.ManifestPath(document with { Atlas = locator }, DocumentPath));
        return document with { Atlas = original is null || target is null ? locator : locator with
        { File = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(target))!, original) } };
    }

    public async Task RefreshAtlasAsync()
    {
        if (DocumentPath is null) return;
        try
        {
            var path = AtlasStore.ManifestPath(AtlasSnapshot(Session.Document), DocumentPath);
            AtlasPath = path;
            var data = await Task.Run(() =>
            {
                AtlasStore.Recover(path);
                if (!File.Exists(path)) return (Atlas: (MapAtlas?)null, Children: ImmutableArray<AtlasChild>.Empty);
                var atlas = AtlasStore.Read(path);
                if ((_atlasLocator ?? Session.Document.Atlas) is { } locator && atlas.Id != locator.Id)
                    throw new InvalidDataException("Идентификатор атласа не совпадает с картой.");
                var children = atlas.Links.Where(l => l.Parent == Session.Document.Id.Value).Select(link =>
                {
                    var map = atlas.Maps.Single(m => m.Id == link.Child); byte[]? preview = null;
                    try { preview = MapLibrary.ReadSummary(AtlasStore.ResolveMap(path, map)).PreviewPng; }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
                    return new AtlasChild(map, link, preview);
                }).ToImmutableArray();
                return (Atlas: (MapAtlas?)atlas, Children: children);
            });
            Atlas = data.Atlas; AtlasChildren = data.Children;
            if (Atlas is not null) _atlasLocator = new(Atlas.Id, Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(DocumentPath))!, path));
            AtlasMessage = Atlas is null ? ((_atlasLocator ?? Session.Document.Atlas) is null ? "У карты пока нет подкарты." : "Файл атласа не найден. Верните его рядом с картами.") : "Карт в атласе: " + Atlas.Maps.Length;
        }
        catch (Exception ex) when (ex is InvalidDataException or MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { Atlas = null; AtlasChildren = []; AtlasMessage = "Не удалось открыть атлас. Проверьте его формат и доступ к папке. Сохранённые копии операции оставлены для восстановления."; _log.Error("ATLAS_OPEN_FAILED", ex); }
        Raise(nameof(Atlas)); Raise(nameof(AtlasChildren)); Raise(nameof(AtlasBreadcrumbs)); Raise(nameof(AtlasMessage));
    }

    public void OpenAtlasMap(Guid id)
    {
        if (AtlasPath is null || Atlas?.Maps.FirstOrDefault(m => m.Id == id) is not { } map) return;
        try
        {
            var path = AtlasStore.ResolveMap(AtlasPath, map);
            if (!File.Exists(path)) { StatusDetail = "Подкарта не найдена: " + map.Title + ". Откройте проверку атласа."; return; }
            var document = _store.Open(path).Document;
            if (document.Id.Value != id || document.Atlas?.Id != Atlas.Id) { StatusDetail = "По этому пути находится другая карта или карта другого атласа. Откройте проверку атласа."; return; }
            AtlasMapRequested?.Invoke(this, path);
        }
        catch (Exception ex) when (ex is InvalidDataException or MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { StatusDetail = "Не удалось открыть карту атласа. Проверьте файл через проверку атласа."; _log.Error("ATLAS_MAP_OPEN_FAILED", ex); }
    }

    public async Task<ImmutableArray<AtlasIssue>> CheckAtlasAsync()
    {
        await RefreshAtlasAsync();
        if (AtlasPath is null || Atlas is null) return [new("manifest", Session.Document.Id.Value, AtlasMessage, "Вернуть файл атласа из резервной копии.")];
        try { return await Task.Run(() => AtlasStore.Inspect(AtlasPath)); }
        catch (Exception ex) when (ex is InvalidDataException or MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { _log.Error("ATLAS_CHECK_FAILED", ex); return [new("manifest", Session.Document.Id.Value, "Не удалось проверить атлас. Файл повреждён или недоступен.", "Проверить доступ к папке или вернуть файл атласа из резервной копии.")]; }
    }

    private async Task<AtlasLinkedResult> SaveAtlasChildAsync(MapDocument parent, MapDocument child, string childPath, byte[] preview, AtlasTransform transform, string kind)
    {
        if (DocumentPath is null) throw new InvalidOperationException("Сначала сохраните родительскую карту.");
        var sourcePath = DocumentPath;
        var snapshot = AtlasSnapshot(parent);
        using var images = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
        var metadata = new SaveMetadata(_appVersion, _clock(), Session.Revision);
        await _saveGate.WaitAsync();
        try
        {
            var result = await RunResourceOperationAsync(() => Task.Run(() => _store.SaveLinked(
                new(snapshot, sourcePath, metadata, ThumbnailRenderer.RenderPng(snapshot, stamps: images)),
                new(child, childPath, new(_appVersion, metadata.ModifiedUtc, 0), preview), transform, kind)),
                reserveBytes: Math.Max(DocumentOperationBytes(snapshot), DocumentOperationBytes(child)));
            _atlasLocator = result.Parent.Atlas; AtlasPath = result.ManifestPath;
            return result;
        }
        finally { _saveGate.Release(); }
    }
}
