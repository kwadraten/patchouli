using Patchouli.Core.Downloads;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.Host.Extensions;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <remarks>
/// Desktop managed-file confirmation (screenshot revision): the interaction attached by
/// <see cref="OcrManagedFileDialogs.EnsureAttached"/> confirms one missing-file plan through a
/// real ConfirmDialog, never exposes URL query/userinfo, declines and cancels actionably, and
/// runs the host download callback under the modal-operation runner. Without a handler the
/// host service must fail honestly — nothing here performs real network access.
/// </remarks>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class OcrManagedFileDialogTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task EnsureAttached_registers_one_interaction_and_stays_idempotent()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IExtensionManagedFileInteraction? attached = services.ExtensionFiles.Interaction;
            attached.Should().BeOfType<OcrManagedFileDialogs.DesktopManagedFileInteraction>(
                "installing host services auto-attaches the desktop handler once");

            OcrManagedFileDialogs.EnsureAttached(main, services);
            services.ExtensionFiles.Interaction.Should().BeSameAs(attached,
                "a second attach for the same main/services keeps the original handler");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Declining_confirmation_returns_failure_without_calling_download()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Cancel };
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            bool downloadCalled = false;
            Result result = await services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    downloadCalled = true;
                    return Task.FromResult(Result.Success());
                });

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should()
                .Be(OcrManagedFileDialogs.DesktopManagedFileInteraction.DeclinedCode);
            downloadCalled.Should().BeFalse("declining must not touch the network");
            ConfirmDialogViewModel confirm = dialogs.Shown.OfType<ConfirmDialogViewModel>()
                .Should().ContainSingle("exactly one real confirmation is shown").Subject;
            confirm.Message.Should().Contain("example.invalid", "the host is displayed");
            confirm.Message.Should().NotContain("secret=", "URL query strings never leak");
            confirm.Message.Should().Contain("inference.onnx").And.Contain("destination");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Accepted_plan_registers_the_download_with_the_host_registry()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            int downloads = 0;
            Result result = await services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (progress, token) =>
                {
                    downloads++;
                    progress?.Report(0.5);
                    return Task.FromResult(Result.Success());
                });

            result.IsSuccess.Should().BeTrue();
            downloads.Should().Be(1, "the handler invokes the host's download callback once");
            DownloadTaskStatus task = services.DownloadTasks.List().Should()
                .ContainSingle("the confirmed download registers in the host download registry")
                .Subject;
            task.State.Should().Be(DownloadTaskService.SucceededState);
            task.Progress.Should().Be(1.0);
            // The runner applies no-window progress back on the calling dispatcher before returning.
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_registered_download_reports_live_progress_while_the_result_stays_pending()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            TaskCompletionSource reportsSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Result> pending = services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), async (progress, token) =>
                {
                    (progress as IDownloadProgress)?.ReportFile(
                        new DownloadFileProgress("inference.onnx", "running", 512, 1024));
                    reportsSent.TrySetResult();
                    await release.Task;
                    return Result.Success();
                });

            try
            {
                await reportsSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
                DownloadTaskStatus running = services.DownloadTasks.List().Single();
                running.State.Should().Be(DownloadTaskService.RunningState,
                    "the confirmed download registers while still running");
                running.Progress.Should().BeApproximately(0.5, 0.001);
                pending.IsCompleted.Should().BeFalse(
                    "the interaction awaits the real download completion, never fire-and-forget");
            }
            finally
            {
                release.TrySetResult();
            }

            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess.Should().BeTrue();
            services.DownloadTasks.List().Single().State.Should()
                .Be(DownloadTaskService.SucceededState);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Repair_plan_shows_replacement_details_with_danger_confirmation()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            Result result = await services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(true), (_, _) => Task.FromResult(Result.Success()));

            result.IsSuccess.Should().BeTrue();
            ConfirmDialogViewModel confirm = dialogs.Shown.OfType<ConfirmDialogViewModel>()
                .Should().ContainSingle().Subject;
            confirm.Title.Should().Be("下载并修复扩展文件");
            confirm.ConfirmText.Should().Be("下载并修复");
            confirm.ConfirmDanger.Should().BeTrue(
                "replacing an existing mismatched file is a destructive confirmation");
            confirm.Message.Should().Contain("替换")
                .And.Contain("校验不匹配")
                .And.Contain("destination/models/inference.onnx");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Download_callback_runs_off_the_dispatcher()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            bool? onDispatcher = null;
            Result result = await services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    onDispatcher = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
                    return Task.FromResult(Result.Success());
                });

            result.IsSuccess.Should().BeTrue();
            onDispatcher.Should().BeFalse(
                "the host download delegate is pinned to a background registry task; file and " +
                "network work must never run on the UI thread");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_while_the_prompt_is_open_closes_it_and_never_downloads()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            BlockingConfirmDialogService dialogs = new();
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            using CancellationTokenSource cts = new();
            bool downloadCalled = false;
            Task<Result> pending = services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    downloadCalled = true;
                    return Task.FromResult(Result.Success());
                }, cts.Token);

            await dialogs.ShownSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Result result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should()
                .Be(OcrManagedFileDialogs.DesktopManagedFileInteraction.CancelledCode);
            (await dialogs.ClosedSource.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should()
                .Be(ConfirmDialogResult.Cancel,
                    "cancellation posts a real close to the live prompt");
            downloadCalled.Should().BeFalse(
                "a dismissed prompt can never authorize a later download");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Library_switch_while_the_prompt_is_open_never_downloads()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            SwitchingDialogService dialogs = new();
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);
            MainWindowViewModel stale = main;
            TemporaryAppSettingsFile switched = settings;
            dialogs.Switch = async () =>
            {
                stale.RuntimeDatabasePath = switched.CreateDatabasePath("switched");
                await stale.OpenDatabaseCommand.ExecuteAsync();
            };

            bool downloadCalled = false;
            Result result = await services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    downloadCalled = true;
                    return Task.FromResult(Result.Success());
                });

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
            downloadCalled.Should().BeFalse(
                "the post-confirmation generation check stops the download into the new library");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_the_registered_task_returns_an_actionable_failure()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrManagedFileDialogs.EnsureAttached(main, services);

            using CancellationTokenSource cts = new();
            Task<Result> pending = services.ExtensionFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Result.Success();
                }, cts.Token);

            for (int attempt = 0;
                 attempt < 400 &&
                 services.DownloadTasks.List().SingleOrDefault()?.State !=
                 DownloadTaskService.RunningState;
                 attempt++)
            {
                await Task.Delay(25);
            }

            services.DownloadTasks.List().SingleOrDefault()?.State.Should()
                .Be(DownloadTaskService.RunningState, "the confirmed download registered");
            await cts.CancelAsync();
            Result result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should()
                .Be(OcrManagedFileDialogs.DesktopManagedFileInteraction.CancelledCode);
            services.DownloadTasks.List().Single().State.Should()
                .Be(DownloadTaskService.CancelledState);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_library_context_blocks_download_before_any_dialog()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            // A handler captured against a different library generation is stale by definition.
            OcrManagedFileDialogs.DesktopManagedFileInteraction stale =
                new(main, services, main.LibraryGeneration + 1);
            bool downloadCalled = false;
            Result result = await stale.ConfirmAndDownloadAsync(Plan(), (_, _) =>
            {
                downloadCalled = true;
                return Task.FromResult(Result.Success());
            });

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
            downloadCalled.Should().BeFalse();
            dialogs.Shown.Should().BeEmpty("a stale context never even asks the user");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Without_a_handler_the_service_fails_honestly_without_network()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            // The desktop host auto-attaches its handler; clear it to exercise the honest
            // no-handler failure the headless/embedder service contract still requires.
            services.ExtensionFiles.Interaction = null;

            // A synthetic declaration whose managed destination cannot exist on this machine.
            ConfigFieldDescriptor field = new("managed-dir", "Managed dir",
                ConfigValueKind.DirectoryPath, false, "", "")
            {
                ManagedFiles =
                [
                    new ExtensionManagedFile("never-exists.bin", "https://example.invalid/x.bin",
                        1)
                ]
            };
            Result ensure = await services.ExtensionFiles.EnsureAsync("ocr.ui-managed-none",
                [field], new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));

            ensure.IsFailure.Should().BeTrue(
                "headless without an interaction is an actionable failure, never a silent download");
            ensure.ErrorCode.Should().Be(AppErrorCodes.NotFound);
            services.TranslationExtensions.Manager.List().Should()
                .BeEmpty("planning is declarative — no activation or download");
            return 0;
        }, CancellationToken.None);
    }

    private static ExtensionManagedFilesPlan Plan(bool overwrites = false)
    {
        return new ExtensionManagedFilesPlan("ocr.multimodal", "Multimodal",
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
            [
                new ExtensionManagedFileDownload("inference.onnx",
                    "https://example.invalid/models/inference.onnx?secret=leak-me",
                    "destination/models/inference.onnx", 1024)
                {
                    OverwritesExisting = overwrites
                }
            ]);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public List<object> Shown { get; } = [];
        public object? Result { get; set; }

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult(Result is TResult typed ? typed : default);
        }
    }

    /// <summary>Real window semantics: stays open until the VM's RequestClose fires.</summary>
    private sealed class BlockingConfirmDialogService : IDialogService
    {
        public TaskCompletionSource<ConfirmDialogViewModel> ShownSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ClosedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel confirm)
            {
                TaskCompletionSource<object?> closed =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                // Subscribe before publishing so a cancellation posted right after ShownSource
                // completes can never miss the close handler.
                confirm.RequestClose += result =>
                {
                    ClosedSource.TrySetResult(result);
                    closed.TrySetResult(result);
                };
                ShownSource.TrySetResult(confirm);
                return (TResult?)await closed.Task;
            }

            return default;
        }
    }

    /// <summary>Accepts the prompt but switches the library while it is open.</summary>
    private sealed class SwitchingDialogService : IDialogService
    {
        public Func<Task>? Switch { get; set; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (Switch is not null)
            {
                await Switch();
            }

            return viewModel is ConfirmDialogViewModel && typeof(TResult) == typeof(ConfirmDialogResult)
                ? (TResult?)(object)ConfirmDialogResult.Confirm
                : default;
        }
    }
}
