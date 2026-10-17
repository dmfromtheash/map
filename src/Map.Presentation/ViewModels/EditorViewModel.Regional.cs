using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public enum RegionalPlacementMode { Forest, Ridge, Settlement }

public sealed partial class EditorViewModel
{
    public bool CanImportRegionalPack => _assetCatalog is not null && RegionalArtPack.IsAvailable;
    public async Task<bool> ImportRegionalPackAsync()
    {
        if (_assetCatalog is null) return false;
        try
        {
            await Task.Run(() => RegionalArtPack.Import(_assetCatalog));
            RefreshLibrary(); StatusDetail = $"Комплект «{RegionalArtPack.Load().Name}» подключён. Ваши наборы сохранены.";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { StatusDetail = "Комплект не подключён: " + e.Message; return false; }
    }

    public bool StartRegionalPlacement(RegionalPlacementMode mode)
    {
        if (_assetCatalog is null || !RegionalArtPack.IsAvailable) return false;
        var group = mode switch { RegionalPlacementMode.Ridge => "mountains", RegionalPlacementMode.Settlement => "buildings", _ => "trees" };
        var hashes = RegionalArtPack.Load().Assets.Where(a => a.Group == group).Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var set = _assetCatalog.Sets.FirstOrDefault(s => s.VariantIds.Select(_assetCatalog.RecordForVariant)
            .OfType<AssetRecord>().Select(r => r.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hashes));
        if (set is null) { StatusDetail = "Сначала подключите «Тихий атлас» в каталоге. Импорт выполняется только по вашей команде."; return false; }
        CancelAppearanceTransfer(); CancelEditableAreaGesture(); CancelEditablePathGesture();
        _selectedAreaId = null; _selectedPathId = null; _newAreaTemplate = null;
        // The regional preset below is for this construction only: the user's ordinary stamp
        // settings come back when they switch to «Один», «Серия» or «Россыпь».
        RememberStampSettingsBeforeConstruction();
        SelectAssetSet(set.Id, armPlacement: false);
        if (RegionalMapFactory.IsRegional(Session.Document))
        {
            var name = mode switch { RegionalPlacementMode.Ridge => "Хребты · редактируемые пути",
                RegionalPlacementMode.Settlement => "Поселения и ориентиры", _ => "Леса · редактируемые области" };
            var layer = Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l=>l.Visible && !l.Locked
                && (l.Name==name || l.PlacementAreas.Any(a=>a.AssetSetId==set.Id) || l.PlacementPaths.Any(p=>p.AssetSetId==set.Id)));
            if (layer is not null) _activeLayerId=layer.Id;
        }
        var width = mode switch { RegionalPlacementMode.Ridge => 230d, RegionalPlacementMode.Settlement => 60d, _ => 46d };
        _stampToolSettings = _stampToolSettings with { ScalePercent = width / DefaultStampReferenceWidth() * 100,
            RotationDegrees = 0, ObjectOpacity = 1, RandomizeVariant = true, RandomAngleStep = 0,
            RandomFlip = false, SeriesSpacingPercent = 62,
            Style = _stampToolSettings.Style is { } s ? s with { ShadowOpacity = 0, Blur = 0, Hue = 0, Saturation = 0, Brightness = 0, Contrast = 0 } : null };
        _naturalAreaDensity = mode == RegionalPlacementMode.Settlement ? 78 : 72;
        _naturalAreaSizeVariation = mode == RegionalPlacementMode.Settlement ? 12 : 28;
        _naturalAreaPattern = mode == RegionalPlacementMode.Settlement ? PlacementAreaPattern.Groups : PlacementAreaPattern.Masses;
        _naturalAreaJoinEdges = mode != RegionalPlacementMode.Settlement;
        if (mode == RegionalPlacementMode.Ridge) StartEditablePath(); else StartEditableArea();
        RaiseStampToolState(); return true;
    }

    // Regional tools and selected areas/paths load their own scale, style and density into the
    // shared stamp settings. Without a way back, one «Лес по области» silently rewrote the scale,
    // shadow and rotation of every later ordinary placement, and saved them as preferences.
    private StampToolSettings? _stampSettingsBeforeConstruction;
    private (double Density, double SizeVariation, PlacementAreaPattern Pattern, bool JoinEdges)? _areaSettingsBeforeConstruction;
    private void RememberStampSettingsBeforeConstruction()
    {
        if (_stampSettingsBeforeConstruction is not null) return;
        _stampSettingsBeforeConstruction = _stampToolSettings;
        _areaSettingsBeforeConstruction = (_naturalAreaDensity, _naturalAreaSizeVariation, _naturalAreaPattern, _naturalAreaJoinEdges);
    }
    private void RestoreStampSettingsAfterConstruction()
    {
        if (_stampSettingsBeforeConstruction is not { } saved) return;
        _stampSettingsBeforeConstruction = null;
        _stampToolSettings = saved with { Mode = _stampToolSettings.Mode, AssetSetId = _stampToolSettings.AssetSetId,
            VariantId = _stampToolSettings.VariantId };
        if (_areaSettingsBeforeConstruction is { } area)
            (_naturalAreaDensity, _naturalAreaSizeVariation, _naturalAreaPattern, _naturalAreaJoinEdges) = area;
        _areaSettingsBeforeConstruction = null;
        RaiseStampToolState(); PersistLocalPreferences();
    }

