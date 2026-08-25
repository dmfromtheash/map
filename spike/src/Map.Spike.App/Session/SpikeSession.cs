using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Map.Spike.Core.Commands;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Export;
using Map.Spike.Persistence;
using Map.Spike.Persistence.Atomic;
using Map.Spike.Persistence.Recovery;
using Map.Spike.Rendering;

namespace Map.Spike.App.Session;

public enum ToolMode
{
    Select,
    Brush,
    MaskErase,
}

/// <summary>
/// Everything the spike window edits. Selection, viewport and tool mode live here as session
/// state, not in the document (docs/04). The document is the only source of truth for map content
/// and is mutated exclusively through <see cref="History"/>.
/// </summary>
public sealed class SpikeSession : IDisposable
{
    public const int DefaultObjectCount = 10_000;

    private readonly AtomicDocumentStore _store;
    private BrushStrokeSession? _activeBrush;
    private MaskStrokeSession? _activeMask;
    private bool _disposed;

    public SpikeSession(int objectCount = DefaultObjectCount)
    {
        _store = new AtomicDocumentStore();
        Document = BuildScene(objectCount);
        GeometryCache = ObjectGeometryCache.Build(Document.Objects);
        TileCache = new TileImageCache();
        Viewport = new Viewport();
        History = new CommandHistory();
        ObjectCount = objectCount;

        Document.MarkSaved(Document.Revision);
        DocumentPath = Path.Combine(DefaultOutputDirectory, "spike-session" + SpikeFormat.Extension);
    }

    public SpikeDocument Document { get; private set; }
    public ObjectGeometryCache GeometryCache { get; private set; }
    public TileImageCache TileCache { get; private set; }
    public Viewport Viewport { get; }
    public CommandHistory History { get; private set; }

    public int ObjectCount { get; private set; }
    public ToolMode Mode { get; set; } = ToolMode.Select;
    public ObjectId? SelectedObjectId { get; private set; }
    public bool MaskEnabled { get; set; } = true;
    public bool ShowMaskEditOverlay { get; set; } = true;
    public BrushSettings Brush { get; set; } = BrushSettings.Default;

    public string DocumentPath { get; private set; }

    /// <summary>Last user-visible outcome line, shown in the status panel.</summary>
    public string StatusLine { get; private set; } = "Ready.";

    /// <summary>Set when a save failed. The window shows this instead of a success message.</summary>
    public string? FailureLine { get; private set; }

    public string LastHitTestLine { get; private set; } = "hit-test: not run yet";

