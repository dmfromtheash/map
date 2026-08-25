using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public enum SaveState
{
    Saved,
    Saving,
    Unsaved,
    SaveFailed,
}

/// <summary>What a canvas drag is doing to the selected object.</summary>
public enum DragMode
{
    Move,
    Resize,
    Rotate,
}

public enum EditorTool
{
    Select,
    Brush,
    Mask,
    Land,
    Line,
    Shape,
}

/// <summary>Live drag preview: never part of the document and never part of history.</summary>
public sealed record DragPreview(
    SceneObject Before,
    SceneObject Proposed,
    DragMode Mode,
    HandleKind? Handle,
    double StartWorldX,
    double StartWorldY);

/// <summary>
/// The editor around one <see cref="EditingSession"/>. The session's snapshot is the single
/// source of truth; this class only orchestrates commands, selection, saving and status.
/// </summary>
public sealed class EditorViewModel : ViewModelBase
{
    private readonly IProjectStore _store;
    private readonly IRecentMaps _recent;
    private readonly IFilePicker _filePicker;
    private readonly IUserPrompts _prompts;
    private readonly IAppLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly AssetCatalog? _assetCatalog;

    private SaveState _saveState;
    private string _statusDetail = "";
    private string _inspectorError = "";
    private DragPreview? _dragPreview;
    private string _xText = "";
    private string _yText = "";
    private string _widthText = "";
    private string _heightText = "";
    private string _rotationText = "";
    private LayerId? _activeLayerId;
    private EditorTool _activeTool;
    private RasterStrokeSession? _stroke;
    private List<VectorPoint>? _vectorPoints;

    public EditorViewModel(
        MapDocument document,
        string? documentPath,
        bool startDirty,
        IProjectStore store,
        IRecentMaps recent,
        IFilePicker filePicker,
        IUserPrompts prompts,
        IAppLog log,
        Func<DateTimeOffset>? clock = null,
        string appVersion = "0.2.0",
        AssetCatalog? assetCatalog = null)
    {
        Session = new EditingSession(document);
        DocumentPath = documentPath;
        _store = store;
        _recent = recent;
        _filePicker = filePicker;
        _prompts = prompts;
        _log = log;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _appVersion = appVersion;
        _assetCatalog = assetCatalog;
        StampImages = assetCatalog is null
            ? null
            : new CatalogStampImageSource(assetCatalog, () => Session.Document, () => DocumentPath);

        if (startDirty)
        {
            // A brand-new or recovered document has no durable revision yet.
            Session.MarkSaved(-1);
        }

        _saveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
        _activeLayerId = document.Layers.LastOrDefault(layer => layer is not BaseColorLayer)?.Id
            ?? document.BaseColor.Id;
    }

    public EditingSession Session { get; }

    public IStampImageSource? StampImages { get; }

    public string? DocumentPath { get; private set; }

    public event EventHandler? SceneChanged;

    // ---------------------------------------------------------------- status

