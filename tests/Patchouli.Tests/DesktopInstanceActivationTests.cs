using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class DesktopInstanceActivationTests
{
    [Fact]
    public async Task Closing_window_and_disposing_view_model_share_one_shutdown()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindow window = new(new MainWindowViewModel(), true);
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

    [Fact]
    public async Task Update_exit_action_runs_once_after_confirmation_and_closes_normally()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Failed_update_exit_action_keeps_window_open_and_allows_normal_exit()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
            closed.Should().BeTrue();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_unsaved_settings_confirmation_never_starts_installer_or_closes_window()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            QueuedExitDialogs dialogs = new(ConfirmDialogResult.Cancel, ConfirmDialogResult.Discard);
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            MainWindow window = new(main);
            int launchCount = 0;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            main.Settings.AppearanceSettings.ReadingFontSize++;

            window.RequestExitAfterAsync(() =>
            {
                launchCount++;
                return Task.FromResult(true);
            });
            await Task.Delay(50);

            closed.Should().BeFalse();
            window.IsVisible.Should().BeTrue();
            launchCount.Should().Be(0);
            dialogs.Confirmations.Should().Be(1);

            window.RequestExit();
            closed.Should().BeTrue();
            launchCount.Should().Be(0);
            dialogs.Confirmations.Should().Be(2);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Activation_shows_hidden_window()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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

        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
        public int Confirmations { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel)
            {
                Confirmations++;
                return Task.FromResult((TResult?)(object?)_choices.Dequeue());
            }

            return Task.FromResult(default(TResult?));
        }
    }
}
