using Avalonia.Controls;
using Avalonia.Interactivity;
using Map.App.Controls;
using Map.App.Views;

namespace Map.Presentation.Tests;

public sealed class UiAsyncButtonTests
{
    [Fact]
    public void Both_async_button_factories_restore_state_and_report_synchronous_and_task_failures()
    {
        // Only the existing CPU geometry backend for the command glyph: no application or window.
        Avalonia.Skia.SkiaPlatform.Initialize();
        foreach (var command in new[] { false, true })
            foreach (var synchronousThrow in new[] { false, true })
            {
                var error = new InvalidOperationException("handler failed");
                var reports = new List<(string Operation, Exception Error)>();
                var fail = true;
                Button? button = null;
                Task Action()
                {
                    Assert.True(button!.IsEnabled); // the editor's own enabled state is left alone
                    if (!fail) return Task.CompletedTask;
                    if (synchronousThrow) throw error;
                    return Task.FromException(error);
                }
                button = command
                    ? Ui.AsyncCommandButton("ThrowingCommand", "Test", MapIcons.Save, Action)
                    : Ui.AsyncButton("ThrowingButton", "Test", Action);
                var originalContent = button.Content;
                var log = new Support.FakeLog();
                UiTasks.SetErrorReporter(button, (operation, exception) =>
                {
                    log.Error(operation, exception);
                    reports.Add((operation, exception));
                });

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(button.IsEnabled);
                Assert.Same(originalContent, button.Content);
                var report = Assert.Single(reports);
                Assert.Equal(button.Name, report.Operation);
                Assert.Same(error, report.Error);
                Assert.Single(log.Lines);

                fail = false;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(button.IsEnabled);
                Assert.Single(reports);

                // Failure of the reporting mechanism must not bypass the button's finally block.
                fail = true;
                UiTasks.SetErrorReporter(button, (_, _) => throw new IOException("diagnostics full"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(button.IsEnabled);
            }
    }

    [Fact]
    public void A_second_click_while_the_action_runs_is_ignored()
    {
        Avalonia.Skia.SkiaPlatform.Initialize();
        var pending = new TaskCompletionSource();
        var calls = 0;
        var button = Ui.AsyncButton("SlowButton", "Test", () => { calls++; return pending.Task; });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, calls);
        pending.SetResult();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Fire_and_forget_observes_a_later_fault_and_a_throw_before_returning_a_task()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new ArgumentException("late failure");
        UiTasks.Forget(() => pending.Task, (_, error) => reported.TrySetResult(error));
        pending.SetException(failure);
        Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Exception? immediate = null;
        UiTasks.Forget(() => throw failure, (_, error) => immediate = error);
        Assert.Same(failure, immediate);
    }
}
