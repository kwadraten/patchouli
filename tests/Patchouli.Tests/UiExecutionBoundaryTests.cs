using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Performance;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class UiExecutionBoundaryTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Guard_counts_every_entry_but_deduplicates_diagnostics_and_ignores_background_work()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            string resource = "guard-test-" + Guid.NewGuid().ToString("N");
            UiThreadExecutionGuard.Check(resource);
            await Task.Run(() => UiThreadExecutionGuard.Check(resource));
            UiThreadExecutionGuard.Check(resource);
            UiThreadExecutionGuard.GetCount(resource).Should().Be(2);
            UiThreadExecutionGuard.GetViolations().Where(violation => violation.Resource == resource)
                .Should().ContainSingle().Which.Stack.Should().NotBeNullOrWhiteSpace();
            Func<bool>? previous =
                UiThreadExecutionGuard.Configure(() => throw new InvalidOperationException("broken probe"));
            try
            {
                Action check = () => UiThreadExecutionGuard.Check(resource);
                check.Should().NotThrow("diagnostics cannot change business behaviour");
                UiThreadExecutionGuard.Configure(null);
                UiThreadExecutionGuard.Check(resource);
                UiThreadExecutionGuard.GetCount(resource).Should().Be(2);
            }
            finally
            {
                UiThreadExecutionGuard.Configure(previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_retired_headless_app_does_not_classify_sessionless_workers_as_UI_threads()
    {
        await headless.Session.Dispatch<bool>(() => Task.FromResult(true), CancellationToken.None);
        string resource = "retired-app-test-" + Guid.NewGuid().ToString("N");
        await Task.Run(() => UiThreadExecutionGuard.Check(resource));
        UiThreadExecutionGuard.GetCount(resource).Should().Be(0);
    }

    [Fact]
    public async Task No_window_modal_work_is_background_and_progress_is_applied_only_on_the_calling_dispatcher()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            NoWindowDialogs dialogs = new();
            ModalOperationRunner runner = new(dialogs);
            UiHeartbeatMeasurement heartbeat = await UiHeartbeatProbe.MeasureAsync(async () =>
            {
                int result = await runner.RunAsync(new ModalOperationOptions("测试", "开始", true), async context =>
                {
                    Dispatcher.UIThread.CheckAccess().Should().BeFalse();
                    await context.ReportAsync(1, 2, "一半", "进度明细");
                    await context.AddLogAsync("后台日志");
                    Thread.Sleep(800);
                    return 42;
                });
                result.Should().Be(42);
            });
            heartbeat.Samples.Should().BeGreaterThanOrEqualTo(3);
            heartbeat.MaximumGapMs.Should().BeLessThan(500);
            dialogs.CrossThreadMutation.Should().BeFalse();
            dialogs.ViewModel!.Logs.Should().Contain(log => log.Contains("后台日志"));
            dialogs.ViewModel.OperationState.Should().Be("已成功");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Modal_dialog_blocks_main_window_but_keeps_heartbeat_progress_and_cancellation_live()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            Border content = new();
            Window owner = new() { Content = content, Width = 800, Height = 600 };
            DialogService dialogs = new OwnedDialogs(owner);
            dialogs.Register<BlockingOperationDialogViewModel, BlockingOperationDialog>();
            ModalOperationRunner runner = new(dialogs);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Result>? running = null;
            BlockingOperationDialog? dialog = null;
            try
            {
                owner.Show();
                running = runner.RunAsync(new ModalOperationOptions("慢任务", "开始", true), async context =>
                {
                    Dispatcher.UIThread.CheckAccess().Should().BeFalse();
                    await context.ReportAsync(1, 2, "进度可见");
                    started.TrySetResult();
                    release.Task.Wait(context.CancellationToken);
                    return Result.Success();
                }, timeout.Token);
                await started.Task.WaitAsync(timeout.Token);
                dialog = owner.OwnedWindows.OfType<BlockingOperationDialog>().Single();
                BlockingOperationDialogViewModel viewModel = (BlockingOperationDialogViewModel)dialog.DataContext!;
                // Measure the deterministic blocked-work interval, not cold XAML/theme construction.
                UiHeartbeatMeasurement heartbeat = await UiHeartbeatProbe.MeasureAsync(async () =>
                {
                    content.IsEnabled.Should().BeFalse();
                    dialog.Close();
                    dialog.IsVisible.Should().BeTrue("a running blocking dialog cannot be closed");
                    viewModel.ProgressValue.Should().Be(50);
                    viewModel.StatusMessage.Should().Be("进度可见");
                    await Task.Delay(400, timeout.Token);
                    await viewModel.CancelCommand.ExecuteAsync();
                    while (viewModel.IsRunning)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    viewModel.OperationState.Should().Be("已取消");
                    content.IsEnabled.Should().BeFalse("the result must still be acknowledged");
                    await viewModel.ConfirmCommand.ExecuteAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                    content.IsEnabled.Should().BeTrue();
                }, timeout.Token);
                heartbeat.Samples.Should().BeGreaterThanOrEqualTo(2);
                heartbeat.MaximumGapMs.Should().BeLessThan(500);
            }
            finally
            {
                release.TrySetResult();
                timeout.Cancel();
                if (dialog?.DataContext is BlockingOperationDialogViewModel viewModel)
                {
                    viewModel.MarkCancelled();
                    dialog.Close();
                }

                if (running is not null)
                {
                    try
                    {
                        await running;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                owner.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private sealed class OwnedDialogs(Window owner) : DialogService
    {
        protected override Window? GetMainWindow()
        {
            return owner;
        }
    }

    private sealed class NoWindowDialogs : IDialogService
    {
        public BlockingOperationDialogViewModel? ViewModel { get; private set; }
        public bool CrossThreadMutation { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            ViewModel = (BlockingOperationDialogViewModel)viewModel;
            ViewModel.PropertyChanged += (_, _) => CrossThreadMutation |= !Dispatcher.UIThread.CheckAccess();
            ViewModel.Logs.CollectionChanged += (_, _) => CrossThreadMutation |= !Dispatcher.UIThread.CheckAccess();
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult(default(TResult));
        }
    }
}
