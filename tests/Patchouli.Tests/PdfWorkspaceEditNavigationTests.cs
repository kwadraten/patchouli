using Avalonia.Headless;
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
/// Edit-mode navigation: page jumps while editing resolve the current draft first — an
/// untouched session auto-discards without prompting, a real edit asks save/discard/cancel —
/// and the workspace re-opens an edit session on the destination page. Sidebar tab switches
/// share the same pending-edit resolution before leaving the box editor surface.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class PdfWorkspaceEditNavigationTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    private readonly TemporaryAppSettingsFile _settings = new();
    private MainWindowViewModel? _mainWindow;
    private PdfWorkspaceViewModel? _workspace;
    private IReadOnlyList<Page> _pages = [];

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Cancelled_search_switch_keeps_the_editor_active_when_search_was_previously_selected()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            await workspace.ShowSearchTabCommand.ExecuteAsync();
            workspace.IsSearchTabActive.Should().BeTrue();
            await workspace.EnterEditModeCommand.ExecuteAsync();
            await EditDraftTextAsync(main, workspace, "keep this draft");
            dialogs.EnqueueUnsavedChoice(null);

            await workspace.ShowSearchTabCommand.ExecuteAsync();

            workspace.IsEditMode.Should().BeTrue();
            workspace.ActiveSidebarTab.Should().Be(SidebarTab.Search);
            workspace.IsSearchTabActive.Should().BeFalse();
            dialogs.UnsavedPromptCount.Should().Be(1);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clean_edit_session_navigates_without_prompt_and_keeps_editing()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            PageEditSessionId firstSession = workspace.EditSessionId!.Value;
            workspace.HasUnsavedPageChanges.Should().BeFalse(
                "merely entering edit mode must not mark the page dirty");

            await workspace.NextPageCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(0, "an untouched draft is auto-discarded");
            workspace.PageIndex.Should().Be(1);
            workspace.IsEditMode.Should().BeTrue("navigation keeps editing on the destination page");
            workspace.EditSessionId.Should().NotBeNull();
            workspace.EditSessionId!.Value.Should().NotBe(firstSession,
                "the destination page gets its own edit session");
            HostServices services = await main.ServicesAsync();
            (await services.DocumentTrees.GetPageEditAsync(firstSession)).IsFailure.Should().BeTrue(
                "the source page's unchanged draft was discarded");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Dirty_edit_prompts_and_cancel_stays_on_page_with_session_intact()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            // Mutate the draft directly at the service layer: the dirty check must catch edits
            // that never flowed through a ViewModel refresh.
            await EditDraftTextAsync(main, workspace, "edited draft text");
            PageEditSessionId sessionId = workspace.EditSessionId!.Value;
            dialogs.EnqueueUnsavedChoice(null);

            await workspace.NextPageCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(1);
            workspace.PageIndex.Should().Be(0);
            workspace.IsEditMode.Should().BeTrue();
            workspace.EditSessionId!.Value.Should().Be(sessionId,
                "cancel preserves the in-flight edit session");
            workspace.HasUnsavedPageChanges.Should().BeTrue();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Dirty_edit_save_commits_then_navigates_and_keeps_editing()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            await EditDraftTextAsync(main, workspace, "saved draft text");
            PageEditSessionId firstSession = workspace.EditSessionId!.Value;
            dialogs.EnqueueUnsavedChoice(ConfirmDialogResult.Confirm);

            await workspace.NextPageCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(1);
            workspace.PageIndex.Should().Be(1);
            workspace.IsEditMode.Should().BeTrue();
            workspace.EditSessionId!.Value.Should().NotBe(firstSession);
            (await CommittedPageTextAsync(main, 0)).Should().Be("saved draft text",
                "the prompt's save path awaited a real commit before navigating");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Dirty_edit_discard_reverts_then_navigates_and_keeps_editing()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            await EditDraftTextAsync(main, workspace, "doomed draft text");
            PageEditSessionId firstSession = workspace.EditSessionId!.Value;
            dialogs.EnqueueUnsavedChoice(ConfirmDialogResult.Discard);

            await workspace.NextPageCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(1);
            workspace.PageIndex.Should().Be(1);
            workspace.IsEditMode.Should().BeTrue();
            workspace.EditSessionId!.Value.Should().NotBe(firstSession);
            (await CommittedPageTextAsync(main, 0)).Should().Be("original baseline text",
                "discarded edits never reach the committed revision");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Sidebar_switch_while_editing_clean_exits_edit_mode_without_prompt()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);

            await workspace.SwitchSidebarTabAsync(SidebarTab.Chapters).WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(0);
            workspace.IsEditMode.Should().BeFalse();
            workspace.EditSessionId.Should().BeNull();
            workspace.ActiveSidebarTab.Should().Be(SidebarTab.Chapters);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Sidebar_switch_while_editing_dirty_respects_cancel_then_discard()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            await EditDraftTextAsync(main, workspace, "edited draft text");

            await workspace.RefreshBoxesAsync();
            workspace.BoundingBoxes.Single().Text.Should().Be("edited draft text");
            dialogs.EnqueueUnsavedChoice(null);
            await workspace.SwitchSidebarTabAsync(SidebarTab.Chapters).WaitAsync(OperationTimeout);
            workspace.IsEditMode.Should().BeTrue("cancel keeps the box editor surface");
            workspace.ActiveSidebarTab.Should().NotBe(SidebarTab.Chapters);

            dialogs.EnqueueUnsavedChoice(ConfirmDialogResult.Discard);
            await workspace.SwitchSidebarTabAsync(SidebarTab.Chapters).WaitAsync(OperationTimeout);
            dialogs.UnsavedPromptCount.Should().Be(2);
            workspace.IsEditMode.Should().BeFalse();
            workspace.ActiveSidebarTab.Should().Be(SidebarTab.Chapters);
            workspace.BoundingBoxes.Single().Text.Should().Be("original baseline text");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Pending_box_draft_counts_as_unsaved_and_prompts_before_jumping()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);

            await workspace.CreateBoxToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(20, 20);
            workspace.OnPointerMoved(120, 90);
            workspace.OnPointerReleased(false);
            workspace.PendingBBox.Should().NotBeNull();
            workspace.HasUnsavedPageChanges.Should().BeTrue(
                "a drawn but uninserted box is unsaved draft work");

            dialogs.EnqueueUnsavedChoice(ConfirmDialogResult.Discard);
            await workspace.GoToPageAsync(3).WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(1);
            workspace.PageIndex.Should().Be(2);
            workspace.IsEditMode.Should().BeTrue();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Footer_save_and_exit_keeps_committing_the_draft()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            ScriptedDialogService dialogs = new();
            MainWindowViewModel main = CreateMainWindow(dialogs);
            PdfWorkspaceViewModel workspace = await OpenEditingWorkspaceAsync(main, true);
            await EditDraftTextAsync(main, workspace, "footer saved text");

            await workspace.SaveAndExitCommand.ExecuteAsync().WaitAsync(OperationTimeout);

            dialogs.UnsavedPromptCount.Should().Be(0, "the footer save never prompts");
            workspace.IsEditMode.Should().BeFalse();
            workspace.EditSessionId.Should().BeNull();
            (await CommittedPageTextAsync(main, 0)).Should().Be("footer saved text");
        }, CancellationToken.None);
    }

    private MainWindowViewModel CreateMainWindow(ScriptedDialogService dialogs)
    {
        MainWindowViewModel main = _mainWindow = new MainWindowViewModel(new FakeClipboard(),
            settingsPath: _settings.Path,
            dialogs: dialogs,
            modalOperations: new ModalOperationRunner(dialogs))
        {
            RuntimeDatabasePath = _settings.CreateDatabasePath("edit-navigation")
        };
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
        DocumentInstanceId documentId = DocumentInstanceId.Parse(item.DocumentInstanceId!);
        _pages = (await services.Pages.ListPagesAsync(documentId)).Value;
        if (seedBaseline)
        {
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                documentId, _pages[0].PageId, "original baseline text");
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

    // Mutates the draft straight through the edit session's service boundary so the workspace's
    // own box caches stay untouched — the leave guard must still see the change.
    private static async Task EditDraftTextAsync(MainWindowViewModel main,
        PdfWorkspaceViewModel workspace, string text)
    {
        HostServices services = await main.ServicesAsync();
        PageEditSessionId sessionId = workspace.EditSessionId!.Value;
        PageEditSession session = (await services.DocumentTrees.GetPageEditAsync(sessionId)).Value;
        IReadOnlyList<DocumentBox> boxes =
            (await services.DocumentTrees.ListBoxesAsync(session.DraftRevisionId)).Value;
        DocumentBox box = boxes.First(candidate => candidate.BoxType == DocumentBoxType.Text);
        Result result = await services.DocumentTreeEditor.UpdateLeafAsync(sessionId,
            new UpdateLeafCommand(box.BoxId, box.BoxType, new TextBoxPayload(text), box.HeadingLevel,
                box.CodeLanguage));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    private async Task<string?> CommittedPageTextAsync(MainWindowViewModel main, int pageIndex)
    {
        HostServices services = await main.ServicesAsync();
        DocumentInstanceId documentId = DocumentInstanceId.Parse(_workspace!.Item.DocumentInstanceId!);
        DocumentTreeRevision revision =
            (await services.DocumentTrees.GetCurrentRevisionAsync(documentId, _pages[pageIndex].PageId)).Value;
        IReadOnlyList<DocumentBox> boxes =
            (await services.DocumentTrees.ListBoxesAsync(revision.TreeRevisionId)).Value;
        return boxes.FirstOrDefault(box => box.BoxType == DocumentBoxType.Text)?.Payload
            is TextBoxPayload payload
            ? payload.Markdown
            : null;
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
            await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Edit nav item", null, 3));
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

    // Records each unsaved-changes prompt and replays the scripted save/discard/cancel choice;
    // an empty queue answers cancel (null), matching a dialog closed without a result.
    private sealed class ScriptedDialogService : IDialogService
    {
        private readonly Queue<ConfirmDialogResult?> _unsavedChoices = new();

        public int UnsavedPromptCount { get; private set; }

        public void EnqueueUnsavedChoice(ConfirmDialogResult? choice)
        {
            _unsavedChoices.Enqueue(choice);
        }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel && typeof(TResult) == typeof(ConfirmDialogResult))
            {
                UnsavedPromptCount++;
                ConfirmDialogResult? choice = _unsavedChoices.Count > 0 ? _unsavedChoices.Dequeue() : null;
                return Task.FromResult((TResult?)(object)(choice ?? ConfirmDialogResult.Cancel));
            }

            return Task.FromResult<TResult?>(default);
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
