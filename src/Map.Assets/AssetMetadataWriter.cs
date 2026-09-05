namespace Map.Assets;

/// <summary>One serial, coalescing writer. Only private owned snapshots cross the thread boundary.</summary>
internal sealed class AssetMetadataWriter(string path, AssetLibraryDocument? initial = null)
{
    private readonly object _gate = new();
    private AssetLibraryDocument? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private Exception? _error;
    private AssetLibraryDocument? _lastSaved = initial;
    public string? Error { get { lock (_gate) return _error?.Message; } }
    public AssetLibraryDocument? LastSaved { get { lock (_gate) return _lastSaved; } }

    public void Enqueue(AssetLibraryDocument snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            if (_running) return;
            _running = true;
            _worker = Task.Run(PumpAsync);
        }
    }

    public async Task FlushAsync()
    {
        Task worker;
        lock (_gate) worker = _worker;
        await worker.ConfigureAwait(false);
        lock (_gate) if (_error is { } error) throw new IOException("Метаданные каталога не сохранены.", error);
    }

    private async Task PumpAsync()
    {
        await Task.Delay(25).ConfigureAwait(false);
        while (true)
        {
            AssetLibraryDocument snapshot;
            lock (_gate) { snapshot = _pending!; _pending = null; }
            Exception? failure = null;
            try { AssetLibraryStore.Save(path, snapshot); }
            catch (Exception error) { failure = error; }
            lock (_gate)
            {
                _error = failure;
                if (failure is null) _lastSaved = snapshot;
                if (_pending is not null) continue;
                _running = false;
                return;
            }
        }
    }
}
