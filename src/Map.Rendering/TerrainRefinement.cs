using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// One terrain preparation at a time, with at most one newer request waiting. Only immutable
/// scene inputs and a finished CPU image cross threads; a window's GPU context never leaves its frame.
/// A result is adopted only for the exact inputs and view that requested it.
/// </summary>
public sealed class TerrainRefinement : IDisposable
{
    private sealed record Request(object[] Parts, WorldBounds View, double Scale,
        Func<SKImage?> Build, Action Ready, object? Tag = null)
    {
        public bool Matches(IReadOnlyList<object> parts, WorldBounds view, double scale) =>
            View == view && Scale == scale && Parts.Length == parts.Count
            && !Parts.Where((part, i) => !Equals(part, parts[i])).Any();
    }

    private readonly object _gate = new();
    private Request? _running, _pending, _finished, _failed;
    private SKImage? _image;
    private bool _disposed;
    public string? LastError { get; private set; }
    public double LastBuildMilliseconds { get; private set; }

    /// <summary>
    /// Set by the renderer when a partial result could not be completed (its base image was gone):
    /// the next request of the stale path rebuilds the whole view instead.
    /// </summary>
    public bool PreferFullBuild { get; set; }

    /// <summary>Transfers ownership of a complete image to the render thread.</summary>
    public SKImage? Take(IReadOnlyList<object> parts, WorldBounds view, double scale) =>
        Take(parts, view, scale, out _);

    /// <summary>
    /// Like <see cref="Take(IReadOnlyList{object}, WorldBounds, double)"/>, with the tag the request
    /// was queued with: a partial result says there how it is laid into the image it completes.
    /// </summary>
    public SKImage? Take(IReadOnlyList<object> parts, WorldBounds view, double scale, out object? tag)
    {
        lock (_gate)
        {
            tag = null;
            if (_finished?.Matches(parts, view, scale) != true) return null;
            var image = _image;
            tag = _finished.Tag;
            _image = null; _finished = null;
            return image;
        }
    }

    /// <returns>False after disposal or a failed build of these inputs; the caller can draw synchronously.</returns>
    public bool Queue(IReadOnlyList<object> parts, WorldBounds view, double scale,
        Func<SKImage?> build, Action ready, object? tag = null)
    {
        lock (_gate)
        {
            if (_disposed || _failed?.Matches(parts, view, scale) == true) return false;
            if (_running?.Matches(parts, view, scale) == true)
            {
                _pending = null; // the camera returned to the running request
                return true;
            }
            if (_pending?.Matches(parts, view, scale) == true || _finished?.Matches(parts, view, scale) == true)
                return true;
            _image?.Dispose(); _image = null; _finished = null;
            var request = new Request([.. parts], view, scale, build, ready, tag);
            if (_running is not null) _pending = request;
            else
            {
                _running = request;
                _ = Task.Run(Run);
            }
            return true;
        }
    }

    private void Run()
    {
        while (true)
        {
            Request request;
            lock (_gate) request = _running!;
            SKImage? image = null;
            string? error = null;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                image = request.Build();
                if (image is null) error = "Terrain surface could not be created.";
            }
            catch (Exception ex) { error = ex.Message; image?.Dispose(); image = null; }
            bool notify, continueWork;
            lock (_gate)
            {
                LastBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                notify = !_disposed && _pending is null;
                if (notify)
                {
                    _image?.Dispose(); _image = image; _finished = image is null ? null : request;
                    _failed = error is null ? null : request; LastError = error;
                }
                else image?.Dispose();
                _running = _disposed ? null : _pending;
                _pending = null;
                continueWork = _running is not null;
            }
            if (notify) request.Ready();
            if (!continueWork) return;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _pending = null; _finished = null; _failed = null;
            _image?.Dispose(); _image = null;
        }
    }
}
