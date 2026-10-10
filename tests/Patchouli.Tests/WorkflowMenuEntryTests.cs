using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.Workflows;

namespace Patchouli.Tests;

/// <summary>
///     The workflow menus and the session status-bar notifications (ADR 0036, plan §3.8): every menu
///     surface is generated from one ordered definition source, a click starts the session against the
///     captured selection and immediately shows the chat tab, launch validation errors route to the
///     workflow editor, and a session that finishes or fails notifies the status bar.
/// </summary>
[Collection("Avalonia")]
public sealed class WorkflowMenuEntryTests
{
    [Fact]
    public void The_menu_offers_only_ShowInMenu_definitions_ordered_by_their_menu_order()
    {
        WorkflowDefinition late = Definition("z-late", "晚执行", true, 90);
        WorkflowDefinition early = Definition("a-early", "早执行", true, 10);
        WorkflowDefinition middle = Definition("m-middle", "中间执行", true, 50);
        WorkflowDefinition hidden = Definition("h-hidden", "隐藏流程", false, 1);

        IReadOnlyList<WorkflowDefinition> entries =
            WorkflowMenuService.MenuEntries([late, hidden, middle, early]);

        entries.Should().HaveCount(3);
        entries.Select(definition => definition.Id).Should().Equal("a-early", "m-middle", "z-late");
    }

    [Fact]
    public void The_menu_source_is_shared_by_the_menu_bar_and_the_library_context_menu()
    {
        // Both surfaces render the window's single ordered entry list, so a change to one is a change
        // to the other; the header is the plan's fixed wording.
        WorkflowDefinition definition = Definition("full-text-translation", "全文翻译", true, 20);
        MainWindowViewModel main = new();
        try
        {
            main.WorkflowMenuEntries.Add(new WorkflowMenuEntryViewModel(
                definition, StubMenuService(definition), new StubHost(), WorkflowLaunchSelection.None));

            main.Shell.WorkflowMenuEntries.Should().BeSameAs(main.WorkflowMenuEntries);
            main.WorkflowMenuEntries[0].Header.Should().Be("全文翻译");
            main.HasWorkflowMenuEntries.Should().BeTrue();
        }
        finally
        {
            main.Dispose();
        }
    }

