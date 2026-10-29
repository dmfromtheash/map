using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;

namespace Map.App.Views;

/// <summary>Observes UI tasks, including failures thrown before a task is returned.</summary>
public sealed class UiTasks : AvaloniaObject
{
    public static readonly AttachedProperty<Action<string, Exception>?> ErrorReporterProperty =
        AvaloniaProperty.RegisterAttached<UiTasks, Control, Action<string, Exception>?>(
            "ErrorReporter", inherits: true);

    public static void SetErrorReporter(Control control, Action<string, Exception>? reporter) =>
        control.SetValue(ErrorReporterProperty, reporter);

    public static Action<string, Exception>? GetErrorReporter(Control control) =>
        control.GetValue(ErrorReporterProperty);

    public static void Forget(Control source, Func<Task> action,
        [CallerArgumentExpression(nameof(action))] string operation = "") =>
        _ = RunAsync(source, action, operation);

    public static Task RunAsync(Control source, Func<Task> action,
        [CallerArgumentExpression(nameof(action))] string operation = "") =>
        RunAsync(action, (name, error) => Report(source, name, error), operation);

    public static void Forget(Func<Task> action, Action<string, Exception> reporter,
        [CallerArgumentExpression(nameof(action))] string operation = "") =>
        _ = RunAsync(action, reporter, operation);

    private static async Task RunAsync(Func<Task> action, Action<string, Exception> reporter,
        [CallerArgumentExpression(nameof(action))] string operation = "")
    {
        try { await action(); }
        catch (OperationCanceledException) { } // A cancelled search/preview is expected, not an error.
        catch (Exception error)
        {
            try { reporter(operation, error); }
            catch (Exception reportingError)
            {
                Trace.TraceError($"{operation}: {error}; reporting failed: {reportingError}");
            }
        }
    }

    // Buttons whose action is still running. IsEnabled is not used for this: the editor's sync enables and disables
    // some of these buttons itself while an action runs, and restoring an old value would undo that.
    private static readonly ConditionalWeakTable<Button, object> Running = new();

    public static async Task RunButtonAsync(Button button, Func<Task> action)
    {
        if (!button.IsEnabled || !Running.TryAdd(button, Running)) return; // a second click while running is ignored
        try { await RunAsync(button, action, button.Name ?? "Async button"); }
        finally { Running.Remove(button); }
    }

    public static void Report(Control source, string operation, Exception error)
    {
        // Reporting must not become a second unhandled exception (e.g. a full diagnostics disk).
        void ReportOnUiThread()
        {
            try
            {
                var reporter = GetErrorReporter(source);
                // Owned dialogs and flyouts may have their own inheritance root.
                for (var window = TopLevel.GetTopLevel(source) as Window;
                     reporter is null && window is not null; window = window.Owner as Window)
                    reporter = GetErrorReporter(window);
                if (reporter is null && Application.Current?.ApplicationLifetime is
                    IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
                    reporter = GetErrorReporter(main);
                if (reporter is not null) reporter(operation, error);
                else Trace.TraceError($"{operation}: {error}");
            }
            catch (Exception reportingError)
            {
                Trace.TraceError($"{operation}: {error}; error reporting failed: {reportingError}");
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) ReportOnUiThread();
        else Dispatcher.UIThread.Post(ReportOnUiThread);
    }
}
