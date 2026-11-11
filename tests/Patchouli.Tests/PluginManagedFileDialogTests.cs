using System.Text.Json;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

/// <remarks>
/// Desktop managed-file confirmation for plugin views: the interaction attached by
/// <see cref="PluginManagedFileDialogs.EnsureAttached"/> confirms one missing-file plan
/// through a real ConfirmDialog, never exposes URL query/userinfo, declines and cancels
/// actionably, and runs the host download callback under the modal-operation runner. The
/// plugin <c>files/status</c> and <c>files/ensure</c> bridge leaves reach
/// <c>HostPluginApi</c> directly; a non-host api surface reports "unsupported" instead of
/// guessing. Nothing here performs real network access.
/// </remarks>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class PluginManagedFileDialogTests(AvaloniaSharedSessionFixture headless)
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
            IPluginManagedFileInteraction? attached = services.PluginFiles.Interaction;
            attached.Should().BeOfType<PluginManagedFileDialogs.DesktopPluginManagedFileInteraction>(
                "installing host services auto-attaches the desktop handler once");

            PluginManagedFileDialogs.EnsureAttached(main, services);
            services.PluginFiles.Interaction.Should().BeSameAs(attached,
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
            PluginManagedFileDialogs.EnsureAttached(main, services);

            bool downloadCalled = false;
            Result result = await services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    downloadCalled = true;
                    return Task.FromResult(Result.Success());
                });

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should()
                .Be(PluginManagedFileDialogs.DesktopPluginManagedFileInteraction.DeclinedCode);
            downloadCalled.Should().BeFalse("declining must not touch the network");
            ConfirmDialogViewModel confirm = dialogs.Shown.OfType<ConfirmDialogViewModel>()
                .Should().ContainSingle("exactly one real confirmation is shown").Subject;
            confirm.Title.Should().Be("下载插件文件");
            confirm.Message.Should().Contain("Demo plugin")
                .And.Contain("example.invalid", "the host is displayed")
                .And.Contain("models/ocr.onnx")
                .And.Contain("destination");
            confirm.Message.Should().NotContain("secret=", "URL query strings never leak");
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
            PluginManagedFileDialogs.EnsureAttached(main, services);

            int downloads = 0;
            Result result = await services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (progress, token) =>
                {
                    downloads++;
                    return Task.FromResult(Result.Success());
                });

            result.IsSuccess.Should().BeTrue();
            downloads.Should().Be(1, "the handler invokes the host's download callback once");
            DownloadTaskStatus task = services.DownloadTasks.List().Should()
                .ContainSingle("the confirmed download registers in the host download registry")
                .Subject;
            task.State.Should().Be(DownloadTaskService.SucceededState);
            task.Files.Should().ContainSingle(file => file.RelativePath == "models/ocr.onnx");
            dialogs.Shown.OfType<DownloadOperationDialogViewModel>().Should().ContainSingle(
                "the dedicated download dialog tracks the registered task");
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
            PluginManagedFileDialogs.EnsureAttached(main, services);

            Result result = await services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(true), (_, _) => Task.FromResult(Result.Success()));

            result.IsSuccess.Should().BeTrue();
            ConfirmDialogViewModel confirm = dialogs.Shown.OfType<ConfirmDialogViewModel>()
                .Should().ContainSingle().Subject;
            confirm.Title.Should().Be("下载并修复插件文件");
            confirm.ConfirmText.Should().Be("下载并修复");
            confirm.ConfirmDanger.Should().BeTrue(
                "replacing an existing mismatched file is a destructive confirmation");
            confirm.Message.Should().Contain("替换")
                .And.Contain("校验不匹配")
                .And.Contain("destination/models/ocr.onnx");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Download_callback_runs_off_the_dispatcher_even_in_inline_modal_fallback()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            RecordingModalRunner modal = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                modalOperations: modal, settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            PluginManagedFileDialogs.EnsureAttached(main, services);

            bool? onDispatcher = null;
            Result result = await services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), (_, _) =>
                {
                    onDispatcher = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
                    return Task.FromResult(Result.Success());
                });

            result.IsSuccess.Should().BeTrue();
            onDispatcher.Should().BeFalse(
                "the host download delegate is pinned to a background task; the inline modal " +
                "fallback must never run file/network work on the UI thread");
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
            PluginManagedFileDialogs.EnsureAttached(main, services);

            using CancellationTokenSource cts = new();
            bool downloadCalled = false;
            Task<Result> pending = services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
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
                .Be(PluginManagedFileDialogs.DesktopPluginManagedFileInteraction.CancelledCode);
            (await dialogs.ClosedSource.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should()
                .Be(ConfirmDialogResult.Cancel,
                    "cancellation posts a real close to the live prompt");
            downloadCalled.Should().BeFalse(
                "a dismissed prompt can never authorize a later download");
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
            PluginManagedFileDialogs.DesktopPluginManagedFileInteraction stale =
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
    public async Task Cancellation_returns_an_actionable_failure()
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
            PluginManagedFileDialogs.EnsureAttached(main, services);

            using CancellationTokenSource cancellation = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Result> pending = services.PluginFiles.Interaction!.ConfirmAndDownloadAsync(
                Plan(), async (_, token) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return Result.Success();
                }, cancellation.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                DownloadTaskStatus task = services.DownloadTasks.List().Should().ContainSingle().Which;
                services.DownloadTasks.Cancel(task.TaskId).IsSuccess.Should().BeTrue();
                Result result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

                result.IsFailure.Should().BeTrue();
                result.ErrorCode.Should()
                    .Be(PluginManagedFileDialogs.DesktopPluginManagedFileInteraction.CancelledCode);
                services.DownloadTasks.List().Should().ContainSingle().Which.State.Should()
                    .Be(DownloadTaskService.CancelledState);
            }
            finally
            {
                await cancellation.CancelAsync();
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_leaves_report_unsupported_when_the_plugin_api_is_not_the_host()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            // A non-HostPluginApi double: fake IMcpPluginApi implementations cannot offer
            // TryManagedFileStatus/EnsureManagedFilesAsync, so both leaves must refuse
            // honestly rather than probe or guess.
            main.PluginApiProvider = () => new StubPluginApi();

            Result<PluginManagedFilesView> status =
                await PluginManagedFileDialogs.StatusAsync(main, "demo");
            Result<PluginManagedFilesView> ensured =
                await PluginManagedFileDialogs.EnsureAsync(main, "demo");

            status.IsFailure.Should().BeTrue();
            status.ErrorCode.Should().Be(PluginManagedFileDialogs.UnsupportedCode);
            ensured.IsFailure.Should().BeTrue();
            ensured.ErrorCode.Should().Be(PluginManagedFileDialogs.UnsupportedCode);
            dialogs.Shown.Should().BeEmpty("an unsupported surface never prompts");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Ensure_override_short_circuits_before_the_host_api()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main =
                new(settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            main.PluginApiProvider = () => new StubPluginApi();
            string? ensuredPlugin = null;
            main.PluginFilesEnsureOverride = (pluginId, _) =>
            {
                ensuredPlugin = pluginId;
                return Task.FromResult(Result<PluginManagedFilesView>.Success(
                    new PluginManagedFilesView(pluginId, "completed",
                    [
                        new PluginManagedFileView("models/ocr.onnx", "present", 4096)
                    ])));
            };

            Result<PluginManagedFilesView> result =
                await PluginManagedFileDialogs.EnsureAsync(main, "demo");

            result.IsSuccess.Should().BeTrue();
            result.Value.State.Should().Be("completed");
            ensuredPlugin.Should().Be("demo");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public void ToView_projects_sanitized_status_and_never_exposes_urls_or_local_paths()
    {
        PluginManagedFilesStatus status = new("demo", "Demo plugin",
        [
            new PluginManagedFileStatus("models/ocr.onnx", false, false, null, "aabb", 4096,
                true, false, "https://example.invalid/m?secret=1", "1.2.0"),
            new PluginManagedFileStatus("models/table.onnx", false, true, 2048, "ccdd", 2048,
                false, true, "https://example.invalid/t?secret=2", "1.0.0"),
            new PluginManagedFileStatus("models/lang.bin", true, true, 128, null, 128,
                false, false, null, null)
        ]);

        PluginManagedFilesView view =
            PluginManagedFileDialogs.ToView(Result<PluginManagedFilesStatus>.Success(status)).Value;

        view.PluginId.Should().Be("demo");
        view.State.Should().Be("missing");
        view.Files.Select(file => file.State).Should()
            .Equal("missing", "invalid", "present");
        view.Files[0].ExpectedBytes.Should().Be(4096);
        view.Files[0].Sha256.Should().Be("aabb");
        view.Files[0].Version.Should().Be("1.2.0");
    }

    [Fact]
    public void ToView_marks_everything_ready_as_satisfied()
    {
        PluginManagedFilesStatus status = new("demo", "Demo plugin",
        [
            new PluginManagedFileStatus("models/ocr.onnx", true, true, 4096, null, 4096,
                false, false, null, "1.2.0")
        ]);

        PluginManagedFileDialogs.ToView(Result<PluginManagedFilesStatus>.Success(status))
            .Value.State.Should().Be("satisfied");
    }

    [Fact]
    public void ToView_passes_host_failures_through()
    {
        Result<PluginManagedFilesView> view = PluginManagedFileDialogs.ToView(
            Result<PluginManagedFilesStatus>.Failure(AppErrorCodes.NotFound, "not registered"));

        view.IsFailure.Should().BeTrue();
        view.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        view.ErrorMessage.Should().Be("not registered");
    }

    private static PluginManagedFilesPlan Plan(bool overwrites = false)
    {
        return new PluginManagedFilesPlan("demo", "Demo plugin",
        [
            new PluginManagedFileDownload("models/ocr.onnx",
                "https://example.invalid/models/ocr.onnx?secret=leak-me",
                "destination/models/ocr.onnx", 4096)
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

    private sealed class RecordingModalRunner : IModalOperationRunner
    {
        public List<ModalOperationOptions> Options { get; } = [];
        public List<BlockingOperationDialogViewModel> BlockingDialogs { get; } = [];

        public async Task<T> RunAsync<T>(ModalOperationOptions options,
            Func<ModalOperationContext, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            using BlockingOperationDialogViewModel dialog = new();
            BlockingDialogs.Add(dialog);
            ModalOperationContext context = new(dialog, cancellationToken, false);
            T result = await Task.Run(() => operation(context), cancellationToken);
            context.ApplyPendingUpdates();
            return result;
        }
    }

    private sealed class CancellingModalRunner : IModalOperationRunner
    {
        public async Task<T> RunAsync<T>(ModalOperationOptions options,
            Func<ModalOperationContext, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            using BlockingOperationDialogViewModel dialog = new();
            return await operation(new ModalOperationContext(dialog, cancelled.Token, false));
        }
    }

    /// <summary>A minimal non-host api double: present but without the managed-file surface.</summary>
    private sealed class StubPluginApi : IMcpPluginApi
    {
        public IReadOnlyList<McpPluginMountInfo> Mounts => [];

        public McpPluginMountInfo? FindMount(string pluginId)
        {
            return null;
        }

        public bool AllowsVerb(string pluginId, string verb)
        {
            return true;
        }

        public bool IsEndpoint(string pluginId, string relativePath)
        {
            return false;
        }

        public IReadOnlyList<McpPluginViewInfo> Views(string pluginId)
        {
            return [];
        }

        public McpPluginViewInfo? FindView(string pluginId, string viewId)
        {
            return null;
        }

        public Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath)
        {
            return Result<McpPluginFetchContent?>.Success(null);
        }

        public Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope,
            string? query, int limit, string? cursor, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId, string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath,
            string action, JsonElement args, string? requestId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
