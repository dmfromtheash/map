using Map.Assets;
using Map.Core.Documents;
using Map.Persistence;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>One tiling texture of the asset library offered as a fill.</summary>
public sealed record LibraryFillTexture(string Id, string Sha256, string Title, MapFillTextureKind Kind, string Group, bool Floor, bool Paint);

/// <summary>
/// Which library pictures are tiling textures, and for what. A card (set) or its picture carries
/// one of the tags <c>Текстура/Земля</c>, <c>Текстура/Вода</c>, <c>Текстура/Пол</c>,
/// <c>Текстура/Краска</c> or just <c>Текстура</c>: water ones are offered to rivers, lakes and the
/// sea, every other one as ground (land, floors, roads, the land brush). Cards of the user's own
/// library form «Мои текстуры»; cards of a pack or a drive library are grouped by their pack.
/// </summary>
public static class LibraryFillTextures
{
    public const string Tag = "Текстура";
    public const string GroundTag = "Текстура/Земля";
    public const string WaterTag = "Текстура/Вода";
    public const string FloorTag = "Текстура/Пол";
    public const string PaintTag = "Текстура/Краска";
    private const string OwnPack = "Мои ассеты";
    private static readonly string[] Drawable = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>The kind the tags give, or null when they do not mark a texture.</summary>
    public static MapFillTextureKind? KindOf(IEnumerable<string> tags, out bool floor, out bool paint)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var marks = tags.Select(tag => tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        floor = marks.Contains(FloorTag);
        paint = marks.Contains(PaintTag);
        if (marks.Contains(WaterTag)) return MapFillTextureKind.Water;
        return floor || paint || marks.Contains(GroundTag) || marks.Contains(Tag) ? MapFillTextureKind.Ground : null;
    }

    /// <summary>Every texture of the library (and of the drive libraries attached to it), each picture once.</summary>
    public static IReadOnlyList<LibraryFillTexture> List(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new List<LibraryFillTexture>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in catalog.Sets.Where(set => !set.Archived))
        {
            var single = set.VariantIds.Count == 1;
            foreach (var variantId in set.VariantIds)
            {
                if (catalog.FindVariant(variantId) is not { } variant || catalog.Find(variant.Sha256) is not { } record
                    || !Drawable.Contains(record.MediaType, StringComparer.OrdinalIgnoreCase)
                    || KindOf(set.Tags.Concat(record.Tags), out var floor, out var paint) is not { } kind
                    || !seen.Add(record.Sha256)) continue;
                var group = catalog.MountOfSet(set.Id) is null && record.Pack == OwnPack ? MapFillTextures.LibraryGroup : record.Pack;
                if (floor) group += " · пол";
                var title = single || string.IsNullOrWhiteSpace(variant.Label) ? set.Name : set.Name + " · " + variant.Label;
                result.Add(new LibraryFillTexture(MapFillTextures.AssetId(record.Sha256), record.Sha256, title, kind, group, floor, paint));
            }
        }
        return result;
    }

    // One rendering source for every open editor; an editor that is gone simply stops answering.
    private static readonly object Gate = new();
    private static readonly List<WeakReference<EditorViewModel>> Editors = [];
    private static IDisposable? _registration;

    internal static void Attach(EditorViewModel editor)
    {
        lock (Gate)
        {
            Editors.RemoveAll(reference => !reference.TryGetTarget(out _));
            Editors.Add(new WeakReference<EditorViewModel>(editor));
            _registration ??= MapTextureLibrary.AddAssetSource(Bytes);
        }
        MapTextureLibrary.RetryMissingAssets();
    }

    private static byte[]? Bytes(string sha256)
    {
        EditorViewModel[] editors;
        lock (Gate) editors = [.. Editors.Select(reference => reference.TryGetTarget(out var editor) ? editor : null).OfType<EditorViewModel>()];
        foreach (var editor in editors)
            if (editor.FillTextureBytes(sha256) is { } bytes) return bytes;
        return null;
    }
}

