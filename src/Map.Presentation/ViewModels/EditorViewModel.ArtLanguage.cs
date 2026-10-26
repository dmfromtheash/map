using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>What a change of art language would do, before it is done.</summary>
public sealed record ArtLanguagePlan(int Replaced, int WithoutRole, int WithoutCandidate, int InConstructions, IReadOnlyList<string> Roles);

/// <summary>
/// R40: «Сменить художественный язык» - every free stamp of a role (tree, spruce, bush, peak,
/// slope, house, tower…) is drawn again with an asset of the same role from another collection,
/// keeping its place, size and turn; the structure of the map stays. One undo step. Stamps of
/// paths and areas are counted, not changed (their constructions own them). And «Книга-атлас»:
/// the maps of the atlas in one PDF with contents, detail sheets, keys and an index of names.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The collections of the catalog that hold assets with a role (the art languages).</summary>
    public IReadOnlyList<AssetCollection> ArtLanguages =>
        _assetCatalog is not { } catalog ? []
        : [.. catalog.Collections.Where(c => catalog.Sets.Any(s => s.CollectionId == c.Id && s.Placement?.Role is not null and not AssetRole.None))
            .OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCulture)];

    private Dictionary<AssetRole, List<(AssetRecord Record, AssetVariant Variant)>> LanguageCandidates(string collectionId)
    {
        var result = new Dictionary<AssetRole, List<(AssetRecord, AssetVariant)>>();
        if (_assetCatalog is not { } catalog) return result;
        foreach (var set in catalog.Sets.Where(s => s.CollectionId == collectionId && !s.Archived && s.Placement?.Role is { } r && r != AssetRole.None))
            foreach (var variantId in set.VariantIds)
                if (catalog.FindVariant(variantId) is { } variant && catalog.Find(variant.Sha256) is { } record)
                {
                    if (!result.TryGetValue(set.Placement!.Role!.Value, out var list)) result[set.Placement.Role.Value] = list = [];
                    list.Add((record, variant));
                }
        return result;
    }

    private (List<(SceneObject Stamp, AssetRecord Record, AssetVariant Variant)> Moves, ArtLanguagePlan Plan) PlanArtLanguage(string collectionId)
    {
        var candidates = LanguageCandidates(collectionId);
        var moves = new List<(SceneObject, AssetRecord, AssetVariant)>();
        int withoutRole = 0, withoutCandidate = 0, owned = 0;
        foreach (var item in Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects))
        {
            if (item.Stamp is not { } stamp) continue;
            var set = _assetCatalog?.FindSetForContent(stamp.AssetSha256);
            if (set?.Placement?.Role is not { } role || role == AssetRole.None) { withoutRole++; continue; }
            if (set.CollectionId == collectionId) continue;
            if (!candidates.TryGetValue(role, out var list) || list.Count == 0) { withoutCandidate++; continue; }
            if (PlacementOwnership.Owner(Session.Document, item.Id) is not null) { owned++; continue; }
            // The same object always gets the same replacement: the choice follows its id.
            var pick = list[(int)(StableSeed(item.Id.Value) % (uint)list.Count)];
            moves.Add((item, pick.Item1, pick.Item2));
        }
        return (moves, new ArtLanguagePlan(moves.Count, withoutRole, withoutCandidate, owned,
            [.. candidates.Keys.Select(RoleTitle).OrderBy(t => t, StringComparer.CurrentCulture)]));
    }

    public ArtLanguagePlan PreviewArtLanguage(string collectionId) => PlanArtLanguage(collectionId).Plan;

    /// <param name="style">R40: a style applied in the same undo step - the language and its look chosen at once.</param>
    public ArtLanguagePlan ChangeArtLanguage(string collectionId, MapStylePreset? style = null)
    {
        var (moves, plan) = PlanArtLanguage(collectionId);
        if (moves.Count == 0 && style is null) { StatusDetail = "Заменять нечего: у ассетов карты нет ролей, которые есть в этом языке."; return plan; }
        var steps = new List<IEditCommand>();
        var introduced = new List<MapAssetReference>();
        foreach (var (stamp, record, variant) in moves)
        {
            var reference = ResolveAssetForDocument(record, out _, out var refusal);
            if (reference is null) { StatusDetail = refusal ?? "Ассет языка недоступен."; return plan with { Replaced = 0 }; }
            MapAssetReference? mask = null;
            if (variant.MaskSha256 is { } maskSha && (mask = ResolveMaskForDocument(maskSha, out _)) is null) continue;
            steps.Add(new ReplaceStampAssetCommand(stamp, reference, record.Height / Math.Max(1.0, record.Width), variant.MaskSha256, mask));
        }
        var replaced = steps.Count;
        if (style is { } preset)
        {
            var current = Style;
            var next = (MapStyle.For(preset) with { Strength = current.Enabled && current.Strength > 0 ? current.Strength : 1 }).Normalized();
            if (Session.Document.Style != next) steps.Add(new SetMapStyleCommand("Стиль «" + PresetTitle(preset) + "»", Session.Document.Style, next));
        }
        if (steps.Count == 0) return plan with { Replaced = 0 };
        Session.Execute(new CompositeCommand(style is null ? "Сменить художественный язык" : "Сменить язык и стиль", steps));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        Raise(nameof(Style));
        StatusDetail = $"Перерисовано ассетов: {replaced}." + (style is { } applied ? $" Стиль — «{PresetTitle(applied)}»." : "") + (plan.InConstructions > 0 ? $" В путях и областях — {plan.InConstructions}: перестройте их с новым набором." : "")
            + " Ctrl+Z вернёт прежний язык.";
        return plan with { Replaced = replaced };
    }

    public static string RoleTitle(AssetRole role) => role switch
    {
        AssetRole.Tree => "деревья",
        AssetRole.Spruce => "ели",
        AssetRole.Bush => "кусты",
        AssetRole.Peak => "вершины",
        AssetRole.Slope => "склоны",
        AssetRole.House => "дома",
        AssetRole.Tower => "башни",
        AssetRole.Wall => "стены",
        AssetRole.Door => "двери",
        AssetRole.Sign => "знаки",
        _ => "без роли",
    };

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
