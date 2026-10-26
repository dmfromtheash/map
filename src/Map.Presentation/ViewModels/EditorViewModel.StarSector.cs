using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R33: a hex map as a star sector - the dark field and parsecs, a system per hex (UWP, bases,
/// remarks, zone, PBG, allegiance, stars, the master's notes) chosen by its four-digit number, and
/// the sector data in and out as text (T5 tab table and the older column «.sec»).
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The hex layer of the level shown; null on a map without hexes.</summary>
    public HexLayer? SectorLayer =>
        Session.Document.Layers.OfType<HexLayer>().FirstOrDefault(l => l.Level == (HexLevelShown ?? 0))
        ?? Session.Document.Layers.OfType<HexLayer>().FirstOrDefault();

    public IReadOnlyList<(string Number, StarSystem System)> SectorSystems =>
        SectorLayer is not { } layer ? []
        : [.. layer.Systems.Select(p => (Offset: layer.Board.Layout.ToOffset(p.Key), p.Value))
            .OrderBy(p => p.Offset.Column).ThenBy(p => p.Offset.Row).Select(p => (StarSector.Number(p.Offset), p.Value))];

    public StarSystem? SystemAt(string number) =>
        SectorLayer is { } layer && StarSector.Parse(number) is { } offset && layer.Systems.TryGetValue(layer.Board.Layout.FromOffset(offset), out var system) ? system : null;

    /// <summary>Puts (or with null removes) the system of a hex given by its number «CCRR».</summary>
    public bool SetSystem(string number, StarSystem? system)
    {
        if (SectorLayer is not { } layer) { StatusDetail = "Звёздный сектор — это гекс-карта."; return false; }
        if (StarSector.Parse(number) is not { } offset || !layer.Board.Contains(layer.Board.Layout.FromOffset(offset)))
        { StatusDetail = $"Гекса {number} нет на карте."; return false; }
        if (system is { IsValid: false }) { StatusDetail = "Проверьте UWP: 9 знаков вида A788899-C."; return false; }
        var hex = layer.Board.Layout.FromOffset(offset);
        var after = system is null ? layer.Systems.Remove(hex) : layer.Systems.SetItem(hex, system);
        if (after == layer.Systems) return false;
        Session.Execute(new SetHexSystemsCommand(layer.Id, layer.Systems, after, system is null ? "Удалить систему" : "Система " + number));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = system is null ? $"Система {number} удалена." : $"Система {number} «{system.Name}» сохранена.";
        return true;
    }

    /// <summary>The dark field of space and a parsec per hex.</summary>
    public void MakeStarSector()
    {
        if (SectorLayer is not { } layer) { StatusDetail = "Звёздный сектор — это гекс-карта: создайте её в мастерской гексов."; return; }
        Session.Execute(new MakeStarSectorCommand(layer.Id));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = "Оформлено как звёздный сектор: тёмное поле, гекс — парсек.";
    }

    public int ImportSectorData(string text)
    {
        if (SectorLayer is not { } layer) { StatusDetail = "Звёздный сектор — это гекс-карта."; return 0; }
        var entries = SecFormat.Read(text, out var problems);
        var builder = layer.Systems.ToBuilder();
        var count = 0;
        foreach (var (offset, system) in entries)
        {
            var hex = layer.Board.Layout.FromOffset(offset);
            if (!layer.Board.Contains(hex)) continue;
            builder[hex] = system; count++;
        }
        if (count == 0) { StatusDetail = "В файле нет систем, которые попадают на карту." + (problems.Count > 0 ? " " + problems[0] : ""); return 0; }
        Session.Execute(new SetHexSystemsCommand(layer.Id, layer.Systems, builder.ToImmutable(), "Импорт сектора"));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = $"Систем загружено: {count}." + (problems.Count > 0 ? $" Пропущено строк: {problems.Count}." : "");
        return count;
    }

    public string ExportSectorData() =>
        SectorLayer is not { } layer ? ""
        : SecFormat.Write(layer.Systems.Select(p => new SecFormat.Entry(layer.Board.Layout.ToOffset(p.Key), p.Value)));

    public async Task<int> PickAndImportSectorAsync()
    {
        if (await _filePicker.PickOpenAsync() is not { } path) return 0;
        try { return ImportSectorData(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusDetail = "Не удалось прочитать файл: " + ex.Message; return 0; }
    }

    public async Task<bool> PickAndExportSectorAsync()
    {
        if (await _filePicker.PickSaveAsync(SafeExportName(Session.Document.Title) + ".tab") is not { } path) return false;
        try { await File.WriteAllTextAsync(path, ExportSectorData()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusDetail = "Не удалось сохранить: " + ex.Message; return false; }
        StatusDetail = "Данные сектора сохранены (таблица T5, табуляция).";
        return true;
    }
}
