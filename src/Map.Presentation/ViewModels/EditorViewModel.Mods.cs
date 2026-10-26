using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>R41: data mods - found in the Mods folder, checked, their assets into the catalog and their encounter tables into the hex map.</summary>
public sealed partial class EditorViewModel
{
    private string? _modsFolder;

    /// <summary>The folder of mods (the user's Map\Mods; tests set another).</summary>
    public string ModsFolder
    {
        get => _modsFolder ?? DataMods.DefaultFolder;
        set => _modsFolder = value;
    }

    public IReadOnlyList<DataMod> ScanMods() => DataMods.Scan(ModsFolder);

    public int ImportModAssets(DataMod mod)
    {
        if (_assetCatalog is null) { StatusDetail = "Каталог ассетов недоступен."; return 0; }
        if (!mod.IsUsable) { StatusDetail = "В моде ошибки: " + string.Join("; ", mod.Problems.Take(3)); return 0; }
        try
        {
            var count = DataMods.ImportAssets(mod, _assetCatalog, _clock());
            StatusDetail = $"«{mod.Name}»: ассетов в каталоге — {count}, коллекция «{mod.Name}». Её можно выбрать как художественный язык.";
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось подключить ассеты мода: " + ex.Message;
            _log.Error("MOD_ASSETS_FAILED", ex);
            return 0;
        }
    }

    /// <summary>The mod's encounter tables added to the hex layer shown (one undo step); a table for the same terrain is replaced.</summary>
    public int AddModEncounters(DataMod mod)
    {
        if (SectorLayer is not { } layer) { StatusDetail = "Таблицы встреч — для гекс-карты."; return 0; }
        if (!mod.IsUsable || mod.Encounters.Count == 0) { StatusDetail = "В моде нет годных таблиц встреч."; return 0; }
        var tables = layer.Journey.Encounters;
        foreach (var table in mod.Encounters)
        {
            var terrain = table.Terrain.Trim().ToLowerInvariant();
            tables = tables.RemoveAll(t => t.Terrain == terrain && t.Region == 0).Add(new EncounterTable(terrain, 0, [.. table.Entries.Select(e => e.Trim())]));
        }
        var after = layer with { Journey = layer.Journey with { Encounters = tables } };
        ExecuteHexLayerEdit("Таблицы встреч из мода", layer, after);
        StatusDetail = $"Таблиц встреч добавлено: {mod.Encounters.Count}.";
        return mod.Encounters.Count;
    }
}