    public IReadOnlyList<LocalBrushPreset> RegionalPresets => _brushPresets.Where(p => p.Placement is not null || p.Appearance is not null).ToArray();
    public bool SavePlacementPreset(string name)
    {
        if (_brushPresetStore is null || string.IsNullOrWhiteSpace(name) || _stampToolSettings.AssetSetId is not { } set) return false;
        var s = _stampToolSettings;
        return SaveRegionalPreset(new LocalBrushPreset { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(),
            Placement = new(set, s.VariantId, s.Mode.ToString(), s.ScalePercent, s.RotationDegrees, s.Sublayer,
                s.ObjectOpacity, s.SeriesSpacingPercent, s.RandomizeVariant, s.Style, EditableAreaDensity,
                EditableAreaSizeVariation, EditableAreaPattern, EditableAreaJoinEdges, s.RandomAngleStep, s.RandomFlip) });
    }
    public bool SaveAppearancePreset(string name) => _copiedAppearance is { } style && !string.IsNullOrWhiteSpace(name)
        && SaveRegionalPreset(new LocalBrushPreset { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Appearance = style });
    private bool SaveRegionalPreset(LocalBrushPreset preset)
    {
        if (_brushPresetStore is null) return false;
        _brushPresets = _brushPresetStore.Save([.._brushPresets, preset]);
        Raise(nameof(RegionalPresets)); StatusDetail = $"Пресет «{preset.Name}» сохранён локально."; return true;
    }
    public bool ApplyRegionalPreset(string id)
    {
        var preset = _brushPresets.FirstOrDefault(p => p.Id == id);
        if (preset?.Appearance is { } appearance)
        { CancelAppearanceTransfer(); _copiedAppearance = appearance;
            AppearanceTransferSummary = $"Загружен вид {appearance.Kind}. Выберите цели и включите предпросмотр.";
            StatusDetail = AppearanceTransferSummary; Raise(nameof(AppearanceTransferSummary)); return true; }
        if (preset?.Placement is not { } p || _assetCatalog?.FindSet(p.SetId) is null)
        { StatusDetail = "Набор этого пресета недоступен. Подключите его в каталоге."; return false; }
        CancelEditableAreaGesture(); CancelEditablePathGesture(); _selectedAreaId = null; _selectedPathId = null; _newAreaTemplate = null;
        SelectAssetSet(p.SetId, armPlacement: false);
        if (p.VariantId is not null && _assetCatalog.FindVariant(p.VariantId) is not null) ChooseGalleryVariant(p.SetId,p.VariantId,armPlacement:false);
        _stampToolSettings = NormalizeStampToolSettings(_stampToolSettings with { ScalePercent = p.Scale, RotationDegrees = p.Rotation,
            Sublayer = p.Sublayer, ObjectOpacity = p.Opacity, SeriesSpacingPercent = p.Spacing, RandomizeVariant = p.RandomVariants,
            RandomAngleStep = p.RandomAngleStep, RandomFlip = p.RandomFlip,
            Style = p.Style is null ? _stampToolSettings.Style : TransferStamp(p.Style, _stampToolSettings.Style!) });
        _naturalAreaDensity = Math.Clamp(p.Density,0,100); _naturalAreaSizeVariation = Math.Clamp(p.SizeVariation,0,50);
        _naturalAreaPattern = p.Pattern; _naturalAreaJoinEdges = p.JoinEdges;
        if (p.Mode == nameof(StampPlacementMode.Path)) StartEditablePath();
        else if (p.Mode == nameof(StampPlacementMode.FillArea)) StartEditableArea();
        else { StampPlacementMode = Enum.TryParse<StampPlacementMode>(p.Mode,out var mode) ? mode : StampPlacementMode.One; ArmStampPlacement(); }
        RaiseStampToolState(); PersistLocalPreferences(); return true;
    }

