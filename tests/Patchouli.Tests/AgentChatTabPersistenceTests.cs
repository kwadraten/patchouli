using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Host.Agent;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels.AgentChat;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class AgentChatTabPersistenceTests
{
    private readonly AvaloniaSharedSessionFixture _headless;

    public AgentChatTabPersistenceTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public async Task Returning_to_chat_catches_up_without_moving_a_reader_who_scrolled_up()
    {
        await _headless.Session.Dispatch<int>(async () =>
        {
            StubSessionService service = new();
            service.Sessions.Add(Snapshot("conversation"));
            AddMessages(service, 80);
            using AgentChatTabViewModel chat = CreateChat(service);
            AgentChatPage page = new() { DataContext = chat };
            Window window = new() { Width = 1100, Height = 760, Content = page };
            window.Show();
            try
            {
                IWorkspaceTabPage lifecycle = page;
                lifecycle.OnTabActivated();
                await WaitForPageAsync(window, () => chat.Messages.Count == 80);

                ScrollViewer scroll = page.FindControl<ScrollViewer>("MessageScroll")!;
                await WaitForPageAsync(window, () => scroll.Extent.Height > scroll.Viewport.Height + 100);
                double readerOffset = (scroll.Extent.Height - scroll.Viewport.Height) / 2;
                scroll.Offset = new Vector(0, readerOffset);
                window.UpdateLayout();
                scroll.Offset.Y.Should().BeApproximately(readerOffset, 1);

                lifecycle.OnTabDeactivated();
                AddMessage(service, 81);
                chat.Messages.Should().HaveCount(80, "a hidden page should stop polling until it is activated again");
                scroll.Offset.Y.Should().BeApproximately(readerOffset, 1);
                service.StopCalls.Should().BeEmpty();
                lifecycle.OnTabActivated();

                await WaitForPageAsync(window, () => chat.Messages.Count == 81);
                scroll.Offset.Y.Should().BeApproximately(readerOffset, 1,
                    "catching up after a hidden tab returns should preserve the reader's position");
                service.StopCalls.Should().BeEmpty("deactivating the tab must leave its session running");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_chat_releases_the_page_and_the_window_owned_view_model_can_be_reopened()
    {
        await _headless.Session.Dispatch<int>(async () =>
        {
            StubSessionService service = new();
            service.Sessions.Add(Snapshot("conversation"));
            AddMessages(service, 80);
            using AgentChatTabViewModel chat = CreateChat(service);
            AgentChatPage firstPage = new() { DataContext = chat };
            Window window = new() { Width = 1100, Height = 760, Content = firstPage };
            window.Show();
            try
            {
                ((IWorkspaceTabPage)firstPage).OnTabActivated();
                await WaitForPageAsync(window, () => chat.Messages.Count == 80);
                ScrollViewer firstScroll = firstPage.FindControl<ScrollViewer>("MessageScroll")!;
                await WaitForPageAsync(window, () => firstScroll.Extent.Height > firstScroll.Viewport.Height + 100);
                double closedOffset = (firstScroll.Extent.Height - firstScroll.Viewport.Height) / 2;
                firstScroll.Offset = new Vector(0, closedOffset);
                window.UpdateLayout();
                ((IWorkspaceTabPage)firstPage).OnTabClosed();

                service.StopCalls.Should().BeEmpty("closing the page must not stop the running session");
                AddMessage(service, 81);
                await chat.RefreshAsync();
                chat.SelectedSession!.SessionId.Should().Be("conversation");
                chat.Messages.Should().HaveCount(81);
                firstScroll.Offset.Y.Should().BeApproximately(closedOffset, 1,
                    "a closed page should have released its collection subscription and scrolling callbacks");

                AgentChatPage reopenedPage = new() { DataContext = chat };
                window.Content = reopenedPage;
                window.UpdateLayout();
                ((IWorkspaceTabPage)reopenedPage).OnTabActivated();
                await WaitForPageAsync(window, () => chat.Messages.Count == 81 &&
                                                     reopenedPage.FindControl<ScrollViewer>("MessageScroll") is
                                                         { Extent.Height: > 0 });
                chat.Messages.Should().HaveCount(81);
                service.StopCalls.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static AgentChatTabViewModel CreateChat(StubSessionService service)
    {
        return new AgentChatTabViewModel(new StubChatHost(), _ => Task.FromResult<IAgentChatSessionService>(service),
            TimeProvider.System, TimeSpan.FromHours(1));
    }

    private static AgentSessionSnapshot Snapshot(string id)
    {
        return new AgentSessionSnapshot(id, AgentSessionStatus.Running, 0, 0, 0, 0, "running",
            DateTimeOffset.UtcNow);
    }

    private static void AddMessages(StubSessionService service, int count)
    {
        for (int index = 1; index <= count; index++)
        {
            AddMessage(service, index);
        }
    }

    private static void AddMessage(StubSessionService service, int index)
    {
        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @case = "UserMessage",
            messageId = $"message-{index}",
            text = $"Message {index}: {new string('x', 120)}"
        });
        service.Log.Add(new AgentSessionLogEntry(index, AgentLogKinds.Event, payload, DateTimeOffset.UtcNow));
    }

    private static async Task WaitForPageAsync(Window window, Func<bool> ready)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            window.UpdateLayout();
            if (ready())
            {
                return;
            }

            await Task.Delay(20);
        }

        ready().Should().BeTrue("the chat page should finish updating within three seconds");
    }

    private sealed class StubChatHost : IAgentChatHost
    {
        public AgentChatCommandContext CommandContext { get; } = new(null);

        public void ReportError(string message)
        {
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult<TResult?>(default);
        }
    }

    private sealed class StubSessionService : IAgentChatSessionService
    {
        public List<AgentSessionSnapshot> Sessions { get; } = [];
        public List<AgentSessionLogEntry> Log { get; } = [];
        public List<string> StopCalls { get; } = [];

        public Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<AgentSessionSnapshot>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionSnapshot>>([.. Sessions]);
        }

        public Task<AgentSessionSnapshot?> TryOpenAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions.FirstOrDefault(session => session.SessionId == sessionId));
        }

        public Task<IReadOnlyList<AgentSessionLogEntry>> ReadEventLogAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionLogEntry>>([.. Log]);
        }

        public Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            StopCalls.Add(sessionId);
            AgentSessionSnapshot stopped = Sessions.Single(session => session.SessionId == sessionId) with
            {
                Status = AgentSessionStatus.Stopped
            };
            Sessions[Sessions.FindIndex(session => session.SessionId == sessionId)] = stopped;
            return Task.FromResult(stopped);
        }

        public Task<AgentSessionSnapshot> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
