using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class DesktopInstanceActivationTests
{
    [Fact]
    public async Task Activation_shows_hidden_window()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        string pipeName = $"net.patchouli.test.pipe.{Guid.NewGuid():N}";

        DesktopInstanceCoordinator primary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        primary.IsPrimary.Should().BeTrue();
        primary.StartListener();

        DesktopInstanceCoordinator secondary = new(new DesktopInstanceCoordinatorOptions(mutexName, pipeName));
        secondary.IsPrimary.Should().BeFalse();

        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
}