    [Fact]
    public async Task Clicking_a_menu_item_starts_the_session_with_the_selection_and_opens_the_chat_tab()
    {
        const string documentId = "0f3a6d1c2b4e5f60718293a4b5c6d7e8";
        string secondDocumentId = "1f3a6d1c2b4e5f60718293a4b5c6d7e8";
        WorkflowDefinition definition = Definition("full-text-translation", "全文翻译", true, 20);
        FakeWorkflowMenuService menu = new([definition], "session-42");
        StubHost host = new();
        WorkflowMenuEntryViewModel entry = new(definition, menu,
            host, new WorkflowLaunchSelection(documentId, "3-5")
            {
                DocumentIds = [documentId, secondDocumentId],
                TextSelection = "selected passage"
            });

        await entry.LaunchCommand.ExecuteAsync();

        menu.Started.Should().HaveCount(1);
        menu.Started[0].WorkflowId.Should().Be("full-text-translation");
        menu.Started[0].Selection.DocumentId.Should().Be(documentId);
        menu.Started[0].Selection.PageRange.Should().Be("3-5");
        menu.Started[0].Selection.DocumentIds.Should().Equal(documentId, secondDocumentId);
        menu.Started[0].Selection.TextSelection.Should().Be("selected passage");
        host.OpenChatTabCount.Should().Be(1, "the launch must bring the chat tab forward");
        host.OpenedSessionId.Should().Be("session-42");
        host.Reported.Should().ContainSingle().Which.Should().Contain("session-42");
        host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Clicking_a_menu_item_without_a_document_still_attempts_the_workflow()
    {
        WorkflowDefinition definition = Definition("full-text-translation", "全文翻译", true, 20);
        FakeWorkflowMenuService menu = new([definition], "session-42");
        StubHost host = new();
        WorkflowMenuEntryViewModel entry =
            new(definition, menu, host, WorkflowLaunchSelection.None);

        await entry.LaunchCommand.ExecuteAsync();

        menu.Started.Should().ContainSingle();
        menu.Started[0].Selection.DocumentId.Should().BeEmpty();
        host.Reported.Should().ContainSingle().Which.Should().Contain("session-42");
        host.Errors.Should().BeEmpty();
        host.OpenChatTabCount.Should().Be(1);
    }

    [Fact]
    public void Menu_request_keeps_selection_as_context_instead_of_hardcoding_parameter_keys()
    {
        const string firstDocument = "doc-a";
        const string secondDocument = "doc-b";
        WorkflowLaunchSelection selection = new(firstDocument, "3-5")
        {
            DocumentIds = [firstDocument, secondDocument],
            TextSelection = "selected passage"
        };

        WorkflowSessionRequest request = WorkflowMenuService.CreateLaunchRequest("custom.workflow", selection);

        request.Parameters.Should().BeEmpty();
        request.Selection.Documents.Should().Equal(firstDocument, secondDocument);
        request.Selection.PageRange.Should().Be("3-5");
        request.Selection.TextSelection.Should().Be("selected passage");
    }

    [Fact]
    public async Task Launch_validation_errors_are_reported_and_open_the_workflow_editor()
    {
        WorkflowDefinition definition = Definition("custom.workflow", "测试工作流", true, 20);
        WorkflowValidationIssue issue = new("targetLanguage", "required", "请选择目标语言。");
        FakeWorkflowMenuService menu = new([definition], "unused")
        {
            StartException = new WorkflowConfigurationValidationException([issue])
        };
        StubHost host = new();
        WorkflowMenuEntryViewModel entry =
            new(definition, menu, host, WorkflowLaunchSelection.None);

        await entry.LaunchCommand.ExecuteAsync();

        host.Errors.Should().ContainSingle().Which.Should().Contain("请选择目标语言");
        host.ValidationWorkflowId.Should().Be("custom.workflow");
        host.ValidationSelection.Should().Be(WorkflowLaunchSelection.None);
        host.ValidationIssues.Should().ContainSingle().Which.Should().Be(issue);
        host.OpenChatTabCount.Should().Be(0, "the editor owns the repair flow");
    }

    [Fact]
    public void A_finished_session_notifies_the_status_bar()
    {
        HostActivityTracker tracker = new();
        MainWindowViewModel main = new(activityTracker: tracker,
            workflowSessionStatus: new StubStatusLookup(Snapshot("run-1", AgentSessionStatus.Finished,
                "run finished")));
        try
        {
            // The window subscribes to the tracker at construction; the session's own scope leaving the
            // snapshot is what makes it read the session's authoritative host status.
            IActivityScope scope = tracker.BeginScope("Agent session run-1", HostActivityKind.Mcp,
                "Running: created", "run-1");
            main.Status.Should().NotContain("已完成", "a running session is not a completion");

            scope.Dispose();

            main.Status.Should().Contain("run-1");
            main.Status.Should().Contain("已完成");
            main.StatusIsError.Should().BeFalse();
        }
        finally
        {
            main.Dispose();
            tracker.Dispose();
        }
    }

    [Fact]
    public void A_failed_session_notifies_the_status_bar_as_an_error()
    {
        HostActivityTracker tracker = new();
        MainWindowViewModel main = new(activityTracker: tracker,
            workflowSessionStatus: new StubStatusLookup(Snapshot("run-2", AgentSessionStatus.Failed,
                "model call refused")));
        try
        {
            IActivityScope scope = tracker.BeginScope("Agent session run-2", HostActivityKind.Mcp,
                "Running: created", "run-2");
            scope.Dispose();

            main.StatusIsError.Should().BeTrue();
            main.Status.Should().Contain("失败");
            main.Status.Should().Contain("model call refused");
        }
        finally
        {
            main.Dispose();
            tracker.Dispose();
        }
    }

    [Fact]
    public void A_session_the_host_no_longer_knows_is_reported_as_unsuccessful()
    {
        using HostActivityTracker tracker = new();
        WorkflowMenuService notifications = new(_ => Task.FromResult<WorkflowSessionRunner?>(null));
        IActivityScope scope = tracker.BeginScope("Agent session run-3", HostActivityKind.Mcp,
            "Running: created", "run-3");
        // Each snapshot is read while the session is live, exactly as the window's subscription does.
        notifications.ReadNotifications(tracker.Current, _ => null).Should().BeEmpty();
        scope.Dispose();

        IReadOnlyList<WorkflowSessionNotification> reported =
            notifications.ReadNotifications(tracker.Current, _ => null);

        reported.Should().ContainSingle();
        reported[0].SessionId.Should().Be("run-3");
        reported[0].IsError.Should().BeTrue();
    }

    [Fact]
    public void A_running_session_is_never_reported_as_a_completion()
    {
        using HostActivityTracker tracker = new();
        WorkflowMenuService notifications = new(_ => Task.FromResult<WorkflowSessionRunner?>(null));
        tracker.BeginScope("Agent session run-4", HostActivityKind.Mcp, "Running: created", "run-4");

        IReadOnlyList<WorkflowSessionNotification> reported =
            notifications.ReadNotifications(tracker.Current,
                _ => Snapshot("run-4", AgentSessionStatus.Running, "created"));

        reported.Should().BeEmpty();
    }

    private static AgentSessionSnapshot Snapshot(string sessionId, AgentSessionStatus status, string detail)
    {
        return new AgentSessionSnapshot(sessionId, status, 0, 0, 0, 0, detail, DateTimeOffset.UtcNow);
    }

    private static WorkflowDefinition Definition(string id, string name, bool showInMenu, int order)
    {
        return new WorkflowDefinition(
            id,
            name,
            $"description of {name}",
            "run",
            [],
            WorkflowSelectionScope.Documents,
            false,
            new WorkflowMenuPlacement(WorkflowDefinitions.DefaultMenuPath, order, showInMenu),
            false);
    }

    private static IWorkflowMenuService StubMenuService(WorkflowDefinition definition)
    {
        return WorkflowMenuService.ForDefinitions([definition]);
    }

    private sealed class StubStatusLookup : IWorkflowSessionStatusLookup
    {
        private readonly AgentSessionSnapshot _snapshot;

        public StubStatusLookup(AgentSessionSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public AgentSessionSnapshot? TryGetStatus(string sessionId)
        {
            return string.Equals(sessionId, _snapshot.SessionId, StringComparison.Ordinal) ? _snapshot : null;
        }
    }

    private sealed class FakeWorkflowMenuService : IWorkflowMenuService
    {
        private readonly IReadOnlyList<WorkflowDefinition> _definitions;
        private readonly string _sessionId;

        public FakeWorkflowMenuService(IReadOnlyList<WorkflowDefinition> definitions, string sessionId)
        {
            _definitions = definitions;
            _sessionId = sessionId;
        }

        public List<(string WorkflowId, WorkflowLaunchSelection Selection)> Started { get; } = [];
        public Exception? StartException { get; init; }

        public Task<IReadOnlyList<WorkflowDefinition>> ListMenuDefinitionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_definitions);
        }

        public Task<string> StartAsync(string workflowId, WorkflowLaunchSelection selection,
            CancellationToken cancellationToken = default)
        {
            Started.Add((workflowId, selection));
            if (StartException is not null)
            {
                throw StartException;
            }

            return Task.FromResult(_sessionId);
        }
    }

    private sealed class StubHost : IWorkflowMenuEntryHost
    {
        public List<string> Reported { get; } = [];

        public List<string> Errors { get; } = [];

        public int OpenChatTabCount { get; private set; }

        public string? OpenedSessionId { get; private set; }

        public string? ValidationWorkflowId { get; private set; }

        public WorkflowLaunchSelection? ValidationSelection { get; private set; }

        public IReadOnlyList<WorkflowValidationIssue> ValidationIssues { get; private set; } = [];

        public IHostActivityTracker? ActivityTracker => null;

        public void Report(string message)
        {
            Reported.Add(message);
        }

        public void ReportError(string message)
        {
            Errors.Add(message);
        }

        public Task OpenChatTabAsync(string? sessionId = null)
        {
            OpenChatTabCount++;
            OpenedSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task HandleWorkflowValidationAsync(string workflowId, WorkflowLaunchSelection selection,
            IReadOnlyList<WorkflowValidationIssue> issues)
        {
            ValidationWorkflowId = workflowId;
            ValidationSelection = selection;
            ValidationIssues = issues;
            return Task.CompletedTask;
        }
    }
}
