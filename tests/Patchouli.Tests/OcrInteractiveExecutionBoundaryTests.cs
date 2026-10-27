using Avalonia.Headless;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

/// <summary>
/// Issue #43 interactive OCR execution boundary: the region-OCR paths keep their modal
/// blocking dialog, but the synchronous adapter/business work must execute off the UI
/// thread, the dispatcher must stay pumpable while it runs, cancellation must still reach
/// the service call, and results may only be applied while the captured library, page,
/// render generation and edit session are still the live ones.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class OcrInteractiveExecutionBoundaryTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    private readonly TemporaryAppSettingsFile _settings = new();
    private MainWindowViewModel? _mainWindow;
    private PdfWorkspaceViewModel? _workspace;

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Region_prefill_executes_synchronous_adapter_off_the_ui_thread_and_stays_pumpable()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main);
            using BlockingRegionAdapter adapter = new("prefill candidate text");
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            await workspace.CreateBoxToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(20, 20);
            workspace.OnPointerMoved(120, 90);
            workspace.OnPointerReleased(false);
            workspace.PendingBBox.Should().NotBeNull("the selection produced a pending region");

            Task prefill = workspace.RunPendingOcrPrefillCommand.ExecuteAsync();
            await adapter.Started.Task.WaitAsync(OperationTimeout);

            dialogs.Blocking.Should().NotBeNull("the modal blocking dialog opened for the operation");
            dialogs.Blocking!.Title.Should().Be("局部 OCR 预填");
            dialogs.Blocking.IsRunning.Should().BeTrue("the dialog stays open while the adapter is blocked");
            dialogs.Blocking.CanCancel.Should().BeFalse("region OCR keeps the non-cancellable dialog");

            // While the adapter is synchronously blocked on its worker thread, the real UI
            // dispatcher must keep pumping jobs — the modal would otherwise freeze exactly like
            // the pre-fix Dispatcher.UIThread.InvokeAsync(operation) defect did.
            for (int pulse = 0; pulse < 3; pulse++)
            {
                bool pumped = await Task.Run(async () =>
                        await Dispatcher.UIThread.InvokeAsync(() => true).GetTask())
                    .WaitAsync(TimeSpan.FromSeconds(10));
                pumped.Should().BeTrue("the UI dispatcher kept pumping while OCR was blocked");
                await Task.Delay(25);
            }

            adapter.Release.TrySetResult(true);
            await prefill.WaitAsync(OperationTimeout);

            adapter.RanOnUiThread.Should().BeFalse(
                "synchronous adapter work must not run on the Avalonia UI dispatcher");
            dialogs.Blocking.OperationState.Should().Be("已成功");
            workspace.NewBoxText.Should().Be("prefill candidate text");
            workspace.Status.Should().Contain("OCR 预填完成");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Region_prefill_discards_stale_result_after_the_edit_session_ends()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main);
            using BlockingRegionAdapter adapter = new("stale candidate text");
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            await workspace.CreateBoxToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(20, 20);
            workspace.OnPointerMoved(120, 90);
            workspace.OnPointerReleased(false);
            workspace.PendingBBox.Should().NotBeNull();

            Task prefill = workspace.RunPendingOcrPrefillCommand.ExecuteAsync();
            await adapter.Started.Task.WaitAsync(OperationTimeout);

            // The draft session ends while the adapter is still blocked: the late completion
            // must never write candidate state into the cleared workspace.
            await workspace.CancelEditModeCommand.ExecuteAsync();
            workspace.EditSessionId.Should().BeNull();

            adapter.Release.TrySetResult(true);
            await prefill.WaitAsync(OperationTimeout);

            adapter.RunCalls.Should().Be(1);
            workspace.NewBoxText.Should().BeEmpty("a stale prefill result is discarded");
            workspace.LocalOcrCandidate.Should().BeNull();
            workspace.Status.Should().NotContain("OCR 预填完成");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Local_ocr_candidate_executes_off_the_ui_thread_even_when_the_runner_is_inline()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            // An inline runner mirrors the no-window path of ModalOperationRunner, where the
            // operation delegate is invoked on the caller thread: only the ViewModel's own
            // Task.Run inside the delegate can provide the execution boundary.
            InlineModalRunner modal = new();
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs, modal);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            using BlockingRegionAdapter adapter = new("local candidate text") { AutoRelease = true };
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            workspace.SelectedBox = workspace.BoundingBoxes.Single(box => !box.IsLogicalPage);
            await workspace.RunLocalOcrCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            modal.Options.Should().ContainSingle(options => options.Title == "局部 OCR");
            adapter.RanOnUiThread.Should().BeFalse(
                "adapter work must stay off the UI dispatcher even without a real dialog task");
            workspace.LocalOcrCandidate.Should().NotBeNull();
            workspace.LocalOcrCandidate!.Payload.Should().BeOfType<TextBoxPayload>()
                .Which.Markdown.Should().Be("local candidate text");
            workspace.LocalOcrSourceText.Should().Be("original baseline text");
            workspace.CandidateBoxes.Should().HaveCount(1);
            workspace.Status.Should().Contain("局部 OCR 候选结果已生成");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Local_ocr_failure_keeps_original_error_behavior_and_creates_no_candidate()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            using BlockingRegionAdapter adapter = new("")
            {
                AutoRelease = true,
                Failure = Result<OcrEnginePageResult>.Failure(
                    AppErrorCodes.InvalidState, "simulated engine failure")
            };
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            workspace.SelectedBox = workspace.BoundingBoxes.Single(box => !box.IsLogicalPage);
            await workspace.RunLocalOcrCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            adapter.RanOnUiThread.Should().BeFalse();
            dialogs.Blocking.Should().NotBeNull();
            dialogs.Blocking!.OperationState.Should().Be("失败");
            workspace.LocalOcrCandidate.Should().BeNull();
            workspace.CandidateBoxes.Should().BeEmpty();
            workspace.Status.Should().Contain("局部 OCR 失败").And.Contain("simulated engine failure");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Workspace_cancellation_reaches_the_blocked_adapter_and_abandons_the_result()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main);
            using BlockingRegionAdapter adapter = new("cancelled candidate text");
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            await workspace.CreateBoxToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(20, 20);
            workspace.OnPointerMoved(120, 90);
            workspace.OnPointerReleased(false);

            Task prefill = workspace.RunPendingOcrPrefillCommand.ExecuteAsync();
            await adapter.Started.Task.WaitAsync(OperationTimeout);

            // Workspace teardown cancels its token; that must reach the business operation
            // even while the adapter is synchronously blocked.
            await workspace.ClearAsync().WaitAsync(OperationTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefill);

            adapter.ObservedToken.IsCancellationRequested.Should().BeTrue(
                "the workspace cancellation token flows into the service call");
            dialogs.Blocking.Should().NotBeNull();
            dialogs.Blocking!.OperationState.Should().Be("已取消");
            workspace.NewBoxText.Should().BeEmpty();
            workspace.EditSessionId.Should().BeNull();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Workspace_teardown_waits_for_work_that_ignores_cancellation()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            AutoCloseDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main);
            using BlockingRegionAdapter adapter = new("late native result") { IgnoreCancellation = true };
            (await main.ServicesAsync()).OcrAdapters.RegisterAdapter(adapter);

            await workspace.CreateBoxToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(20, 20);
            workspace.OnPointerMoved(120, 90);
            workspace.OnPointerReleased(false);
            Task prefill = workspace.RunPendingOcrPrefillCommand.ExecuteAsync();
            await adapter.Started.Task.WaitAsync(OperationTimeout);

            Task<bool> clearing = workspace.ClearAsync();
            clearing.IsCompleted.Should().BeFalse("native work still owns the old workspace resources");
            adapter.Release.TrySetResult(true);
            (await clearing.WaitAsync(OperationTimeout)).Should().BeTrue();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefill);
            workspace.NewBoxText.Should().BeEmpty();
            dialogs.Blocking!.OperationState.Should().Be("已取消");
        }, CancellationToken.None);
    }

    private MainWindowViewModel CreateMainWindow(AutoCloseDialogService dialogs,
        IModalOperationRunner? modalOperations = null)
    {
        MainWindowViewModel main = _mainWindow = new MainWindowViewModel(new FakeClipboard(),
            settingsPath: _settings.Path,
            dialogs: dialogs,
            modalOperations: modalOperations ?? new ModalOperationRunner(dialogs))
        {
            RuntimeDatabasePath = _settings.CreateDatabasePath("ocr-execution-boundary")
        };
        // Region calls must reach the registered adapter: extension engines (the NdlKoten
        // default) route through the compiled-FSX job path and never consult the adapter
        // registry, while the built-in development engine keeps the adapter boundary this
        // fixture measures.
        main.UpdateAppOptions(main.AppOptions with
        {
            OcrEngines = main.AppOptions.OcrEngines with { RegionOcrEngine = OcrEngineIds.Mock }
        });
        return main;
    }

    // Loads the imported PDF on a deterministic 200x200 preview surface, enters edit mode so a
    // draft revision and edit session exist, then returns the ready workspace.
    private async Task<PdfWorkspaceViewModel> OpenEditingWorkspaceAsync(MainWindowViewModel main,
        bool seedBaseline = false)
    {
        string pdf = CreatePdfPath();
        await OpenImportedItemAsync(main, pdf);
        HostServices services = await main.ServicesAsync();
        LibraryItemViewModel item = main.Shell.Items.Single();
        if (seedBaseline)
        {
            DocumentInstanceId documentId = DocumentInstanceId.Parse(item.DocumentInstanceId!);
            IReadOnlyList<Page> pages = (await services.Pages.ListPagesAsync(documentId)).Value;
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                documentId, pages[0].PageId, "original baseline text");
        }

        PdfWorkspaceViewModel workspace = _workspace = new PdfWorkspaceViewModel(main, item);
        workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(
            Result<PdfPagePixelBufferLease>.Success(new PdfPagePixelBufferLease(
                new byte[200 * 200 * 4], 200, 200, 800, 0, "test", 200, 200, "v1")));
        await workspace.LoadAsync();
        await workspace.EnterEditModeCommand.ExecuteAsync();
        workspace.IsEditMode.Should().BeTrue();
        return workspace;
    }

    private string CreatePdfPath()
    {
        string root = Path.GetDirectoryName(_settings.Path)!;
        return Path.Combine(root, $"source-{Guid.NewGuid():N}.pdf");
    }

    private static async Task OpenImportedItemAsync(MainWindowViewModel vm, string pdf)
    {
        File.Copy(TestFixtures.RealThreePagePdf, pdf);
        await vm.OpenDatabaseCommand.ExecuteAsync();
        await vm.Library.CreateCommand.ExecuteAsync();
        HostServices services = await vm.ServicesAsync();
        PdfImportResult imported =
            await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "OCR boundary item", null, 3));
        imported.Success.Should().BeTrue(imported.ErrorMessage);
        await vm.Shell.RefreshItemsAsync();
    }

    private async Task DispatchAndShutdownAsync(HeadlessUnitTestSession session, Func<Task> action,
        CancellationToken cancellationToken)
    {
        await session.Dispatch<bool>(async () =>
        {
            try
            {
                await action();
                return true;
            }
            finally
            {
                try
                {
                    if (_workspace is not null)
                    {
                        await _workspace.ClearAsync();
                        _workspace.Dispose();
                        _workspace = null;
                    }
                }
                finally
                {
                    if (_mainWindow is not null)
                    {
                        await _mainWindow.ShutdownAsync();
                        _mainWindow = null;
                    }
                }
            }
        }, cancellationToken);
    }

    // Dialog service that keeps the blocking-operation dialog open until the runner marks it
    // completed, failed or cancelled — the same lifetime a real modal window has — while every
    // other dialog completes immediately.
    private sealed class AutoCloseDialogService : IDialogService
    {
        public BlockingOperationDialogViewModel? Blocking { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            if (viewModel is not BlockingOperationDialogViewModel blocking)
            {
                return Task.CompletedTask;
            }

            Blocking = blocking;
            TaskCompletionSource<object?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            blocking.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BlockingOperationDialogViewModel.IsRunning) &&
                    !blocking.IsRunning)
                {
                    closed.TrySetResult(null);
                }
            };
            return closed.Task;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult<TResult?>(default);
        }
    }

    // Runner that invokes the operation delegate inline on the calling thread, exactly like
    // ModalOperationRunner's no-dialog path, so tests prove the ViewModel's own boundary.
    private sealed class InlineModalRunner : IModalOperationRunner
    {
        public List<ModalOperationOptions> Options { get; } = [];

        public async Task<T> RunAsync<T>(ModalOperationOptions options,
            Func<ModalOperationContext, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            using BlockingOperationDialogViewModel dialog = new();
            return await operation(new ModalOperationContext(dialog, cancellationToken, false));
        }
    }

    // Deterministic NDL-Koten stand-in: records the thread it runs on and then blocks
    // synchronously until released (or cancelled), proving where the business call executes.
    private sealed class BlockingRegionAdapter : IRealOcrAdapter, IDisposable
    {
        private readonly string _recognizedText;

        public BlockingRegionAdapter(string recognizedText)
        {
            _recognizedText = recognizedText;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AutoRelease { get; init; }
        public bool IgnoreCancellation { get; init; }
        public Result<OcrEnginePageResult>? Failure { get; init; }
        public bool RanOnUiThread { get; private set; }
        public int RunCalls { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public string EngineId => OcrEngineIds.Mock;

        public string DisplayName => "Blocking region adapter";

        // A non-Mock kind is required: RunEngineAsync treats Mock-kind adapters as the
        // built-in deterministic engine and never invokes the registered adapter.
        public string Kind => OcrAdapterKind.LocalLibrary;

        public OcrEngineCapability GetCapability()
        {
            return new OcrEngineCapability(EngineId, DisplayName, false, false, true, false,
                true, false, false, false, false, [OcrInputKinds.PageImage, OcrInputKinds.RegionImage],
                "Test adapter");
        }

        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult(
                EngineId, presetVersion.ModelId, null, OcrEnvironmentStatus.Ready, true, "Ready",
                OcrRequiredAction.None, []));
        }

        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ValidateInputAsync(OcrInputDescriptor input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input,
            OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            RanOnUiThread = Dispatcher.UIThread.CheckAccess();
            ObservedToken = cancellationToken;
            Started.TrySetResult(true);
            if (!AutoRelease)
            {
                // Deliberately synchronous, like a native inference call. The timeout is only a
                // failure watchdog so a regression cannot wedge the shared UI test session.
                Release.Task.WaitAsync(OperationTimeout,
                    IgnoreCancellation ? CancellationToken.None : cancellationToken).GetAwaiter().GetResult();
            }

            if (!IgnoreCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(Failure ?? Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(
                input.PageId, true, _recognizedText,
                input.RegionBBox ?? new NormalizedBBox(0, 0, 1, 1), null, null)));
        }

        public void Dispose()
        {
            Release.TrySetResult(true);
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
    }
}