    private CopiedAppearance? _copiedAppearance;
    private MapDocument? _appearanceTransferBefore, _appearanceTransferPreview;
    private string? _appearanceTransferSelection;
    private IReadOnlyList<IEditCommand> _appearanceTransferCommands = [];
    public string AppearanceTransferSummary { get; private set; } = "Скопируйте вид одного ассета, подписи или дороги. Геометрия и содержание целей сохраняются.";
    private string AppearanceSelectionKey => string.Join(";", Session.SelectedObjectIds.OrderBy(x => x.ToString())) + "|" +
        string.Join(";",SelectedRoads.Select(r=>r.Id).Order()) + "|" + _roadLevel + "|" + SelectedRoadBranch?.Id;
    private MapDocument? ValidAppearancePreview => ReferenceEquals(_appearanceTransferBefore,Session.Document)
        && _appearanceTransferSelection == AppearanceSelectionKey ? _appearanceTransferPreview : null;
    public bool HasAppearanceTransferPreview => ValidAppearancePreview is not null;
    public bool CopyAppearance()
    {
        CancelAppearanceTransfer();
        _copiedAppearance = Session.SelectedObjects.Count == 1 ? Session.SelectedObject switch
        {
            { Stamp: { } stamp } o => new("ассета",o.ObjectOpacity,Stamp:stamp),
            { Text: { } text } o => new("подписи",o.ObjectOpacity,Text:text),
            _ => null,
        } : SelectedRoads.Count == 1 && SelectedRoad is { } road
            ? new("дороги",Road: _roadLevel != RoadSelectionLevel.Road && SelectedRoadBranch?.Style is { } branchStyle ? branchStyle : road.Style) : null;
        AppearanceTransferSummary = _copiedAppearance is null ? "Выберите один ассет, подпись или дорогу." : $"Скопирован вид {_copiedAppearance.Kind}. Выберите цели и нажмите «Предпросмотр».";
        Raise(nameof(AppearanceTransferSummary)); return _copiedAppearance is not null;
    }
    public bool PreviewAppearanceTransfer()
    {
        CancelAppearanceTransfer();
        if (_copiedAppearance is not { } style) { StatusDetail = "Сначала скопируйте вид или загрузите его пресет."; return false; }
        var commands = new List<IEditCommand>(); var skipped = 0; var count = 0;
        var before = new List<SceneObject>(); var after = new List<SceneObject>();
        foreach (var o in Session.SelectedObjects)
        {
            if (!IsObjectAvailable(o) || PlacementOwnership.Owner(Session.Document,o.Id) is not null) { skipped++; continue; }
            SceneObject? next = style.Stamp is { } stamp && o.Stamp is { } target ? o with { Stamp = TransferStamp(stamp,target),ObjectOpacity=style.Opacity }
                : style.Text is { } text && o.Text is { } targetText ? o with { Text = text with { Content = targetText.Content,
                    Curve = targetText.Curve, FlipHorizontal = targetText.FlipHorizontal, FlipVertical = targetText.FlipVertical }, ObjectOpacity=style.Opacity } : null;
            if (next is null) { skipped++; continue; }
            if (next != o) { before.Add(o); after.Add(next); count++; }
        }
        if (before.Count > 0) commands.Add(new ReplaceObjectsCommand(before,after,"Перенести вид"));
        foreach(var road in SelectedRoads)
        {
            if (style.Road is null || road.Locked || !road.Visible || RoadLayerOf(road.Id) is not { Visible:true,Locked:false } layer)
            { skipped++; continue; }
            var next = _roadLevel != RoadSelectionLevel.Road && SelectedRoads.Count == 1 && SelectedRoadBranch is { } branch
                ? road with { Branches = [..road.Branches.Select(b=>b.Id==branch.Id?b with { Style=style.Road }:b)] }
                : road with { Style=style.Road };
            if(next!=road) { commands.Add(new RoadCommand(layer.Id,road.Id,next,"Перенести вид дороги")); count++; }
        }
        if(commands.Count == 0) { AppearanceTransferSummary = $"Нет изменений. Пропущено: {skipped}. Связанные ассеты сначала отделите от группы."; Raise(nameof(AppearanceTransferSummary)); return false; }
        _appearanceTransferBefore = Session.Document; _appearanceTransferSelection = AppearanceSelectionKey;
        var preview = Session.Document;
        foreach(var command in commands) preview = command.Apply(preview);
        _appearanceTransferCommands=commands; _appearanceTransferPreview=preview;
        AppearanceTransferSummary = $"Предпросмотр: {count}. Пропущено: {skipped} (несовместимы, связаны или заблокированы). «Применить» — один шаг отмены.";
        Raise(nameof(AppearanceTransferSummary)); Raise(nameof(HasAppearanceTransferPreview)); SceneChanged?.Invoke(this,EventArgs.Empty); return true;
    }
    public bool ApplyAppearanceTransfer()
    {
        if(!HasAppearanceTransferPreview) { CancelAppearanceTransfer(); StatusDetail="Выделение или карта изменились. Обновите предпросмотр."; return false; }
        var command = new CompositeCommand("Перенести внешний вид",_appearanceTransferCommands);
        CancelAppearanceTransfer(); Session.Execute(command); AfterEdit(); return true;
    }
    public void CancelAppearanceTransfer()
    {
        _appearanceTransferBefore=null; _appearanceTransferPreview=null; _appearanceTransferCommands=[]; _appearanceTransferSelection=null;
        Raise(nameof(HasAppearanceTransferPreview)); SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    private static StampStyle TransferStamp(StampStyle source, StampStyle target) => target with
    {
        Blend=source.Blend,Blur=source.Blur,Hue=source.Hue,Saturation=source.Saturation,Brightness=source.Brightness,Contrast=source.Contrast,
        ShadowOffsetX=source.ShadowOffsetX,ShadowOffsetY=source.ShadowOffsetY,ShadowBlur=source.ShadowBlur,
        ShadowOpacity=source.ShadowOpacity,ShadowColor=source.ShadowColor,ShadowPlacement=source.ShadowPlacement,
    };
}
