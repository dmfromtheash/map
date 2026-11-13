using Avalonia.Threading;

namespace Map.Presentation.Tests.Support;

// UI tests share one actual dispatcher thread. xUnit worker threads are not UI threads,
// even when collections run serially; error reporting deliberately posts to UIThread.
internal static class EditorUiThread
{
    private static readonly Lazy<Task<Dispatcher>> Started = new(Start);

    public static async Task Run(Action test)
    {
        var dispatcher = await Started.Value.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatcher.InvokeAsync(test);
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiProbe.Initialize();
                var dispatcher = Dispatcher.UIThread;
                ready.SetResult(dispatcher);
                dispatcher.MainLoop(CancellationToken.None);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Map presentation test UI" };
        thread.Start();
        return ready.Task;
    }
}
