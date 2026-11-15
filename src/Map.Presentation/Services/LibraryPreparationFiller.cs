using Map.Assets;

namespace Map.Presentation.Services;

/// <summary>
/// Fills the shared preparations (<c>derived/v*/&lt;sha&gt;.mapderived</c>) of every attached drive library in the
/// background, so any machine that attaches the library opens its maps warm (DEC-106, DEC-110 p.1: «для подключённой
/// библиотеки — фоновое заполнение её общего хранилища заготовок»). Until 2026-11-15 only the measurement tools wrote
/// these files and every machine prepared every asset again into its own cache.
/// <para>
/// One asset at a time on a below-normal thread, with a pause between assets; its own store instance (rendering
/// only reads portable files, see <see cref="StampDerivedAssetStore.BuildPortableAsync"/>). Writes only into the
/// library's <c>derived</c> folder, never next to the originals; the owner's own library is covered by the
/// machine cache and is skipped. A library that refuses writes is skipped for the session; work stops while the
/// library's drive has less than <see cref="MinimumFreeBytes"/> free. Attaching a drive starts a new pass.
/// </para>
/// </summary>
public sealed class LibraryPreparationFiller : IAsyncDisposable
{
    public const string DisableVariable = "MAP_LIBRARY_PREFILL";
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    private readonly AssetCatalog _catalog;
    private readonly string _builderRoot;
    private readonly TimeSpan _startDelay;
    private readonly TimeSpan _pause;
    private readonly Func<string, long?> _freeBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _thread;
    private int _built, _failed;

    /// <param name="builderRoot">The builder's private machine folder (its own cache; never a library).</param>
    /// <param name="freeBytes">Test seam; null measures the library's drive.</param>
    public LibraryPreparationFiller(AssetCatalog catalog, string builderRoot, TimeSpan? startDelay = null, TimeSpan? pause = null,
        Func<string, long?>? freeBytes = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _builderRoot = Path.GetFullPath(builderRoot ?? throw new ArgumentNullException(nameof(builderRoot)));
        _startDelay = startDelay ?? TimeSpan.FromSeconds(30);
        _pause = pause ?? TimeSpan.FromMilliseconds(50);
        _freeBytes = freeBytes ?? FreeBytes;
    }

    /// <summary>Assets whose shared preparation this filler wrote, and those it could not prepare.</summary>
    public int Built => Volatile.Read(ref _built);
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>True once a pass found nothing left to prepare (tests and diagnostics).</summary>
    public bool Idle { get; private set; }

    public static bool DisabledByEnvironment => Environment.GetEnvironmentVariable(DisableVariable) == "0";

    public void Start()
    {
        if (_thread is not null || DisabledByEnvironment) return;
        _catalog.MountsChanged += OnMountsChanged;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Map library preparation filler" };
        _thread.Start();
    }

    private void OnMountsChanged(object? sender, EventArgs e) => _wake.Release();

    private void Loop()
    {
        var token = _stop.Token;
        try
        {
            if (token.WaitHandle.WaitOne(_startDelay)) return;
            while (!token.IsCancellationRequested)
            {
                Idle = false;
                Pass(token);
                Idle = true;
                // Sleep until a drive is attached or detached (or once an hour for assets added meanwhile).
                _wake.Wait(TimeSpan.FromHours(1), token);
                while (_wake.CurrentCount > 0) _wake.Wait(0, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Pass(CancellationToken token)
    {
        var own = Path.GetFullPath(_catalog.Root);
        var pending = _catalog.Records
            .Select(record => (Record: record, Root: Path.GetFullPath(_catalog.RootFor(record))))
            .Where(item => !string.Equals(item.Root, own, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pending.Count == 0) return;
        Directory.CreateDirectory(_builderRoot);
        // Own store: the builder's preparations never mix with the editor's working set; a small private budget.
        var store = new StampDerivedAssetStore(_catalog, _builderRoot, diskBudgetBytes: 256L * 1024 * 1024);
        try
        {
            foreach (var (record, root) in pending)
            {
                if (token.IsCancellationRequested) return;
                lock (_refused) if (_refused.Contains(root)) continue;
                if (_freeBytes(root) is { } free && free < MinimumFreeBytes) continue;
                if (_catalog.Find(record.Sha256) is null || store.HasCurrentPortable(record.Sha256)) continue;
                try
                {
                    store.BuildPortableAsync(record.Sha256, token).GetAwaiter().GetResult();
                    Interlocked.Increment(ref _built);
                }
                catch (OperationCanceledException) { return; }
                catch (UnauthorizedAccessException) { lock (_refused) _refused.Add(root); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
                {
                    // A changed, missing or unreadable source: the next pass tries again.
                    Interlocked.Increment(ref _failed);
                }
                if (token.WaitHandle.WaitOne(_pause)) return;
            }
        }
        finally { store.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static long? FreeBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _catalog.MountsChanged -= OnMountsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_thread is { } thread) await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }
}