    public SaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (SetField(ref _saveState, value))
            {
                Raise(nameof(SaveStateText));
            }
        }
    }

    public string SaveStateText => _saveState switch
    {
        SaveState.Saved => "Сохранено",
        SaveState.Saving => "Сохранение…",
        SaveState.SaveFailed => "Ошибка сохранения",
        _ => "Не сохранено",
    };

    /// <summary>Short human explanation of the last failure, with the recovery path when one exists.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    public string Title => Session.Document.Title;

    public string ViewportStatus =>
        $"Масштаб {Math.Round(Session.Viewport.Scale * 100)}% · " +
        $"X {Session.Viewport.OffsetX:F0} · Y {Session.Viewport.OffsetY:F0}";

    public string SelectionStatus =>
        Session.SelectedObject is { } selected
            ? $"Выбран {selected.Kind} {selected.Id}"
            : "Ничего не выбрано";

    public IReadOnlyList<MapLayer> Layers => Session.Document.Layers;

    public MapLayer? ActiveLayer =>
        _activeLayerId is { } id ? Session.Document.FindLayer(id) : null;

    public LayerId? ActiveLayerId => _activeLayerId;

    public EditorTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value)
            {
                return;
            }

            CancelStroke();
            CancelVectorDraw();
            _activeTool = value;
            Raise();
            Raise(nameof(MaskEditLayerId));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public BrushSettings BrushSettings { get; set; } = new();

    public MaskPaintMode MaskMode { get; set; } = MaskPaintMode.Hide;

    public bool LandSubtract { get; set; }

    public StrokePreview? StrokePreview => _stroke is null || _activeLayerId is null
        ? null
        : new StrokePreview
        {
            LayerId = _activeLayerId.Value,
            Coverage = _stroke.Coverage,
            Color = ActiveTool == EditorTool.Land && ActiveLayer is LandLayer land
                ? land.EdgeStyle.FillColor
                : _stroke.Settings.Color,
            Opacity = _stroke.Settings.Opacity,
            Eraser = _stroke.Settings.Eraser,
            IsMask = ActiveTool == EditorTool.Mask,
        };

    public LayerId? MaskEditLayerId =>
        ActiveTool == EditorTool.Mask && ActiveLayer?.Mask is not null ? _activeLayerId : null;

    public IReadOnlyList<AssetRecord> Assets => _assetCatalog?.Records ?? [];

    public IReadOnlyList<AssetRecord> SearchAssets(string? query) =>
        _assetCatalog?.Search(query) ?? [];

    public AssetRecord? ImportAsset(string path, bool linked)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return null;
        }

        try
        {
            var imported = _assetCatalog.Import(path, new AssetImportOptions
            {
                Linked = linked,
                Provenance = new AssetProvenance
                {
                    Source = "user-imported",
                    LicenseStatus = AssetLicenseStatus.Unknown,
                },
            }, _clock());
            Raise(nameof(Assets));
            StatusDetail = $"Импортирован ассет «{imported.DisplayName}».";
            return imported;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось импортировать изображение: " + ex.Message;
            _log.Error("Asset import failed", ex);
            return null;
        }
    }

    public void PlaceStamp(AssetRecord record, bool embedded = true, double? x = null, double? y = null)
    {
        if (_assetCatalog is null)
        {
            return;
        }

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
        var resolution = _assetCatalog.Resolve(record);
        if (layer is null || layer.Locked || !layer.Visible
            || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = resolution.Status == AssetResolutionStatus.HashMismatch
                ? "Файл ассета изменился и не совпадает с сохранённым hash. Выберите замену явно."
                : "Выберите доступный ассет и разблокированный слой объектов.";
            return;
        }

        var thumbnail = File.Exists(_assetCatalog.ThumbnailPath(record))
            ? File.ReadAllBytes(_assetCatalog.ThumbnailPath(record))
            : null;
        var projectDirectory = DocumentPath is null ? null : Path.GetDirectoryName(DocumentPath);
        var relative = projectDirectory is null
            ? record.OriginalFileName
            : Path.GetRelativePath(projectDirectory, resolution.Path);
        var reference = new MapAssetReference(
            record.Sha256,
            record.DisplayName,
            record.MediaType,
            record.Extension,
            embedded ? ProjectAssetStorage.Embedded : ProjectAssetStorage.Linked,
            record.ImportedUtc,
            record.Provenance.Source,
            record.Provenance.LicenseId,
            embeddedContent: embedded ? File.ReadAllBytes(resolution.Path) : null,
            catalogId: embedded ? null : record.Sha256,
            relativePath: embedded ? null : relative,
            fallbackThumbnailPng: thumbnail);
        var width = Math.Min(256.0, Session.Document.Canvas.Width / 4.0);
        var height = width * record.Height / record.Width;
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
            x ?? ((Session.Document.Canvas.Width - width) / 2),
            y ?? ((Session.Document.Canvas.Height - height) / 2),
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(record.Sha256),
        };

        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, reference, [stamp]));
        Session.SelectedObjectId = stamp.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ScatterStamp(AssetRecord record, int count, int seed = 12345)
    {
        if (_assetCatalog is null || count is < 1 or > 5_000)
        {
            return;
        }

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
        var resolution = _assetCatalog.Resolve(record);
        if (layer is null || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Ассет недоступен для размещения.";
            return;
        }

        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
        var random = new Random(seed);
        var objects = new List<SceneObject>(count);
        for (var i = 0; i < count; i++)
        {
            var size = 48 + (random.NextDouble() * 80);
            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Stamp, $"{record.DisplayName} {i + 1}",
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Width - size),
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Height - size),
                size, size * record.Height / record.Width,
                random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(record.Sha256)
                {
                    FlipHorizontal = random.Next(2) == 0,
                },
            });
        }

        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, reference, objects));
        Session.SelectedObjectId = objects[^1].Id;
        AfterEdit();
    }

    public void ReplaceSelectedStampAsset(AssetRecord record)
    {
        if (_assetCatalog is null || Session.SelectedObject is not { Stamp: not null } selected)
        {
            StatusDetail = "Выберите stamp и новый ассет.";
            return;
        }

        var resolution = _assetCatalog.Resolve(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Новый ассет отсутствует или не совпадает с его SHA-256.";
            return;
        }

        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
        Session.Execute(new ReplaceStampAssetCommand(selected, reference));
        AfterEdit();
    }

    public void KeepSelectedStampPlaceholder()
    {
        StatusDetail = "Placeholder оставлен: ссылка и исходный SHA-256 не изменены.";
    }

    public void UpdateSelectedStamp(
        double? opacity = null, bool toggleFlip = false, bool toggleShadow = false,
        double? hue = null, double? saturation = null)
    {
        if (Session.SelectedObject is not { Stamp: { } style } before || before.Locked)
        {
            StatusDetail = "Выберите разблокированный stamp.";
            return;
        }

        var after = CopyObject(before) with
        {
            ObjectOpacity = opacity ?? before.ObjectOpacity,
            Stamp = style with
            {
                FlipHorizontal = toggleFlip ? !style.FlipHorizontal : style.FlipHorizontal,
                ShadowOpacity = toggleShadow ? (style.ShadowOpacity > 0 ? 0 : 0.55) : style.ShadowOpacity,
                Hue = hue ?? style.Hue,
                Saturation = saturation ?? style.Saturation,
            },
        };
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Edit stamp style"));
        AfterEdit();
    }

    // ---------------------------------------------------------------- editing

    public bool BeginVectorDraw(double worldX, double worldY)
    {
        if (ActiveTool is not (EditorTool.Line or EditorTool.Shape)
            || ActiveObjectLayer() is not { Visible: true, Locked: false })
        {
            return false;
        }

        _vectorPoints = [new VectorPoint(worldX, worldY)];
        return true;
    }

    public void ContinueVectorDraw(double worldX, double worldY)
    {
        if (_vectorPoints is not { Count: > 0 } points)
        {
            return;
        }

        var last = points[^1];
        if (Math.Pow(worldX - last.X, 2) + Math.Pow(worldY - last.Y, 2) >= 4)
        {
            points.Add(new VectorPoint(worldX, worldY));
        }
    }

    public void EndVectorDraw()
    {
        if (_vectorPoints is not { } points)
        {
            return;
        }

        _vectorPoints = null;
        var layer = ActiveObjectLayer();
        var required = ActiveTool == EditorTool.Shape ? 3 : 2;
        if (layer is null || points.Count < required)
        {
            return;
        }

        var added = ActiveTool == EditorTool.Shape
            ? VectorGeometry.CreateFreeShape(ObjectId.New(), "Свободная фигура", points)
            : VectorGeometry.CreateLine(ObjectId.New(), "Свободная линия", points);
        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CancelVectorDraw() => _vectorPoints = null;

    public DragPreview? DragPreview
    {
        get => _dragPreview;
        private set => SetField(ref _dragPreview, value);
    }

    /// <summary>What the canvas should draw: the snapshot, with the live drag substituted in.</summary>
    public MapDocument RenderDocument =>
        DragPreview is { } preview
            ? DocumentEditor.ReplaceObject(Session.Document, preview.Proposed)
            : Session.Document;

    public void AddObject(SceneObjectKind kind)
    {
        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault();
        if (layer is null || layer.Locked || !layer.Visible)
        {
            StatusDetail = "Выберите видимый разблокированный слой объектов.";
            return;
        }

        var canvas = Session.Document.Canvas;

        // Stagger new objects so consecutive adds stay individually visible.
        var offset = (Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) % 5) * 40;
        var width = Math.Min(240.0, canvas.Width / 4.0);
        var height = Math.Min(160.0, canvas.Height / 4.0);

        var added = new SceneObject(
            ObjectId.New(), kind,
            kind == SceneObjectKind.Ellipse ? "Эллипс" : "Прямоугольник",
            ((canvas.Width - width) / 2) + offset,
            ((canvas.Height - height) / 2) + offset,
            width, height, 0,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A", 2,
            visible: true, locked: false);

        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        AfterEdit();
    }

    public void AddLine()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var line = VectorGeometry.CreateLine(ObjectId.New(), "Линия",
        [
            new VectorPoint(centre.X - 240, centre.Y + 80),
            new VectorPoint(centre.X, centre.Y - 100),
            new VectorPoint(centre.X + 240, centre.Y + 40),
        ]);
        Session.Execute(new AddObjectCommand(layer.Id, line));
        Session.SelectedObjectId = line.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddShape(ShapePrimitiveKind kind)
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var shape = VectorGeometry.CreateShape(ObjectId.New(), $"Фигура {kind}", kind,
            new WorldBounds(centre.X - 180, centre.Y - 130, centre.X + 180, centre.Y + 130));
        Session.Execute(new AddObjectCommand(layer.Id, shape));
        Session.SelectedObjectId = shape.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddNodeToSelectedLine()
    {
        if (EditableSelection() is not { Line: not null } before) return;
        ReplaceSelected(before, VectorObjectEditor.AddLineNode(before, 1, 0.5), "Add line node");
    }

    public void MoveSelectedVectorNode()
    {
        if (EditableSelection() is not { } before) return;
        var node = before.Line?.Nodes.FirstOrDefault() ?? before.Shape?.Regions[0].Nodes.FirstOrDefault();
        if (node is null) return;
        ReplaceSelected(before,
            VectorObjectEditor.MoveNode(before, node.Id, Math.Clamp(node.X + 0.08, -1, 2), Math.Clamp(node.Y + 0.05, -1, 2)),
            "Move vector node");
    }

    public void ToggleSelectedNodeCurve()
    {
        if (EditableSelection() is not { Line: { } line } before) return;
        var node = line.Nodes[Math.Min(1, line.Nodes.Count - 1)];
        ReplaceSelected(before, VectorObjectEditor.SetNodeCurve(before, node.Id, node.Kind != VectorNodeKind.Curve),
            "Toggle line curve");
    }

    public void AddPortalToSelected()
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null) return;
        ReplaceSelected(before, VectorObjectEditor.AddPortal(before, 0.5, 48), "Add portal");
    }

    public void MoveSelectedPortal()
    {
        if (EditableSelection() is not { } before) return;
        var portal = before.Line?.Portals.FirstOrDefault() ?? before.Shape?.Portals.FirstOrDefault();
        if (portal is null) return;
        ReplaceSelected(before, VectorObjectEditor.MovePortal(before, portal.Id, Math.Min(0.9, portal.Position + 0.1)),
            "Move portal");
    }

    public void DeleteSelectedPortal()
    {
        if (EditableSelection() is not { } before) return;
        var portal = before.Line?.Portals.FirstOrDefault() ?? before.Shape?.Portals.FirstOrDefault();
        if (portal is null) return;
        ReplaceSelected(before, VectorObjectEditor.DeletePortal(before, portal.Id), "Delete portal");
    }

    public void CutSelectedLine()
    {
        if (EditableSelection() is not { Line: { Nodes.Count: >= 3 } line } before
            || DocumentEditor.FindLayerOf(Session.Document, before.Id) is not { } layer)
        {
            return;
        }

        var points = line.Nodes.Select(node => new VectorPoint(
            before.X + node.X * before.Width, before.Y + node.Y * before.Height)).ToList();
        var cut = points.Count / 2;
        var pieces = new[]
        {
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " A", points.Take(cut + 1).ToList(), line.Stroke),
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " B", points.Skip(cut).ToList(), line.Stroke),
        };
        Session.Execute(new ReplaceObjectWithManyCommand(Session.Document, layer.Id, before.Id, pieces));
        Session.SelectedObjectId = pieces[0].Id;
        AfterEdit();
    }

    public void CycleSelectedVectorStroke()
    {
        if (EditableSelection() is not { } before) return;
        var current = before.Line?.Stroke ?? before.Shape?.Stroke;
        if (current is null) return;
        var changed = current with
        {
            Offset = current.Offset == 0 ? 12 : 0,
            EndCap = current.EndCap == VectorCap.Round ? VectorCap.Square : VectorCap.Round,
            ShadowOpacity = current.ShadowOpacity == 0 ? 0.35 : 0,
        };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        ReplaceSelected(before, after, "Edit vector stroke");
    }

    public void UseAssetAsSelectedVectorStroke(AssetRecord record)
    {
        if (_assetCatalog is null || EditableSelection() is not { } before)
        {
            return;
        }

        var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
        var resolution = _assetCatalog.Resolve(record);
        if (stroke is null || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Выберите Line/Shape и доступный локальный ассет.";
            return;
        }

        var changed = stroke with { AssetSha256 = record.Sha256, AssetSpacing = 1.25 };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
        Session.Execute(new VectorAssetStyleCommand(before, after, reference));
        AfterEdit();
    }

    public void ApplyShapeBoolean(ShapeBooleanOperation operation)
    {
        var layer = ActiveObjectLayer();
        var shapes = layer?.Objects.Where(item => item.Shape is not null).TakeLast(2).ToList();
        if (layer is null || shapes?.Count != 2)
        {
            StatusDetail = "Для boolean нужны две последние фигуры на активном Object layer.";
            return;
        }

        var command = new ShapeBooleanCommand(Session.Document, layer.Id, shapes[0].Id, shapes[1].Id, operation);
        Session.Execute(command);
        Session.SelectedObjectId = command.ResultId;
        AfterEdit();
    }

    private ObjectLayer? ActiveObjectLayer() => ActiveLayer as ObjectLayer
        ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(layer => layer.Visible && !layer.Locked);

    private (double X, double Y) ViewCentre() => Session.Viewport.ScreenToWorld(
        Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);

    private void ReplaceSelected(SceneObject before, SceneObject after, string name)
    {
        Session.Execute(new ReplaceObjectsCommand([before], [after], name));
        AfterEdit();
    }

    public void SelectLayer(LayerId id)
    {
        if (Session.Document.FindLayer(id) is null)
        {
            return;
        }

        _activeLayerId = id;
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayer));
        Raise(nameof(MaskEditLayerId));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddObjectLayer()
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, []);
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddBrushLayer()
    {
        var layer = BrushLayer.CreateEmpty(LayerId.New(), "Кисть");
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddLandLayer()
    {
        var layer = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        ActiveTool = EditorTool.Land;
        AfterEdit();
    }

    public void RenameActiveLayer(string name)
    {
        if (ActiveLayer is not { } layer || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.Rename(layer, name.Trim()));
        AfterEdit();
    }

    public void ToggleActiveLayerVisibility()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetVisible(layer, !layer.Visible));
        AfterEdit();
    }

    public void ToggleActiveLayerLock()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetLocked(layer, !layer.Locked));
        AfterEdit();
    }

    public void CycleActiveLayerBlend()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        var next = layer.Blend switch
        {
            "Normal" => "Multiply",
            "Multiply" => "Screen",
            _ => "Normal",
        };
        Session.Execute(UpdateLayerCommand.SetBlend(layer, next));
        AfterEdit();
    }

    public void ReorderActiveLayer(bool up)
    {
        if (ActiveLayer is null)
        {
            return;
        }

        Session.Execute(new ReorderLayerCommand(ActiveLayer.Id, up));
        AfterEdit();
    }

    public void DuplicateActiveLayer()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var command = new DuplicateLayerCommand(ActiveLayer.Id);
        Session.Execute(command);
        _activeLayerId = command.CopyLayerId;
        AfterEdit();
    }

    public void DeleteActiveLayer()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var deleted = ActiveLayer.Id;
        Session.Execute(new DeleteLayerCommand(Session.Document, deleted));
        _activeLayerId = Session.Document.Layers[Session.Document.Layers.Count - 1].Id;
        AfterEdit();
    }

    public void ToggleActiveMask()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        Session.Execute(ActiveLayer.Mask is null
            ? UpdateMaskCommand.AddMask(ActiveLayer)
            : UpdateMaskCommand.SetEnabled(ActiveLayer, !ActiveLayer.Mask.Enabled));
        AfterEdit();
        Raise(nameof(MaskEditLayerId));
    }

    public bool BeginStroke(double worldX, double worldY)
    {
        if (ActiveLayer is not { Visible: true, Locked: false } layer)
        {
            StatusDetail = "Активный слой скрыт или заблокирован.";
            return false;
        }

        TileMap target;
        if (ActiveTool == EditorTool.Brush && layer is BrushLayer brush)
        {
            target = brush.Tiles;
        }
        else if (ActiveTool == EditorTool.Mask && layer.Mask is { } mask && layer is not BaseColorLayer)
        {
            target = mask.Tiles;
        }
        else if (ActiveTool == EditorTool.Land && layer is LandLayer land)
        {
            target = land.GeometryTiles;
        }
        else
        {
            StatusDetail = ActiveTool switch
            {
                EditorTool.Mask => "Сначала добавьте маску к выбранному слою.",
                EditorTool.Land => "Для земли выберите Land layer.",
                _ => "Для рисования выберите слой кисти.",
            };
            return false;
        }

        _stroke = new RasterStrokeSession(target, BrushSettings);
        _stroke.AddPoint(worldX, worldY);
        SceneChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ContinueStroke(double worldX, double worldY)
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke.AddPoint(worldX, worldY);
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EndStroke()
    {
        if (_stroke is null || _activeLayerId is null)
        {
            return;
        }

        var stroke = _stroke;
        _stroke = null;
        if (stroke.IsEmpty)
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (ActiveTool == EditorTool.Mask)
        {
            var (Before, After) = stroke.CommitCoverage(
                missingTileValue: 255,
                subtract: MaskMode == MaskPaintMode.Hide);
            Session.Execute(new MaskStrokeCommand(_activeLayerId.Value, Before, After));
        }
        else if (ActiveTool == EditorTool.Land)
        {
            var (Before, After) = stroke.CommitCoverage(missingTileValue: 0, subtract: LandSubtract);
            Session.Execute(new BrushStrokeCommand(
                _activeLayerId.Value, Before, After,
                LandSubtract ? "Subtract land" : "Add land"));
        }
        else
        {
            var (Before, After) = stroke.Commit();
            Session.Execute(new BrushStrokeCommand(
                _activeLayerId.Value, Before, After,
                BrushSettings.Eraser ? "Erase stroke" : "Brush stroke"));
        }

        AfterEdit();
    }

    public void ApplyLandShape(LandShapeKind kind)
    {
        if (ActiveLayer is not LandLayer land || land.Locked || !land.Visible)
        {
            StatusDetail = "Выберите видимый разблокированный Land layer.";
            return;
        }

        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        const double size = 512;
        var bounds = new WorldBounds(
            centre.X - (size / 2), centre.Y - (size / 2),
            centre.X + (size / 2), centre.Y + (size / 2));
        IReadOnlyList<LandPoint> polygon = kind == LandShapeKind.Polygon
            ?
            [
                new LandPoint(bounds.Left, bounds.Bottom),
                new LandPoint(centre.X, bounds.Top),
                new LandPoint(bounds.Right, bounds.Bottom),
            ]
            : [];
        var (Before, After) = LandRasterizer.ApplyShape(land.GeometryTiles, new LandShape
        {
            Kind = kind,
            Bounds = bounds,
            Points = polygon,
            GridSize = 64,
        }, LandSubtract);
        if (Before.Count == 0)
        {
            return;
        }

        Session.Execute(new BrushStrokeCommand(
            land.Id, Before, After,
            LandSubtract ? $"Subtract land {kind}" : $"Add land {kind}"));
        AfterEdit();
    }

    public void CycleActiveLandStyle()
    {
        if (ActiveLayer is not LandLayer land)
        {
            return;
        }

        var coast = land.EdgeStyle.RippleCount == 0;
        var style = land.EdgeStyle with
        {
            FillColor = coast ? "#FF8CA36B" : "#FFC69B62",
            OutlineColor = coast ? "#FF405533" : "#FF65452B",
            RippleCount = coast ? 3 : 0,
            RippleSpacing = coast ? 12 : 10,
            OuterShadowOpacity = coast ? 0.3 : 0.15,
        };
        Session.Execute(new UpdateLandStyleCommand(land, style));
        AfterEdit();
    }

    public void MoveActiveLandArea(int deltaX, int deltaY)
    {
        if (ActiveLayer is not LandLayer land || land.Locked)
        {
            return;
        }

        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        var (Before, After) = LandRasterizer.MoveRegion(
            land.GeometryTiles,
            new WorldBounds(centre.X - 256, centre.Y - 256, centre.X + 256, centre.Y + 256),
            deltaX,
            deltaY);
        if (Before.Count == 0)
        {
            return;
        }

        Session.Execute(new BrushStrokeCommand(land.Id, Before, After, "Move land area"));
        AfterEdit();
    }

    public void CancelStroke()
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAt(double worldX, double worldY)
    {
        Session.SelectedObjectId = SceneGeometry.HitTest(Session.Document, worldX, worldY)?.Id;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectObject(ObjectId id)
    {
        var layer = DocumentEditor.FindLayerOf(Session.Document, id);
        if (layer is null)
        {
            return;
        }

        Session.SelectedObjectId = id;
        _activeLayerId = layer.Id;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleSelectedObjectVisibility()
    {
        if (Session.SelectedObject is not { } before)
        {
            return;
        }

        var after = CopyObject(before, visible: !before.Visible);
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Object visibility"));
        AfterEdit();
    }

    public void ToggleSelectedObjectLock()
    {
        if (Session.SelectedObject is not { } before)
        {
            return;
        }

        var after = CopyObject(before, locked: !before.Locked);
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Object lock"));
        AfterEdit();
    }

    public void DeleteSelectedObject()
    {
        if (Session.SelectedObject is not { } selected
            || DocumentEditor.FindLayerOf(Session.Document, selected.Id) is not { } layer)
        {
            return;
        }

        Session.Execute(new RemoveObjectsCommand(Session.Document, layer.Id, [selected.Id]));
        Session.SelectedObjectId = null;
        AfterEdit();
    }

    /// <summary>Selected, editable object - locked and hidden objects cannot be edited.</summary>
    private SceneObject? EditableSelection()
    {
        var selected = Session.SelectedObject;
        return selected is { Locked: false, Visible: true } ? selected : null;
    }

    /// <summary>World-space reach of a handle grab and the rotation handle's stem, per zoom.</summary>
    public static double HandleHitRadius(double viewScale) => 8.0 / Math.Max(viewScale, 0.01);

    public static double RotationHandleOffset(double viewScale) => 28.0 / Math.Max(viewScale, 0.01);

    /// <summary>The gizmo handle under the cursor, for pointer feedback. Null when none.</summary>
    public HandleKind? HitHandle(double worldX, double worldY, double viewScale)
    {
        var selected = EditableSelection();
        if (selected is null)
        {
            return null;
        }

        return SelectionHandles.HitTest(
            selected, worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
    }

    /// <summary>
    /// One canvas press: a grab of a gizmo handle starts a resize/rotate of the current selection;
    /// otherwise the press selects, and an editable object starts a move. Returns true when a
    /// drag began. Whatever the mode, the whole gesture ends as one history entry.
    /// </summary>
    public bool BeginCanvasInteraction(double worldX, double worldY, double viewScale)
    {
        var handle = HitHandle(worldX, worldY, viewScale);
        if (handle is { } grabbed)
        {
            var selected = EditableSelection()!;
            DragPreview = new DragPreview(
                selected, selected,
                grabbed == HandleKind.Rotate ? DragMode.Rotate : DragMode.Resize,
                grabbed, worldX, worldY);
            return true;
        }

        SelectAt(worldX, worldY);
        RefreshInspector();
        return BeginDrag(worldX, worldY);
    }

    public bool BeginDrag(double startWorldX = 0, double startWorldY = 0)
    {
        var selected = EditableSelection();
        if (selected is null)
        {
            return false;
        }

        DragPreview = new DragPreview(selected, selected, DragMode.Move, null, startWorldX, startWorldY);
        return true;
    }

    /// <summary>Absolute-cursor drag update covering move, resize and rotate.</summary>
    public void UpdateDragTo(double worldX, double worldY)
    {
        if (DragPreview is not { } preview)
        {
            return;
        }

        var before = preview.Before;
        var proposed = preview.Mode switch
        {
            DragMode.Resize => SelectionHandles.Resize(before, preview.Handle!.Value, worldX, worldY),
            DragMode.Rotate => SelectionHandles.Rotate(
                before, preview.StartWorldX, preview.StartWorldY, worldX, worldY),
            _ => WithGeometry(
                before,
                before.X + (worldX - preview.StartWorldX),
                before.Y + (worldY - preview.StartWorldY),
                before.Width, before.Height, before.RotationDegrees),
        };

        DragPreview = preview with { Proposed = proposed };
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateDrag(double worldDx, double worldDy)
    {
        if (DragPreview is { } preview)
        {
            UpdateDragTo(preview.StartWorldX + worldDx, preview.StartWorldY + worldDy);
        }
    }

    /// <summary>The whole drag becomes exactly one history entry; a no-move drag becomes none.</summary>
    public void EndDrag()
    {
        if (DragPreview is not { } preview)
        {
            return;
        }

        DragPreview = null;

        if (preview.Proposed != preview.Before)
        {
            Session.Execute(new TransformObjectCommand(preview.Before, preview.Proposed));
            AfterEdit();
        }
    }

    public void CancelDrag()
    {
        DragPreview = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (Session.Undo())
        {
            AfterEdit();
        }
    }

    public void Redo()
    {
        if (Session.Redo())
        {
            AfterEdit();
        }
    }

    private void AfterEdit()
    {
        SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
        StatusDetail = "";
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(Layers));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(MaskEditLayerId));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- inspector

    public string XText { get => _xText; set => SetField(ref _xText, value); }
    public string YText { get => _yText; set => SetField(ref _yText, value); }
    public string WidthText { get => _widthText; set => SetField(ref _widthText, value); }
    public string HeightText { get => _heightText; set => SetField(ref _heightText, value); }
    public string RotationText { get => _rotationText; set => SetField(ref _rotationText, value); }

    public string InspectorError
    {
        get => _inspectorError;
        private set => SetField(ref _inspectorError, value);
    }

    public bool HasEditableSelection => EditableSelection() is not null;

    public void RefreshInspector()
    {
        var selected = Session.SelectedObject;
        XText = selected?.X.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        YText = selected?.Y.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        WidthText = selected?.Width.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        HeightText = selected?.Height.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        RotationText = selected?.RotationDegrees.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        InspectorError = "";
        Raise(nameof(HasEditableSelection));
    }

    /// <summary>
    /// Applies the numeric fields as ONE transform command. Invalid input produces a message and
    /// no command; unchanged values produce no history entry at all.
    /// </summary>
    public bool CommitInspector()
    {
        var before = EditableSelection();
        if (before is null)
        {
            InspectorError = "Выбранный объект нельзя редактировать.";
            return false;
        }

        if (!TryParse(XText, out var x) || !TryParse(YText, out var y)
            || !TryParse(WidthText, out var width) || !TryParse(HeightText, out var height)
            || !TryParse(RotationText, out var rotation)
            || width <= 0 || height <= 0)
        {
            InspectorError = "Числовые поля должны содержать корректные значения; размеры больше нуля.";
            return false;
        }

        var after = WithGeometry(before, x, y, width, height, rotation);

        if (after == before)
        {
            InspectorError = "";
            return true;
        }

        Session.Execute(new TransformObjectCommand(before, after));
        AfterEdit();
        return true;
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static SceneObject WithGeometry(
        SceneObject source, double x, double y, double width, double height, double rotation) =>
        new(
            source.Id, source.Kind, source.Name,
            x, y, width, height, rotation,
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            ObjectOpacity = source.ObjectOpacity,
        };

    private static SceneObject CopyObject(
        SceneObject source, bool? visible = null, bool? locked = null) =>
        new(
            source.Id, source.Kind, source.Name,
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            visible ?? source.Visible, locked ?? source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            ObjectOpacity = source.ObjectOpacity,
        };

    // ---------------------------------------------------------------- saving

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        var path = DocumentPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            path = await _filePicker.PickSaveAsync(Session.Document.Title + MapProjectFormat.Extension)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                return false; // user cancelled; nothing changes
            }
        }

        // Concurrent saves of one document must queue, not interleave.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = Session.Document;
            var revision = Session.Revision;
            var metadata = new SaveMetadata(_appVersion, _clock(), revision);

            SaveState = SaveState.Saving;
            StatusDetail = "";

            try
            {
                await Task.Run(() =>
                {
                    // The thumbnail comes from the same scene evaluator as the canvas, without
                    // selection or any editor chrome.
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot);
                    _store.Save(snapshot, path!, metadata, thumbnail);
                }).ConfigureAwait(false);

                DocumentPath = path;
                Session.MarkSaved(revision);
                SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
                Raise(nameof(DocumentPath));

                _recent.Touch(path!, Session.Document.Id.Value, snapshot.Title, metadata.ModifiedUtc);
                _log.Info($"Saved {path} (revision {revision}).");
                return true;
            }
            catch (MapProjectSaveException ex)
            {
                SaveState = SaveState.SaveFailed;
                StatusDetail = ex.TempPath is null
                    ? "Не удалось сохранить файл. Документ остаётся открытым, данные не потеряны."
                    : $"Не удалось заменить основной файл. Документ остаётся открыт, recovery сохранён здесь: {ex.TempPath}";
                _log.Error($"Save of {path} failed", ex);
                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>True when it is safe to leave the editor. Never loses work silently.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!Session.IsDirty)
        {
            return true;
        }

        return await _prompts.ConfirmCloseDirtyAsync(Session.Document.Title).ConfigureAwait(false) switch
        {
            CloseChoice.Save => await SaveAsync().ConfigureAwait(false) && !Session.IsDirty,
            CloseChoice.DontSave => true,
            _ => false,
        };
    }
}