public sealed partial class EditorViewModel
{
    private IReadOnlyList<LibraryFillTexture> _libraryTextures = [];
    private (long Metadata, long Content, int Mounts) _libraryTexturesRevision = (-1, -1, -1);

    /// <summary>Tiling textures of the asset library (and attached drive libraries), after the built-in ones in the pickers.</summary>
    public IReadOnlyList<LibraryFillTexture> LibraryTextures
    {
        get
        {
            if (_assetCatalog is not { } catalog) return [];
            var revision = (catalog.MetadataRevision, catalog.ContentRevision, catalog.Mounts.Count);
            if (revision != _libraryTexturesRevision)
            {
                _libraryTextures = LibraryFillTextures.List(catalog);
                _libraryTexturesRevision = revision;
            }
            return _libraryTextures;
        }
    }

    /// <summary>
    /// What a fill picker of this kind offers: Map's own materials that have pixels, then the
    /// library textures of that kind (grouped «Мои текстуры» or by pack).
    /// </summary>
    public IReadOnlyList<MapFillTexture> FillTextureChoices(MapFillTextureKind kind) =>
    [
        .. MapFillTextures.Of(kind).Where(texture => MapTextureLibrary.Image(texture.Id) is not null),
        .. LibraryTextures.Where(texture => texture.Kind == kind)
            .Select(texture => new MapFillTexture(texture.Id, texture.Title, texture.Kind, texture.Group)),
    ];

    /// <summary>The name of a fill texture: the library card, the picture the map carries, or the built-in title.</summary>
    public string? FillTextureTitle(string? id)
    {
        if (MapFillTextures.AssetSha256(id) is not { } sha) return MapFillTextures.Find(id)?.Title;
        return LibraryTextures.FirstOrDefault(texture => texture.Sha256 == sha)?.Title
            ?? Session.Document.Assets.FirstOrDefault(asset => asset.Sha256 == sha)?.DisplayName
            ?? MapFillTextures.LibraryTitle;
    }

    /// <summary>The bytes of a library texture: the copy the map carries first, then the library (or its drive).</summary>
    public byte[]? FillTextureBytes(string sha256)
    {
        if (Session.Document.Assets.FirstOrDefault(asset => asset.Sha256 == sha256)?.EmbeddedContent is { } embedded) return embedded;
        if (_assetCatalog?.Find(sha256) is not { } record) return null;
        var resolution = _assetCatalog.ReadVerified(record);
        return resolution.Status == AssetResolutionStatus.Available ? resolution.Content : null;
    }

    /// <summary>
    /// The embedded asset the session copies into the map when an edit starts using a library
    /// texture; null when the library cannot supply it or the map has no room for it.
    /// </summary>
    private MapAssetReference? FillTextureAsset(string sha256)
    {
        if (_assetCatalog?.Find(sha256) is not { } record || _assetResolver?.Resolve(record) is not { IsAvailable: true } resolution) return null;
        // The map names the texture as the library card does, so the legend and the pickers say the
        // same thing without the library.
        var reference = LibraryTextures.FirstOrDefault(texture => texture.Sha256 == record.Sha256) is { } texture
            ? resolution.Reference! with { DisplayName = texture.Title }
            : resolution.Reference!;
        if (!MapProjectStore.CanAdmitAdditionalAssets(Session.Document, [reference], out var refusal))
        {
            _log.Info("LIBRARY_TEXTURE_NOT_EMBEDDED " + refusal);
            StatusDetail = "Текстура не сохранена в карте: " + refusal;
            return null;
        }
        return reference;
    }

    private void AttachLibraryTextures()
    {
        Session.FillTextureAssetSource = FillTextureAsset;
        LibraryFillTextures.Attach(this);
        if (_assetCatalog is { } catalog) catalog.MountsChanged += (_, _) => MapTextureLibrary.RetryMissingAssets();
    }
}
