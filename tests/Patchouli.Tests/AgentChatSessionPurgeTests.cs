using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.AgentChat;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

/// <summary>
///     Covers the S5 chat-tab purge entry: a session row can be physically purged only after an
///     explicit confirmation and only while it is not running; the purge removes the row from the
///     list, and cancelling the confirmation (or a running status) never purges anything.
/// </summary>
[Collection("Avalonia")]
public sealed class AgentChatSessionPurgeTests
{
    [Fact]
    public async Task A_stopped_session_is_purged_after_confirmation_and_leaves_the_list()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-live", AgentSessionStatus.Running));
        service.Sessions.Add(Snapshot("run-old", AgentSessionStatus.Stopped));
        StubChatHost host = new() { DialogResult = ConfirmDialogResult.Confirm };
        AgentChatTabViewModel chat = CreateChat(host, service);
        await chat.ActivateAsync();
        chat.SelectedSession = chat.HistorySessions.Single(session => session.SessionId == "run-old");

        chat.CanPurgeSession.Should().BeTrue();
        chat.PurgeSessionCommand.CanExecute(null).Should().BeTrue();

        await chat.PurgeSessionAsync();

        service.Purged.Should().Equal("run-old");
        chat.HistorySessions.Should().NotContain(session => session.SessionId == "run-old");
        chat.ActiveSessions.Select(session => session.SessionId).Should().Equal("run-live");
        chat.SelectedSession!.SessionId.Should().Be("run-live");
        chat.StatusSummary.Should().Contain("已清除会话 run-old");
        chat.StatusSummary.Should().Contain("OCR 成果未受影响");
    }

    [Fact]
    public async Task A_finished_or_failed_session_can_be_purged()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-done", AgentSessionStatus.Finished));
        service.Sessions.Add(Snapshot("run-failed", AgentSessionStatus.Failed));
        StubChatHost host = new() { DialogResult = ConfirmDialogResult.Confirm };
        AgentChatTabViewModel chat = CreateChat(host, service);
        await chat.ActivateAsync();
        chat.SelectedSession = chat.HistorySessions.Single(session => session.SessionId == "run-failed");

        chat.CanPurgeSession.Should().BeTrue();
        await chat.PurgeSessionAsync();

        service.Purged.Should().Equal("run-failed");
        chat.HistorySessions.Select(session => session.SessionId).Should().Equal("run-done");
        chat.StatusSummary.Should().Contain("已清除会话 run-failed");
    }

    [Fact]
    public async Task A_running_session_cannot_be_purged_and_the_command_is_disabled()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-live", AgentSessionStatus.Running));
        AgentChatTabViewModel chat = CreateChat(new StubChatHost(), service);
        await chat.ActivateAsync();

        chat.CanPurgeSession.Should().BeFalse();
        chat.PurgeSessionCommand.CanExecute(null).Should().BeFalse();

        // The guard the command's disabled state points at: even a direct call purges nothing.
        await chat.PurgeSessionAsync();

        service.Purged.Should().BeEmpty();
        chat.ActiveSessions.Should().ContainSingle(session => session.SessionId == "run-live");
        chat.StatusSummary.Should().Contain("不能清除");
    }

    [Fact]
    public async Task Cancelling_the_confirmation_never_purges()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-old", AgentSessionStatus.Stopped));
        StubChatHost host = new() { DialogResult = ConfirmDialogResult.Cancel };
        AgentChatTabViewModel chat = CreateChat(host, service);
        await chat.ActivateAsync();
        chat.SelectedSession = chat.HistorySessions.Single(session => session.SessionId == "run-old");

        await chat.PurgeSessionAsync();

        host.Dialogs.Should().HaveCount(1);
        service.Purged.Should().BeEmpty();
        chat.HistorySessions.Should().ContainSingle(session => session.SessionId == "run-old");
        chat.StatusSummary.Should().Contain("已取消清除会话 run-old");
    }

    private static AgentChatTabViewModel CreateChat(StubChatHost host, StubSessionService service)
    {
        return new AgentChatTabViewModel(host, _ => Task.FromResult<IAgentChatSessionService>(service));
    }

    private static AgentSessionSnapshot Snapshot(string sessionId, AgentSessionStatus status)
    {
        return new AgentSessionSnapshot(sessionId, status, 0, 0, 0, 0, status.ToString(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>In-memory session service recording the purge calls; nothing else is observable here.</summary>
    private sealed class StubSessionService : IAgentChatSessionService
    {
        public Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public List<AgentSessionSnapshot> Sessions { get; } = [];

        public List<string> Purged { get; } = [];

        public Task<IReadOnlyList<AgentSessionSnapshot>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionSnapshot>>([.. Sessions]);
        }

        public Task<AgentSessionSnapshot?> TryOpenAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions.FirstOrDefault(session => session.SessionId == sessionId));
        }

        public Task<IReadOnlyList<AgentSessionLogEntry>> ReadEventLogAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionLogEntry>>([]);
        }

        public Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentMessageSendResult(true, false, sessionId, message.MessageId, 0));
        }

        public Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions.First(session => session.SessionId == sessionId));
        }

        public Task<AgentSessionSnapshot> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions.First(session => session.SessionId == sessionId));
        }

        public Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Purged.Add(sessionId);
            Sessions.RemoveAll(session => session.SessionId == sessionId);
            return Task.FromResult(true);
        }
    }

    /// <summary>In-memory host: records the dialogs shown and replies with the configured result.</summary>
    private sealed class StubChatHost : IAgentChatHost
    {
        public List<object> Dialogs { get; } = [];

        /// <summary>The result the next dialog is closed with; null stands for "closed without a result".</summary>
        public object? DialogResult { get; set; }

        public AgentChatCommandContext CommandContext { get; } = new(null);

        public void ReportError(string message)
        {
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Dialogs.Add(viewModel);
            return Task.FromResult((TResult?)DialogResult);
        }
    }
}
