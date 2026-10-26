using Map.Core.Documents;

namespace Map.Core.Editing;

public enum GenerationApplyResult { Applied, MissingPreview, Conflict, ResourceRefused }

/// <summary>Owns one cancellable calculation. Only TryApply may write the editing session.</summary>
public sealed class BackgroundGeneration : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private long _job;
    private MapDocument? _snapshot;
    private long _revision;
    private IEditCommand? _command;
    private Task _worker = Task.CompletedTask;
    public bool IsBusy { get; private set; }
    public MapDocument? Preview { get; private set; }
    public bool Conflicted { get; private set; }
    public bool IsCurrent(EditingSession session) => session.Revision == _revision && ReferenceEquals(session.Document, _snapshot);

    public async Task CalculateAsync(EditingSession session, Func<MapDocument, CancellationToken, IEditCommand> calculate)
    {
        Cancel();
        var job = ++_job;
        var source = _cancellation = new CancellationTokenSource();
        var snapshot = _snapshot = session.Document;
        _revision = session.Revision;
        IsBusy = true;
        Task<(IEditCommand command, MapDocument preview)>? worker = null;
        try
        {
            var previous = _worker;
            worker = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch (Exception) { /* A previous job reports its own failure. */ }
                source.Token.ThrowIfCancellationRequested();
                var command = calculate(snapshot, source.Token);
                var preview = command.Apply(snapshot);
                source.Token.ThrowIfCancellationRequested();
                return (command, preview);
            });
            _worker = worker;
            var result = await worker;
            if (job != _job || source.IsCancellationRequested) return;
            _command = new GenerationSnapshotCommand(snapshot, result.preview, result.command.MeasuredWorkingBytes);
            Preview = result.preview;
            Conflicted = session.Revision != _revision || !ReferenceEquals(session.Document, snapshot);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_worker, worker)) _worker = Task.CompletedTask;
            if (job == _job) { IsBusy = false; _cancellation = null; }
            source.Dispose();
        }
    }

    public GenerationApplyResult TryApply(EditingSession session)
    {
        if (_command is null || Preview is null || IsBusy) return GenerationApplyResult.MissingPreview;
        if (session.Revision != _revision || !ReferenceEquals(session.Document, _snapshot))
        { Conflicted = true; return GenerationApplyResult.Conflict; }
        var revision = session.Revision;
        session.Execute(_command);
        if (session.Revision == revision) return GenerationApplyResult.ResourceRefused;
        Cancel();
        return GenerationApplyResult.Applied;
    }

    public void Cancel()
    {
        ++_job;
        _cancellation?.Cancel();
        _cancellation = null;
        IsBusy = false;
        _snapshot = null; _command = null; Preview = null; Conflicted = false;
    }

    public void Dispose() => Cancel();

    private sealed class GenerationSnapshotCommand(MapDocument before, MapDocument after, long? bytes) : IEditCommand
    {
        public string Name => "Применить генерацию";
        public long? MeasuredWorkingBytes => bytes;
        // Calculation and validation already finished in the worker. The UI only swaps immutable snapshots.
        public MapDocument Apply(MapDocument document) => ReferenceEquals(document, before) ? after
            : throw new InvalidOperationException("Карта изменилась: требуется пересчёт генерации.");
        public MapDocument Revert(MapDocument document) => before;
    }
}
