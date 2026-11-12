using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Downloads;
using Patchouli.Host.Extensions;
using Patchouli.Host.Plugins;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.Infrastructure.Ocr.NdlLite;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class DownloadOperationDialogTests(AvaloniaSharedSessionFixture headless)
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class GatedStream(byte[] first, byte[] second, TaskCompletionSource release)
        : MemoryStream
    {
        private int _phase;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_phase == 0)
            {
                _phase = 1;
                first.CopyTo(buffer);
                return first.Length;
            }

            if (_phase == 1)
            {
                _phase = 2;
                await release.Task.WaitAsync(cancellationToken);
                second.CopyTo(buffer);
                return second.Length;
            }

            return 0;
        }
    }

    private sealed class RecordingDialogService(Func<Task>? show = null) : IDialogService
    {
        public List<object> Shown { get; } = new();
        public bool OnUiThread;

        public Task ShowDialogAsync(object viewModel)
        {
            OnUiThread = Dispatcher.UIThread.CheckAccess();
            Shown.Add(viewModel);
            return show?.Invoke() ?? Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            OnUiThread = Dispatcher.UIThread.CheckAccess();
            Shown.Add(viewModel);
            return Task.FromResult(default(TResult));
        }
    }

    private sealed class RichProgress : IDownloadProgress
    {
        public ConcurrentQueue<double> Scalars { get; } = new();
        public ConcurrentQueue<DownloadFileProgress> Files { get; } = new();

        public TaskCompletionSource<DownloadFileProgress> FirstBytes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstFraction { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(double value)
        {
            Scalars.Enqueue(value);
            if (value is > 0 and < 1)
            {
                FirstFraction.TrySetResult();
            }
        }

        public void ReportFile(DownloadFileProgress value)
        {
            Files.Enqueue(value);
            if (value.State == "running" && value.DownloadedBytes > 0)
            {
                FirstBytes.TrySetResult(value);
            }
        }
    }

    private sealed class ScalarProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value)
        {
            report(value);
        }
    }

    private static RecordingDialogService ObservingDialogs(DownloadTaskService service)
    {
        return new RecordingDialogService(() =>
        {
            SubscriberCount(service).Should().Be(1, "the runner observes the open dialog");
            return Task.CompletedTask;
        });
    }

    private static RecordingDialogService FailingDialogs(
        DownloadTaskService service, InvalidOperationException failure, bool throwSynchronously)
    {
        return new RecordingDialogService(() =>
        {
            SubscriberCount(service).Should().Be(1);
            if (throwSynchronously)
            {
                throw failure;
            }

            return Task.FromException(failure);
        });
    }

    private static int SubscriberCount(DownloadTaskService service)
    {
        FieldInfo? changed = typeof(DownloadTaskService)
            .GetField(nameof(DownloadTaskService.Changed), BindingFlags.Instance | BindingFlags.NonPublic);
        changed.Should().NotBeNull("the field-like event must expose its actual backing delegate");
        return (changed!.GetValue(service) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private sealed class OwnedDialogs(Window owner) : DialogService
    {
        protected override Window? GetMainWindow()
        {
            return owner;
        }
    }

    [Fact]
    public async Task Backgrounding_restores_the_owner_while_the_download_keeps_running()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            Border content = new();
            Window owner = new() { Content = content, Width = 800, Height = 600 };
            OwnedDialogs dialogs = new(owner);
            dialogs.Register<DownloadOperationDialogViewModel, DownloadOperationDialog>();
            await using DownloadTaskService service = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start("m", async (progress, token) =>
                {
                    using CancellationTokenRegistration registration =
                        token.Register(() => cancelled.TrySetResult());
                    started.TrySetResult();
                    await release.Task;
                    return Result.Success();
                }, files:
                [
                    new DownloadFileProgress("a.bin", "queued", 0, 100),
                    new DownloadFileProgress("b.bin", "queued", 0, null)
                ]);
            Task<Result> pending;
            DownloadOperationDialog dialog;
            DownloadOperationDialogViewModel vm;
            try
            {
                owner.Show();
                pending = new DownloadOperationRunner(dialogs).RunAsync("m", service, handle);
                await started.Task.WaitAsync(timeout.Token);
                dialog = owner.OwnedWindows.OfType<DownloadOperationDialog>().Single();
                vm = (DownloadOperationDialogViewModel)dialog.DataContext!;
                content.IsEnabled.Should().BeFalse("the modal download dialog disables the owner");
                vm.ToggleDetailsCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Should().Contain(t => t.Text == "a.bin")
                    .And.Contain(t => t.Text == "b.bin");
                await vm.BackgroundCommand.ExecuteAsync();
                Dispatcher.UIThread.RunJobs();
                owner.OwnedWindows.OfType<DownloadOperationDialog>().Should().BeEmpty(
                    "backgrounding closes the modal dialog");
                content.IsEnabled.Should().BeTrue(
                    "the owner is usable again while the download continues");
                service.List().Single().State.Should().Be("running");
                cancelled.Task.IsCompleted.Should().BeFalse(
                    "backgrounding must not cancel the worker token");
                pending.IsCompleted.Should().BeFalse(
                    "the runner still awaits the real download completion");
            }
            finally
            {
                release.TrySetResult();
                await handle.Completion.WaitAsync(timeout.Token);
                owner.Close();
            }

            (await pending.WaitAsync(timeout.Token)).IsSuccess.Should().BeTrue();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Successful_download_automatically_closes_the_owned_dialog_and_restores_the_owner()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            Border content = new();
            Window owner = new() { Content = content, Width = 800, Height = 600 };
            OwnedDialogs dialogs = new(owner);
            dialogs.Register<DownloadOperationDialogViewModel, DownloadOperationDialog>();
            await using DownloadTaskService service = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start("m", async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            });
            try
            {
                owner.Show();
                Task<Result> pending = new DownloadOperationRunner(dialogs).RunAsync("m", service, handle);
                await started.Task.WaitAsync(timeout.Token);
                DownloadOperationDialog dialog = owner.OwnedWindows.OfType<DownloadOperationDialog>().Single();
                content.IsEnabled.Should().BeFalse("the download dialog disables the owner");
                pending.IsCompleted.Should().BeFalse("the worker is still held");

                release.TrySetResult();
                (await pending.WaitAsync(timeout.Token)).IsSuccess.Should().BeTrue();
                dialog.IsVisible.Should().BeFalse("success closes the dialog without a user command");
                owner.OwnedWindows.OfType<DownloadOperationDialog>().Should().BeEmpty();
                content.IsEnabled.Should().BeTrue("automatic close restores the owner");
                SubscriberCount(service).Should().Be(0);
            }
            finally
            {
                release.TrySetResult();
                await handle.Completion.WaitAsync(timeout.Token);
                owner.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static ExtensionManagedFileDownload ExtFile(long? expected = null)
    {
        return new ExtensionManagedFileDownload("model.onnx", "https://example.invalid/m.bin",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "model.onnx"),
            expected);
    }

    [Fact]
    public async Task Runner_called_off_the_dispatcher_shows_the_dialog_on_the_ui_thread()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            RecordingDialogService dialogs = new();
            await using DownloadTaskService service = new();
            DownloadTaskHandle handle = service.Start("m", (_, _) =>
                Task.FromResult(Result.Success()));
            Result result = await Task.Run(() =>
                new DownloadOperationRunner(dialogs).RunAsync("m", service, handle));
            result.IsSuccess.Should().BeTrue();
            dialogs.OnUiThread.Should().BeTrue("the runner marshals dialog creation to the dispatcher");
            dialogs.Shown.Should().ContainSingle().Which.Should()
                .BeOfType<DownloadOperationDialogViewModel>();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task No_window_dialog_completion_does_not_leak_the_subscription()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            await using DownloadTaskService service = new();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            RecordingDialogService dialogs = ObservingDialogs(service);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start("m", async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            });
            try
            {
                SubscriberCount(service).Should().Be(0);
                Task<Result> pending = new DownloadOperationRunner(dialogs).RunAsync("m", service, handle);
                await started.Task.WaitAsync(timeout.Token);
                SubscriberCount(service).Should().Be(0,
                    "a returned no-window show task ends observation even while the worker is held");
                pending.IsCompleted.Should().BeFalse("dialog completion is not download completion");
                release.TrySetResult();
                (await pending.WaitAsync(timeout.Token)).IsSuccess.Should().BeTrue();
                SubscriberCount(service).Should().Be(0);
            }
            finally
            {
                release.TrySetResult();
                await handle.Completion.WaitAsync(timeout.Token);
            }

            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dialog_failure_unsubscribes_but_waits_for_the_actual_worker(bool throwSynchronously)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await using DownloadTaskService service = new();
            InvalidOperationException failure = new("dialog failed");
            RecordingDialogService dialogs = FailingDialogs(service, failure, throwSynchronously);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start("m", async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            });
            try
            {
                SubscriberCount(service).Should().Be(0);
                Task<Result> pending = new DownloadOperationRunner(dialogs).RunAsync("m", service, handle);
                await started.Task.WaitAsync(timeout.Token);
                SubscriberCount(service).Should().Be(0,
                    "both synchronous throws and faulted show tasks end observation immediately");
                pending.IsCompleted.Should().BeFalse("the worker must finish before the dialog failure escapes");
                handle.Completion.IsCompleted.Should().BeFalse();
                release.TrySetResult();
                CancellationToken deadline = timeout.Token;
                Func<Task> finish = async () => { await pending.WaitAsync(deadline); };
                (await finish.Should().ThrowAsync<InvalidOperationException>())
                    .Which.Should().BeSameAs(failure);
                handle.Completion.IsCompletedSuccessfully.Should().BeTrue();
                SubscriberCount(service).Should().Be(0);
            }
            finally
            {
                release.TrySetResult();
                await handle.Completion.WaitAsync(timeout.Token);
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_invokes_service_cancel_once_and_worker_completes()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await using DownloadTaskService service = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int[] cancellationCount = [0];
            DownloadTaskHandle handle = service.Start("m", async (progress, token) =>
            {
                using CancellationTokenRegistration registration =
                    token.Register(() => Interlocked.Increment(ref cancellationCount[0]));
                started.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    return Result.Success();
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await release.Task;
                    throw;
                }
            });
            try
            {
                await started.Task.WaitAsync(timeout.Token);
                DownloadOperationDialogViewModel vm = new(service, handle.TaskId) { Title = "m" };
                vm.Apply(service.List().Single());
                await vm.CancelCommand.ExecuteAsync();
                await vm.CancelCommand.ExecuteAsync();
                await cancelled.Task.WaitAsync(timeout.Token);
                Volatile.Read(ref cancellationCount[0]).Should().Be(1,
                    "only the view-model cancel command requests cancellation, once");
                handle.Completion.IsCompleted.Should().BeFalse(
                    "cancellation cannot complete the handle until worker cleanup finishes");
            }
            finally
            {
                release.TrySetResult();
            }

            (await handle.Completion.WaitAsync(timeout.Token)).ErrorCode.Should()
                .Be(DownloadTaskService.CancelledErrorCode);
            service.List().Single().State.Should().Be("cancelled");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task File_rows_update_real_bindings_when_details_expanded()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            await using DownloadTaskService service = new();
            DownloadOperationDialog window = new();
            DownloadOperationDialogViewModel vm = new(service, Guid.NewGuid());
            try
            {
                window.DataContext = vm;
                vm.Apply(new DownloadTaskStatus(Guid.NewGuid(), "m", "running", 0, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, null, null, null)
                {
                    Files =
                    [
                        new DownloadFileProgress("a.bin", "running", 10, 100),
                        new DownloadFileProgress("b.bin", "queued", 0, null)
                    ],
                    IsIndeterminate = true
                });
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.ToggleDetailsCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                List<TextBlock> texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
                texts.Should().Contain(t => t.Text == "a.bin");
                texts.Should().Contain(t => t.Text == "b.bin");
                vm.Apply(new DownloadTaskStatus(Guid.NewGuid(), "m", "running", 0.5,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null)
                {
                    Files = [new DownloadFileProgress("a.bin", "running", 50, 100)],
                    IsIndeterminate = true
                });
                Dispatcher.UIThread.RunJobs();
                TextBlock bytes = window.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text is not null && t.Text.Contains("/"));
                bytes.Text.Should().Contain("50");
                ProgressBar fileProgress = window.GetVisualDescendants().OfType<ProgressBar>()
                    .Single(bar => bar.DataContext is DownloadFileRowViewModel);
                fileProgress.IsIndeterminate.Should().BeFalse();
                fileProgress.Value.Should().Be(50, "the file row binds the reported byte fraction");
            }
            finally
            {
                await vm.BackgroundCommand.ExecuteAsync();
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Background_command_requests_close_without_cancelling_the_worker()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            RecordingDialogService dialogs = new();
            await using DownloadTaskService service = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadTaskHandle handle = service.Start("m", async (progress, token) =>
            {
                started.TrySetResult();
                await release.Task;
                return Result.Success();
            });
            Task<Result> pending;
            try
            {
                pending = new DownloadOperationRunner(dialogs).RunAsync("m", service, handle);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                DownloadOperationDialogViewModel vm = dialogs.Shown
                    .OfType<DownloadOperationDialogViewModel>().Single();
                bool closeInvoked = false;
                vm.RequestClose = _ => closeInvoked = true;
                await vm.BackgroundCommand.ExecuteAsync();
                vm.BackgroundRequested.Should().BeTrue();
                closeInvoked.Should().BeTrue("background closes the dialog window");
                service.List().Single().State.Should().Be("running",
                    "backgrounding never cancels the registered worker");
                pending.IsCompleted.Should().BeFalse(
                    "the runner still awaits the real download completion");
            }
            finally
            {
                release.TrySetResult();
            }

            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ShowAsync_reopens_the_same_task_without_new_admission()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            RecordingDialogService dialogs = new();
            await using DownloadTaskService service = new();
            DownloadTaskHandle handle = service.Start("m", (_, _) =>
                Task.FromResult(Result.Success()));
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await new DownloadOperationRunner(dialogs)
                .ShowAsync("m", service, handle.TaskId);
            service.List().Should().ContainSingle(
                "viewing an existing task never registers another download");
            DownloadOperationDialogViewModel vm = dialogs.Shown
                .OfType<DownloadOperationDialogViewModel>().Single();
            vm.IsRunning.Should().BeFalse("the reopened task is already terminal");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NdlKoten_and_RapidOcr_with_runner_each_register_one_task()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using DownloadTaskService downloads = new();
        NdlKotenModelDownloadService koten = new(new HttpClient(handler), dir, downloads);
        (await koten.DownloadAllAsync()).IsFailure.Should().BeTrue();
        DownloadTaskStatus kotenTask = downloads.List().Should().ContainSingle().Subject;
        kotenTask.State.Should().Be("failed");
        kotenTask.Files.Should().NotBeEmpty();

        await using DownloadTaskService downloads2 = new();
        RapidOcrModelDownloadService rapid = new(new HttpClient(handler), dir, downloads2);
        (await rapid.DownloadAllAsync()).IsFailure.Should().BeTrue();
        DownloadTaskStatus rapidTask = downloads2.List().Should().ContainSingle().Subject;
        rapidTask.State.Should().Be("failed");
        rapidTask.Files.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Plugin_service_reports_content_length_total_and_bytes()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] first = new byte[64];
        byte[] second = new byte[64];
        StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedStream(first, second, release))
            };
            response.Content.Headers.ContentLength = first.Length + second.Length;
            return response;
        });
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using PluginManagedFileService service = new(root, new HttpMessageInvoker(handler));
        string destination = Path.Combine(root, "p", "m.bin");
        PluginManagedFilesPlan plan = new("p", "P",
            [new PluginManagedFileDownload("m.bin", "https://example.invalid/m", destination)]);
        RichProgress rich = new();
        Task<Result> pending;
        try
        {
            pending = service.DownloadAsync(plan, rich);
            DownloadFileProgress mid = await rich.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10));
            mid.TotalBytes.Should().Be(128,
                "Content-Length supplies the total when the manifest pins none");
        }
        finally
        {
            release.TrySetResult();
        }

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
        rich.Files.ToArray().Should().Contain(f =>
                f.State == "succeeded" && f.DownloadedBytes == 128 && f.TotalBytes == 128,
            "the terminal row preserves actual bytes and the Content-Length total");
    }

    [Fact]
    public async Task NdlLite_with_runner_registers_one_task_with_manifest_rows()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        await using DownloadTaskService downloads = new();
        NdlLiteModelDownloadService service = new(new HttpClient(handler),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), downloads)
        {
            VerifyContentHash = false
        };
        Result result = await service.DownloadAllAsync();
        result.IsFailure.Should().BeTrue();
        DownloadTaskStatus task = downloads.List().Should().ContainSingle(
            "the legacy downloader registers exactly one queue task").Subject;
        task.State.Should().Be("failed");
        task.Files.Should().NotBeEmpty("manifest rows are seeded at admission");
        task.Files.Should().Contain(file => file.State == "failed");
    }

    [Fact]
    public async Task Layout_with_runner_registers_one_task_and_forwards_external_progress()
    {
        await using DownloadTaskService downloads = new();
        LayoutModelFile file = new("test-model", "https://example.invalid/m", 4,
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        byte[] payload = "test"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        List<double> scalars = new();
        LayoutModelDownloadService service = new(new HttpClient(handler),
            _ => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "m.onnx"),
            downloads);
        Result result = await service.DownloadAsync(file, new ScalarProgress(scalars.Add));
        result.IsSuccess.Should().BeTrue();
        downloads.List().Should().ContainSingle().Which.State.Should().Be("succeeded");
        scalars.Should().NotBeEmpty("the external scalar reporter is not dropped");
    }

    [Fact]
    public async Task Extension_service_reports_content_length_and_rejects_truncated_body()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] first = new byte[64];
        byte[] second = new byte[64];
        StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedStream(first, second, release))
            };
            response.Content.Headers.ContentLength = first.Length + second.Length;
            return response;
        });
        await using ExtensionManagedFileService service = new(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => "x",
            new HttpMessageInvoker(handler));
        ExtensionManagedFilesPlan plan = new("ext", "Ext",
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0), [ExtFile()]);
        RichProgress rich = new();
        Task<Result> pending;
        try
        {
            pending = service.DownloadAsync(plan, rich);
            DownloadFileProgress mid = await rich.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10));
            mid.TotalBytes.Should().Be(128, "Content-Length supplies the unknown manifest total");
            await rich.FirstFraction.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rich.Scalars.Should().Contain(v => v > 0 && v < 1,
                "scalar callers also receive Content-Length-based fractions");
        }
        finally
        {
            release.TrySetResult();
        }

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
        rich.Files.Should().Contain(f => f.State == "verifying" && f.DownloadedBytes == 128);
        rich.Files.Should().Contain(f => f.State == "succeeded" && f.DownloadedBytes == 128);
    }

    [Fact]
    public async Task Extension_service_fails_when_body_is_shorter_than_declared_length()
    {
        StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16])
            };
            response.Content.Headers.ContentLength = 64;
            return response;
        });
        await using ExtensionManagedFileService service = new(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => "x",
            new HttpMessageInvoker(handler));
        Result result = await service.DownloadAsync(new ExtensionManagedFilesPlan("ext", "Ext",
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
            [ExtFile()]), new RichProgress());
        result.IsFailure.Should().BeTrue("a truncated declared body must not install");
    }
}
