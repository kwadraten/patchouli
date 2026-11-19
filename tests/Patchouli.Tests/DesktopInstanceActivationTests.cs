using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

// R13 shared session: every case still owns its windows, view models, settings file and
// coordinators inside the dispatch; only the dedicated create/dispose stress theory keeps
// independent sessions off the shared path.
[Collection("RetainedTabUI")]
public sealed class DesktopInstanceActivationTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Closing_window_and_disposing_view_model_share_one_shutdown()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            MainWindow window = new(main);
            window.Show();
            window.Close();
            Task first = main.ShutdownAsync();
            main.ShutdownAsync().Should().BeSameAs(first);
            await first;
            await main.DisposeAsync();
            // A returned value binds this delegate to the fully awaited
            // Dispatch<T>(Func<Task<T>>) overload; a bare Func<Task> binds to
            // Dispatch<Task>(Func<Task>), which never awaits the nested task.
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headless_session_supports_immediate_disposal(bool asynchronously)
    {
        // Avalonia #22222: the worker task must exist before StartNew publishes the session,
        // and shutdown must wait for the worker before completing its input queue.
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
            if (asynchronously)
            {
                await session.DisposeAsync();
            }
            else
            {
                session.Dispose();
            }
        }
    }

    [Fact]
    public async Task Closing_tray_enabled_window_hides_it_without_closing()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            // Confirmed tray host: close-to-tray stays the historical behavior.
            MainWindow window = new(new MainWindowViewModel(), true, () => Task.FromResult(true));
            bool closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            window.Close();

            window.IsVisible.Should().BeFalse();
            closed.Should().BeFalse();

            window.RequestExit();
            closed.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    // Issue #74: hiding is only safe while a tray host can reach the app. On hosts like Sway
    // without a StatusNotifierWatcher the window must close for real so the process exits.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_without_confirmed_tray_host_exits_instead_of_hiding(bool probeFails)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            MainWindow window = new(new MainWindowViewModel(), true,
                () => probeFails
                    ? Task.FromException<bool>(new InvalidOperationException("probe failure"))
                    : Task.FromResult(false));
            TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closedSignal.TrySetResult();

            window.Show();
            window.Close();
            await closedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            window.IsVisible.Should().BeFalse();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_before_probe_settles_waits_then_hides_when_host_exists()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow window = new(new MainWindowViewModel(), true, () => probe.Task);
            bool closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            window.Close();
            await Task.Delay(50);
            window.IsVisible.Should().BeTrue("an undecided close must not hide yet");
            closed.Should().BeFalse();

            probe.TrySetResult(true);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (window.IsVisible && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            window.IsVisible.Should().BeFalse();
            closed.Should().BeFalse();

            window.RequestExit();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_before_probe_settles_waits_then_exits_when_no_host()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow window = new(new MainWindowViewModel(), true, () => probe.Task);
            TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closedSignal.TrySetResult();

            window.Show();
            window.Close();
            await Task.Delay(50);
            window.IsVisible.Should().BeTrue("an undecided close must not exit yet");

            probe.TrySetResult(false);
            await closedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_with_never_settling_probe_exits_once_without_reentry()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            // The probe never completes and the wait bound (2 s) is shorter than the
            // probe's own bound, so the deferred close must time out into exactly one
            // real exit path: a synchronous Close() re-entering OnClosing would loop
            // on the still-pending probe.
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow window = new(new MainWindowViewModel(), true, () => probe.Task);
            List<bool> cancels = [];
            int closedCount = 0;
            window.Closing += (_, e) => cancels.Add(e.Cancel);
            window.Closed += (_, _) => closedCount++;

            window.Show();
            window.Close();
            window.IsVisible.Should().BeTrue("the first close is deferred to the probe wait");
            cancels.Should().Equal(true);

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (closedCount == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            closedCount.Should().Be(1,
                "the timed-out probe closes for real exactly once instead of re-entering OnClosing");
            cancels.Should().Equal([true, false],
                "the posted follow-up close must pass through OnClosing uncancelled");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Update_exit_action_runs_once_after_confirmation_and_closes_normally()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(async () =>
        {
            MainWindow window = new(new MainWindowViewModel());
            int launchCount = 0;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();

            window.RequestExitAfterAsync(() =>
            {
                launchCount++;
                return Task.FromResult(true);
            });

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!closed && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            closed.Should().BeTrue();
            launchCount.Should().Be(1);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Failed_update_exit_action_keeps_window_open_and_allows_normal_exit()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(async () =>
        {
            MainWindow window = new(new MainWindowViewModel());
            int launchCount = 0;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();

            window.RequestExitAfterAsync(() =>
            {
                launchCount++;
                return Task.FromResult(false);
            });
            await Task.Delay(50);

            closed.Should().BeFalse();
            window.IsVisible.Should().BeTrue();
            launchCount.Should().Be(1);

            window.RequestExit();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!closed && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            closed.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_unsaved_settings_confirmation_never_starts_installer_or_closes_window()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<bool>(async () =>
        {
            QueuedExitDialogs dialogs = new(ConfirmDialogResult.Cancel, ConfirmDialogResult.Discard);
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            MainWindow window = new(main);
            int launchCount = 0;
            bool closed = false;
            // OnClosed awaits the view-model shutdown before raising Closed, so the Closed
            // event is the real completion signal for a confirmed exit instead of a delay.
            TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) =>
            {
                closed = true;
                closedSignal.TrySetResult();
            };
            window.Show();
            main.Settings.AppearanceSettings.ReadingFontSize++;

            TaskCompletionSource cancelledConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialogs.ConfirmationShown += () => cancelledConfirmation.TrySetResult();
            window.RequestExitAfterAsync(() =>
            {
                launchCount++;
                return Task.FromResult(true);
            });
            // The fake dialog completes synchronously, so the cancellation has finished
            // by the time RequestExitAfterAsync returns; wait on its signal, not a delay.
            await cancelledConfirmation.Task.WaitAsync(TimeSpan.FromSeconds(5));

            closed.Should().BeFalse();
            window.IsVisible.Should().BeTrue();
            launchCount.Should().Be(0);
            dialogs.Confirmations.Should().Be(1);

            window.RequestExit();
            await closedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            closed.Should().BeTrue();
            launchCount.Should().Be(0);
            dialogs.Confirmations.Should().Be(2);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Activation_shows_hidden_window()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            MainWindow window = new(new MainWindowViewModel());
            try
            {
                window.Show();
                window.Hide();
                window.IsVisible.Should().BeFalse();

                App.ActivateWindow(window);

                window.IsVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Activation_restores_minimized_window()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(() =>
        {
            MainWindow window = new(new MainWindowViewModel());
            try
            {
                window.Show();
                window.WindowState = WindowState.Minimized;
                window.WindowState.Should().Be(WindowState.Minimized);

                App.ActivateWindow(window);

                window.WindowState.Should().Be(WindowState.Normal);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Real_coordinator_secondary_notification_marshals_to_ui_thread_and_restores_window()
    {
        string mutexName = $"net.patchouli.test.mutex.{Guid.NewGuid():N}";
        string pipeName = NewPipeName();

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.IsPrimary.Should().BeTrue();
        primary.StartListener();

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        secondary.IsPrimary.Should().BeFalse();

        HeadlessUnitTestSession session = headless.Session;
        try
        {
            await session.Dispatch(async () =>
            {
                App app = (App)Avalonia.Application.Current!;
                MainWindow window = new(new MainWindowViewModel());
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Minimized;
                    window.Hide();
                    window.IsVisible.Should().BeFalse();

                    app.SubscribeToActivation(window, primary);

                    // Send real named pipe activation request from background task
                    bool success = await Task.Run(() => secondary.NotifyPrimaryAsync());
                    success.Should().BeTrue();

                    // Bounded polling for UI Dispatcher to process the window activation
                    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                    while ((!window.IsVisible || window.WindowState != WindowState.Normal) &&
                           DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.Delay(20);
                    }

                    window.IsVisible.Should().BeTrue();
                    window.WindowState.Should().Be(WindowState.Normal);
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, CancellationToken.None);
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pre_window_pending_activation_consumed_on_subscribe()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch(async () =>
        {
            App app = (App)Avalonia.Application.Current!;
            MainWindow window = new(new MainWindowViewModel());
            FakeCoordinator fakeCoordinator = new() { HasPending = true };

            try
            {
                window.Show();
                window.WindowState = WindowState.Minimized;
                window.Hide();

                app.SubscribeToActivation(window, fakeCoordinator);

                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while ((!window.IsVisible || window.WindowState != WindowState.Normal) &&
                       DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                window.IsVisible.Should().BeTrue();
                window.WindowState.Should().Be(WindowState.Normal);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    // macOS caps Unix domain socket paths at 104 chars; the temp directory alone
    // takes ~50 and .NET prepends "CoreFxPipe_", so test pipe names stay short.
    private static string NewPipeName()
    {
        return $"pt.{Guid.NewGuid():N}"[..19];
    }

    private sealed class FakeCoordinator : IDesktopInstanceCoordinator
    {
        private Action? _subscriber;
        public bool IsPrimary => true;
        public bool HasPending { get; set; }

        public void StartListener()
        {
        }

        public Task<bool> NotifyPrimaryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> NotifyPrimaryUriAsync(string uri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public IDisposable SubscribeUris(Action<string> callback)
        {
            return new Subscription(() => { });
        }

        public IDisposable Subscribe(Action callback)
        {
            _subscriber = callback;
            if (HasPending)
            {
                HasPending = false;
                callback();
            }

            return new Subscription(() => _subscriber = null);
        }

        public bool TryConsumePendingActivation()
        {
            bool had = HasPending;
            HasPending = false;
            return had;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }

        private sealed class Subscription : IDisposable
        {
            private Action? _action;

            public Subscription(Action action)
            {
                _action = action;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _action, null)?.Invoke();
            }
        }
    }

    private sealed class QueuedExitDialogs(params ConfirmDialogResult[] choices) : IDialogService
    {
        private readonly Queue<ConfirmDialogResult> _choices = new(choices);
        public event Action? ConfirmationShown;
        public int Confirmations { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel)
            {
                ConfirmDialogResult choice = _choices.Dequeue();
                Confirmations++;
                ConfirmationShown?.Invoke();
                return Task.FromResult((TResult?)(object?)choice);
            }

            return Task.FromResult(default(TResult?));
        }
    }
}
