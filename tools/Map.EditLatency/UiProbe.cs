using Avalonia;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.Input;
using System.Reflection;
using Map.App.Views;
using Map.Presentation.ViewModels;

internal static class UiProbe
{
    public static void Initialize() => AppBuilder.Configure<Application>()
        .UseStandardRuntimePlatformSubsystem().UseWindowingSubsystem(BindCursors, "NoWindows")
        .UseSkia().UseHarfBuzz().SetupWithoutStarting();
    public static EditorView Create(EditorViewModel editor) => new(editor, () => Task.CompletedTask, () => { }, () => { });
    public static void Drain(EditorView? view = null)
    {
        Dispatcher.UIThread.RunJobs();
        if (view is null) return;
        // Baseline's 250 ms timer delays panels. Invoke its pending work explicitly so the
        // CPU comparison includes those jobs without attributing an artificial sleep to them.
        var flags = typeof(EditorView).GetField("_deferredPanels", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var run = typeof(EditorView).GetMethod("RunDeferredPanel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        while (Convert.ToInt32(flags.GetValue(view)) != 0)
        { run.Invoke(view, null); Dispatcher.UIThread.RunJobs(); }
    }
    public static void Dispose(EditorView view)
    {
        // The view was never attached to a visual root, so no DetachedFromVisualTree is raised.
        foreach (var field in typeof(EditorView).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var value = field.GetValue(view);
            if (value is DispatcherTimer timer) timer.Stop();
            if (value?.GetType().Name == "UiWorkQueue") value.GetType().GetMethod("Clear")!.Invoke(value, null);
        }
    }
    private static void BindCursors()
    {
        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(ICursorFactory)).Invoke(locator, null)!;
        var method = binding.GetType().GetMethod("ToConstant")!;
        if (method.IsGenericMethodDefinition) method = method.MakeGenericMethod(typeof(ICursorFactory));
        method.Invoke(binding, [DispatchProxy.Create<ICursorFactory, NullCursorService>()]);
    }
    public class NullCursorService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
}
