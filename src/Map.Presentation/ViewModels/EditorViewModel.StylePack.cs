using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;

namespace Map.Presentation.ViewModels;

/// <summary>R09d (DEC-094): a style pack sets the whole look of the map in one step that one Ctrl+Z takes back.</summary>
public sealed partial class EditorViewModel
{
    public static IReadOnlyList<MapStylePack> StylePacks => MapStylePack.Presets;

    /// <summary>The file of the owner's own pack in the profile (set by the application); null: no own pack (tests).</summary>
    public static string? OwnStylePackPath { get; set; }

    private string? _activeStylePackId;

    /// <summary>The pack the map was last put into while it still has that pack's style; null otherwise.</summary>
    public string? ActiveStylePackId =>
        _activeStylePackId is { } id && (id == MapStylePack.OwnId ? _ownStylePack : MapStylePack.Find(id)) is { } pack
        && Session.Document.Style == pack.Style.Normalized() ? id : null;

    /// <summary>Applies the pack; false when the map already looks so (nothing to undo).</summary>
    public bool ApplyStylePack(MapStylePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var before = Session.Document;
        var after = pack.ApplyTo(before);
        if (after.Layers.SequenceEqual(before.Layers) && after.Style == before.Style) return false;
        Session.Execute(new ApplyStylePackCommand(pack.Title, before, after));
        _activeStylePackId = pack.Id;
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        Raise(nameof(Style));
        StatusDetail = $"Пакет стиля «{pack.Title}»: стиль, обрывы, реки, дороги, море и подписи. Ctrl+Z вернёт прежний вид.";
        return true;
    }

    // ------------------------------------------------------------------ the owner's own pack

    private MapStylePack? _ownStylePack;
    private bool _ownStylePackRead;

    /// <summary>The owner's own pack saved in the profile, or null.</summary>
    public MapStylePack? OwnStylePack
    {
        get
        {
            if (_ownStylePackRead) return _ownStylePack;
            _ownStylePackRead = true;
            if (OwnStylePackPath is not { } path || !File.Exists(path)) return null;
            try { _ownStylePack = MapStylePack.From(_store.Open(path).Document, MapStylePack.OwnId, "Мой пакет"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException)
            { _log.Error("Own style pack could not be read", ex); }
            return _ownStylePack;
        }
    }

    /// <summary>
    /// Keeps the look of this map - its style, cliffs, rivers, roads, sea and names - as the owner's own pack in the profile.
    /// The pack is stored as a small map of its own, so every part of the look is saved the way a map saves it.
    /// </summary>
    public bool SaveOwnStylePack()
    {
        if (OwnStylePackPath is not { } path) return false;
        var pack = MapStylePack.From(Session.Document, MapStylePack.OwnId, "Мой пакет");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _store.Save(pack.ToTemplate(), path, new SaveMetadata(_appVersion, _clock()), TransparentPng);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Own style pack could not be saved", ex);
            StatusDetail = "Свой пакет не сохранён: " + ex.Message;
            return false;
        }
        _ownStylePack = pack; _ownStylePackRead = true;
        _activeStylePackId = pack.Id;
        Raise(nameof(OwnStylePack));
        StatusDetail = "Вид карты сохранён как «Мой пакет»: он применяется одной кнопкой на любой карте.";
        return true;
    }

    /// <summary>A 1×1 transparent PNG: the pack's file needs a preview image and shows none.</summary>
    private static readonly byte[] TransparentPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
