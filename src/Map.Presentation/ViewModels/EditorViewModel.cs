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
        var layer = Session.Document.Layers[0];
        var canvas = Session.Document.Canvas;

        // Stagger new objects so consecutive adds stay individually visible.
        var offset = (Session.Document.Layers.Sum(l => l.Objects.Count) % 5) * 40;
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

    public void SelectAt(double worldX, double worldY)
    {
        Session.SelectedObjectId = SceneGeometry.HitTest(Session.Document, worldX, worldY)?.Id;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        SceneChanged?.Invoke(this, EventArgs.Empty);
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
            _ => new SceneObject(
                before.Id, before.Kind, before.Name,
                before.X + (worldX - preview.StartWorldX),
                before.Y + (worldY - preview.StartWorldY),
                before.Width, before.Height, before.RotationDegrees,
                before.FillColor, before.StrokeColor, before.StrokeWidth,
                before.Visible, before.Locked),
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

        var after = new SceneObject(
            before.Id, before.Kind, before.Name,
            x, y, width, height, rotation,
            before.FillColor, before.StrokeColor, before.StrokeWidth,
            before.Visible, before.Locked);

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