    public static string DefaultOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Map",
        "Spike");

    public bool IsStrokeActive => _activeBrush is not null || _activeMask is not null;

    // ------------------------------------------------------------------ scene

    private static SpikeDocument BuildScene(int objectCount)
    {
        var document = SpikeFixtures.CreateObjectScene(objectCount, SpikeFixtures.DefaultCanvasSize);

        // A decor patch in the top-left corner. It shares no pixels with the brush layer, so it is
        // the visual control that shows the mask is confined to its target layer.
        SpikeFixtures.FillDecorPatch(document, 3, 2, 0x8A6E4BC0);
        return document;
    }

    public void RebuildScene(int objectCount)
    {
        GeometryCache.Dispose();
        TileCache.Dispose();

        Document = BuildScene(objectCount);
        Document.MarkSaved(Document.Revision);
        GeometryCache = ObjectGeometryCache.Build(Document.Objects);
        TileCache = new TileImageCache();
        History = new CommandHistory();
        ObjectCount = objectCount;
        SelectedObjectId = null;
        StatusLine = $"Rebuilt scene with {objectCount:N0} objects.";
        FailureLine = null;
    }

    public void ZoomToFit(double viewWidth, double viewHeight)
    {
        if (viewWidth <= 0 || viewHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(viewWidth / Document.CanvasWidth, viewHeight / Document.CanvasHeight) * 0.96;
        var offsetX = ((Document.CanvasWidth * scale) - viewWidth) / (2 * scale);
        var offsetY = ((Document.CanvasHeight * scale) - viewHeight) / (2 * scale);
        Viewport.Set(new Vec2(offsetX, offsetY), scale);
    }

    // ------------------------------------------------------------------ input

    public void Select(Vec2 screenPoint)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = HitTester.HitTestScreen(Document, Viewport, screenPoint);
        stopwatch.Stop();

        SelectedObjectId = result.Object?.Id;
        LastHitTestLine =
            $"hit-test: {stopwatch.Elapsed.TotalMicroseconds:F1} us, {result.Stats}, " +
            $"selected={(result.Object is null ? "none" : result.Object.Id.ToString())}";

        StatusLine = result.Object is null
            ? "Nothing under the cursor."
            : $"Selected {result.Object.Kind} {result.Object.Id}.";
    }

    public void BeginStroke(Vec2 worldPoint)
    {
        switch (Mode)
        {
            case ToolMode.Brush:
                _activeBrush = new BrushStrokeSession(Document.Brush, Brush);
                _activeBrush.AddPoint(worldPoint);
                break;

            case ToolMode.MaskErase:
                _activeMask = new MaskStrokeSession(Document.Mask, Brush with { Flow = 1.0 });
                _activeMask.AddPoint(worldPoint);
                break;
        }
    }

    public void ContinueStroke(Vec2 worldPoint)
    {
        _activeBrush?.AddPoint(worldPoint);
        _activeMask?.AddPoint(worldPoint);
    }

    /// <summary>Ends the gesture and puts exactly one entry into the history.</summary>
    public void EndStroke()
    {
        if (_activeBrush is { } brush)
        {
            _activeBrush = null;
            var command = brush.Complete();
            if (command is not null)
            {
                History.Execute(Document, command);
                StatusLine = $"Brush stroke committed: {command.TouchedTiles.Count} tiles, " +
                             $"{command.ApproximateMemoryBytes / 1024:N0} KiB of undo state.";
            }
        }

        if (_activeMask is { } mask)
        {
            _activeMask = null;
            var command = mask.Complete();
            if (command is not null)
            {
                History.Execute(Document, command);
                StatusLine = $"Mask stroke committed: {command.TouchedTiles.Count} tiles.";
            }
        }
    }

    public void Undo()
    {
        StatusLine = History.Undo(Document)
            ? "Undo."
            : "Nothing to undo.";
    }

    public void Redo()
    {
        StatusLine = History.Redo(Document)
            ? "Redo."
            : "Nothing to redo.";
    }

    // ------------------------------------------------------------------ file

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DefaultOutputDirectory);
            var result = _store.Save(Document, DocumentPath);

            FailureLine = null;
            StatusLine =
                $"Saved revision {result.SavedRevision} to {result.TargetPath} " +
                $"({result.BytesWritten / 1024.0 / 1024.0:F2} MiB in {result.Duration.TotalMilliseconds:F0} ms). " +
                $"dirty={Document.IsDirty}";
        }
        catch (SaveFailedException ex)
        {
            // No false success: the document stays dirty and the user is told where the work is.
            FailureLine =
                $"SAVE FAILED at {ex.Phase}. The document is still unsaved (dirty={Document.IsDirty}). " +
                $"Last good file: {(ex.TargetStillIntact ? ex.TargetPath : "none")}. " +
                $"Recoverable temp: {ex.TempPath ?? "none"}.";
            StatusLine = "Save failed - see the red line.";
        }
    }

    public void Reopen()
    {
        try
        {
            if (!File.Exists(DocumentPath))
            {
                StatusLine = $"Nothing saved yet at {DocumentPath}.";
                return;
            }

            var loaded = AtomicDocumentStore.Load(DocumentPath);

            GeometryCache.Dispose();
            TileCache.Dispose();

            Document = loaded;
            GeometryCache = ObjectGeometryCache.Build(Document.Objects);
            TileCache = new TileImageCache();
            History = new CommandHistory();
            SelectedObjectId = null;
            ObjectCount = Document.Objects.Count;
            FailureLine = null;

            StatusLine =
                $"Reopened {DocumentPath}: {Document.Objects.Count:N0} objects, " +
                $"{Document.Brush.TileCount} brush tiles, {Document.Mask.TileCount} mask tiles, " +
                $"revision {Document.Revision}, dirty={Document.IsDirty}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            FailureLine = $"REOPEN FAILED: {ex.Message}";
            StatusLine = "Reopen failed - see the red line.";
        }
    }

    public void Export4K()
    {
        try
        {
            Directory.CreateDirectory(DefaultOutputDirectory);
            var path = Path.Combine(DefaultOutputDirectory, "export-4096.png");
            var result = OffscreenPngExporter.Export(Document, path, 4096, 4096);

            FailureLine = null;
            StatusLine =
                $"Exported {result.Width}x{result.Height} PNG ({result.FileBytes / 1024.0 / 1024.0:F2} MiB) " +
                $"in {result.Duration.TotalMilliseconds:F0} ms to {result.Path}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            FailureLine = $"EXPORT FAILED: {ex.Message}";
            StatusLine = "Export failed - see the red line.";
        }
    }

    public IEnumerable<string> RecoveryNotices()
    {
        var record = _store.Journal.TryRead(Document.DocumentId);
        if (record is null)
        {
            yield break;
        }

        yield return record.HasRecoverableWork
            ? $"Recoverable work from {record.UpdatedUtc}: {record.TempPath} (revision {record.PendingRevision})."
            : $"An interrupted save was recorded ({record.State}) but nothing verified is recoverable.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GeometryCache.Dispose();
        TileCache.Dispose();
    }
}
