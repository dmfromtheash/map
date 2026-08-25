using System.Globalization;
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
        string appVersion = "0.2.0")
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
            _activeTool = value;
            Raise();
            Raise(nameof(MaskEditLayerId));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public BrushSettings BrushSettings { get; set; } = new();

    public MaskPaintMode MaskMode { get; set; } = MaskPaintMode.Hide;

    public StrokePreview? StrokePreview => _stroke is null || _activeLayerId is null
        ? null
        : new StrokePreview
        {
            LayerId = _activeLayerId.Value,
            Coverage = _stroke.Coverage,
            Color = _stroke.Settings.Color,
            Opacity = _stroke.Settings.Opacity,
            Eraser = _stroke.Settings.Eraser,
            IsMask = ActiveTool == EditorTool.Mask,
        };

    public LayerId? MaskEditLayerId =>
        ActiveTool == EditorTool.Mask && ActiveLayer?.Mask is not null ? _activeLayerId : null;

    // ---------------------------------------------------------------- editing

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
        else
        {
            StatusDetail = ActiveTool == EditorTool.Mask
                ? "Сначала добавьте маску к выбранному слою."
                : "Для рисования выберите слой кисти.";
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
        else
        {
            var (Before, After) = stroke.Commit();
            Session.Execute(new BrushStrokeCommand(
                _activeLayerId.Value, Before, After,
                BrushSettings.Eraser ? "Erase stroke" : "Brush stroke"));
        }

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
