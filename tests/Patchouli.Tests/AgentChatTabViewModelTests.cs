using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.AgentChat;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>
///     Covers the S3 chat tab: the session list (active / history + re-open), the message flow built
///     from the append-only event log, the send acknowledgement, stop / resume, and the D9 contract
///     that closing the tab removes the front end only.
/// </summary>
[Collection("Avalonia")]
public sealed class AgentChatTabViewModelTests : IDisposable
{
    [Fact]
    public async Task Nested_sdk_operations_merge_by_operation_id_without_overwriting_the_parent_activity()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("sdk-events", AgentSessionStatus.Idle));
        service.Log.Add(Entry(1, AgentLogKinds.EffectIssued,
            """{"effectId":4,"kind":"McpToolCall","name":"fsi","input":"code"}"""));
        service.Log.Add(Entry(2, "sdk/operation", System.Text.Json.JsonSerializer.Serialize(
            new AgentSdkReceipt("effect/4/1", "effect/4", "fetch", "{}", "version", "Started", "", null, 0))));
        service.Log.Add(Entry(3, "sdk/operation", System.Text.Json.JsonSerializer.Serialize(
            new AgentSdkReceipt("effect/4/1", "effect/4", "fetch", "{}", "version", "Failed", "denied",
                "PERMISSION_DENIED", 12))));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.Messages.Should().HaveCount(2);
        chat.Messages.Single(row => row.EffectId == 4).Title.Should().Contain("fsi");
        AgentChatMessageViewModel operation = chat.Messages.Single(row => row.OperationId == "effect/4/1");
        operation.ParentId.Should().Be("effect/4");
        operation.State.Should().Be("failed");
        operation.ErrorCode.Should().Be("PERMISSION_DENIED");
        operation.DurationText.Should().Be("12 ms");
    }

    [Fact]
    public async Task Opening_a_launched_session_replaces_the_new_chat_draft_even_after_it_finishes()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("previous", AgentSessionStatus.Idle));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.NewSessionCommand.Execute(null);
        service.Sessions.Add(Snapshot("workflow-finished", AgentSessionStatus.Finished));
        (await chat.OpenSessionByIdAsync("workflow-finished")).Should().BeTrue();
        chat.SelectedSession!.SessionId.Should().Be("workflow-finished");
        chat.HistorySessions.Should().Contain(row => row.SessionId == "workflow-finished");
        await chat.RefreshAsync();
        chat.SelectedSession!.SessionId.Should().Be("workflow-finished");
        (await chat.OpenSessionByIdAsync("missing")).Should().BeFalse();
        chat.SelectedSession!.SessionId.Should().Be("workflow-finished");
    }

    [Fact]
    public async Task Composer_action_is_one_of_stop_resume_send_and_typing_selects_send()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("action", AgentSessionStatus.Running));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.ComposerStops.Should().BeTrue();
        chat.ComposerResumes.Should().BeFalse();
        chat.ComposerSends.Should().BeFalse();
        await chat.ComposerActionCommand.ExecuteAsync(null);
        chat.ComposerStops.Should().BeFalse();
        chat.ComposerResumes.Should().BeTrue();
        chat.MessageInput = "继续翻译";
        chat.ComposerSends.Should().BeTrue();
        chat.ComposerResumes.Should().BeFalse();
        await chat.ComposerActionCommand.ExecuteAsync(null);
        service.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Native_text_displays_actual_model_and_tool_only_replies_have_no_bubble()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("native", AgentSessionStatus.Idle));
        service.Log.Add(Entry(1, AgentLogKinds.Event,
            """{"case":"AssistantReply","reply":{"text":"先读取，再翻译。","model":"deepseek-flash","finish":"Tools","toolCalls":[{"id":"a","name":"put","arguments":"{}"}]}}"""));
        service.Log.Add(Entry(2, AgentLogKinds.Event,
            """{"case":"AssistantReply","reply":{"text":"","model":"other-model","finish":"Tools","toolCalls":[{"id":"b","name":"fetch","arguments":"{}"}]}}"""));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        await chat.SelectSessionAsync(chat.HistorySessions.Single());
        AgentChatMessageViewModel assistant = chat.Messages.Single(message => message.IsAssistant);
        assistant.Title.Should().Be("deepseek-flash");
        assistant.Text.Should().Be("先读取，再翻译。");
        chat.CanCompose.Should().BeTrue();
        chat.CanStopSession.Should().BeFalse();
        chat.SessionStatusText.Should().Be("就绪");
    }

    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Chat_reply_renders_mermaid_and_clickable_citations_and_updates_streamed_text()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(ChatRenderApp));
        await session.Dispatch(async () =>
        {
            const string citationUri =
                "patchouli://texts/00000000-0000-0000-0000-000000000001/page-2.md?box=00000000-0000-0000-0000-000000000002";
            string markdown =
                $"**研究结构**\n\n[文献出处]({citationUri}) · [网络出处](https://example.org/paper)\n\n```mermaid\nflowchart LR\n A[研究问题] --> B[研究方法]\n B --> C[主要结论]\n```\n";
            StubSessionService service = new();
            service.Sessions.Add(Snapshot("reading-session", AgentSessionStatus.Running));
            service.Log.Add(Entry(1, AgentLogKinds.Event,
                System.Text.Json.JsonSerializer.Serialize(new
                    { @case = "ModelResult", effectId = 1, text = markdown })));
            using AgentChatTabViewModel chat = CreateChat(service);
            AgentChatPage page = new() { DataContext = chat };
            page.FindControl<Button>("FsiButton").Should().BeNull();
            page.FindControl<Button>("ComposerActionButton").Should().NotBeNull();
            Window window = new() { Width = 1100, Height = 760, Content = page };
            window.Show();
            try
            {
                await chat.ActivateAsync();
                await WaitForMarkdownAsync(window, () => page.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MermaidPresenter>()
                    .Any(presenter => presenter.IsVisible && presenter.Bounds.Height > 50));
                ChatMarkdownRenderer renderer = page.GetVisualDescendants().OfType<ChatMarkdownRenderer>().Single();
                LiveMarkdown.Avalonia.MermaidPresenter diagram = renderer.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MermaidPresenter>().Single();
                diagram.Text.Should().Contain("A[研究问题] --> B[研究方法]");
                diagram.Bounds.Width.Should().BeGreaterThan(100);
                page.TryFindResource("OnSurfaceBrush", out object? foreground);
                diagram.Foreground.Should().BeSameAs(foreground);
                LiveMarkdown.Avalonia.Link[] links = renderer.GetLogicalDescendants()
                    .OfType<LiveMarkdown.Avalonia.Link>().ToArray();
                links.Select(link => link.HRef!.AbsoluteUri).Should().Equal(citationUri, "https://example.org/paper");

                Uri? clicked = null;
                renderer.AddHandler(LiveMarkdown.Avalonia.MarkdownTextBlock.LinkClickEvent,
                    (_, args) => clicked = args.HRef, handledEventsToo: true);
                LiveMarkdown.Avalonia.MarkdownTextBlock citation = renderer.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>().First(block =>
                        block.ActualText.Contains("文献出处", StringComparison.Ordinal));
                Rect linkBounds = citation.GetTextRangeBoundsInControl(0, 4).First();
                Point position = citation.TranslatePoint(linkBounds.Center, window)!.Value;
                window.MouseMove(position);
                window.MouseDown(position, MouseButton.Left);
                window.MouseUp(position, MouseButton.Left);
                clicked!.AbsoluteUri.Should().Be(citationUri);
                SaveChatPreview(page, 1100, "mermaid");

                renderer.Markdown = markdown + string.Concat(Enumerable.Repeat("\n\n补充结论。", 30));
                await WaitForMarkdownAsync(window, () => renderer.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>()
                    .Any(block => block.ActualText.Contains("补充结论", StringComparison.Ordinal)));
                renderer.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MermaidPresenter>().Should()
                    .ContainSingle();
                ScrollViewer scroll = page.FindControl<ScrollViewer>("MessageScroll")!;
                await WaitForMarkdownAsync(window, () => scroll.Offset.Y > 0 &&
                                                         scroll.Offset.Y >= scroll.Extent.Height -
                                                         scroll.Viewport.Height - 1);
                scroll.Offset = new Vector();
                window.UpdateLayout();
                renderer.Markdown += "\n\n最后一段。";
                await WaitForMarkdownAsync(window, () => renderer.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>().Any(block =>
                        block.ActualText.Contains("最后一段", StringComparison.Ordinal)));
                scroll.Offset.Y.Should().Be(0, "reading earlier content should not jump to the latest reply");

                renderer.Markdown = "替换后的回复";
                await WaitForMarkdownAsync(window, () => !renderer.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MermaidPresenter>().Any());
                renderer.GetLogicalDescendants().OfType<LiveMarkdown.Avalonia.Link>().Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task WaitForMarkdownAsync(Window window, Func<bool> isReady)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            window.UpdateLayout();
            if (isReady())
            {
                return;
            }

            await Task.Delay(20);
        }

        isReady().Should().BeTrue("the Markdown renderer should finish updating within three seconds");
    }

    [Fact]
    public async Task Chat_page_selects_a_session_and_starts_a_new_one_from_the_composer()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(async () =>
        {
            StubSessionService service = new();
            service.Sessions.Add(Snapshot("live", AgentSessionStatus.Running));
            service.Sessions.Add(Snapshot("old", AgentSessionStatus.Finished));
            using AgentChatTabViewModel chat = new(new StubChatHost(),
                _ => Task.FromResult<IAgentChatSessionService>(service), TimeProvider.System, TimeSpan.Zero);
            AgentChatPage page = new() { DataContext = chat };
            Window window = new() { Width = 1100, Height = 760, Content = page };
            window.Show();
            try
            {
                await chat.ActivateAsync();
                page.FindControl<ListBox>("HistorySessionList")!.SelectedIndex = 0;
                chat.SelectedSession!.SessionId.Should().Be("old");
                page.FindControl<TextBox>("MessageBox")!.IsEnabled.Should().BeTrue();

                chat.NewSessionCommand.Execute(null);
                TextBox composer = page.FindControl<TextBox>("MessageBox")!;
                composer.IsEnabled.Should().BeTrue();
                composer.Text = "从页面创建会话";
                composer.Focus();
                composer.CaretIndex = composer.Text.Length;
                window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
                service.CreatedPrompts.Should().BeEmpty();
                composer.Text.Should().Contain("\n");
                composer.Text += "第二行";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                if (chat.SendMessageCommand.ExecutionTask is { } send)
                {
                    await send;
                }

                service.CreatedPrompts.Should().ContainSingle().Which.Should().Contain("从页面创建会话").And.Contain("第二行");
                chat.SelectedSession!.SessionId.Should().Be("chat-new");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1100, true)]
    [InlineData(640, false)]
    public async Task Chat_page_keeps_the_composer_visible_at_desktop_and_narrow_widths(int width, bool sidebar)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(ChatRenderApp));
        await session.Dispatch(async () =>
        {
            StubSessionService service = new();
            using AgentChatTabViewModel chat = new(new StubChatHost(),
                _ => Task.FromResult<IAgentChatSessionService>(service), TimeProvider.System, TimeSpan.Zero);
            chat.IsSidebarOpen = sidebar;
            AgentChatPage page = new() { DataContext = chat };
            Window window = new() { Width = width, Height = 760, Content = page };
            window.Show();
            try
            {
                await chat.ActivateAsync();
                window.UpdateLayout();
                TextBox composer = page.FindControl<TextBox>("MessageBox")!;
                Point origin = composer.TranslatePoint(new Point(), page)!.Value;
                origin.Y.Should().BeGreaterThan(500);
                (origin.Y + composer.Bounds.Height).Should().BeLessThan(760);
                composer.Bounds.Width.Should().BeGreaterThan(400);
                composer.IsEnabled.Should().BeTrue();
                page.FindControl<Border>("SessionSidebar")!.IsVisible.Should().Be(sidebar);
                SaveChatPreview(page, width, "new");

                service.Sessions.Add(Snapshot("reading-session", AgentSessionStatus.Running));
                service.Log.Add(Entry(1, AgentLogKinds.Event,
                    "{\"case\":\"UserMessage\",\"messageId\":\"u1\",\"text\":\"请帮我梳理这篇文献的核心观点。\"}"));
                service.Log.Add(Entry(2, AgentLogKinds.Event,
                    "{\"case\":\"ModelResult\",\"effectId\":1,\"text\":\"**核心观点**\\n\\n文章讨论了证据如何支撑结论，以及不同研究方法的适用边界。\\n\\n1. 明确研究问题与假设。\\n2. 区分观察结果与推论。\\n3. 保留尚待验证的线索。\"}"));
                service.Log.Add(Entry(3, AgentLogKinds.EffectIssued,
                    "{\"effectId\":2,\"kind\":\"McpToolCall\"}"));
                await chat.RefreshAsync();
                await WaitForMarkdownAsync(window, () => page.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>().Any(block =>
                        block.ActualText.Contains("核心观点", StringComparison.Ordinal)));
                window.UpdateLayout();
                page.GetVisualDescendants().OfType<Expander>().Where(row => row.IsVisible)
                    .Should().OnlyContain(row => !row.IsExpanded);
                SaveChatPreview(page, width, "conversation");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(640)]
    public async Task Detailed_tool_failure_is_expanded_and_composer_remains_available(int width)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(ChatRenderApp));
        await session.Dispatch(async () =>
        {
            StubSessionService service = new();
            service.Sessions.Add(Snapshot("translation", AgentSessionStatus.Failed));
            service.Log.Add(Entry(1, AgentLogKinds.Event,
                """{"case":"UserMessage","messageId":"u1","text":"继续完成第 3 页的翻译，保留原文段落与公式结构。"}"""));
            service.Log.Add(Entry(2, AgentLogKinds.Event,
                """{"case":"ModelResult","effectId":1,"text":"已经读取页面内容。正在保存翻译并检查段落结构。"}"""));
            service.Log.Add(Entry(3, AgentLogKinds.EffectIssued,
                """{"effectId":3,"kind":"McpToolCall","name":"put","state":"running","input":"{\"uri\":\"patchouli://translations/doc/page-3.md\",\"content\":\"译文段落……\"}"}"""));
            service.Log.Add(Entry(4, AgentLogKinds.EffectResult,
                """{"effectId":3,"state":"failed","errorCode":"STRUCTURE_MISMATCH","elapsedMs":1532,"summary":"保存失败：译文有 2 个段落，原文要求 3 个","output":"{\"error\":{\"code\":\"STRUCTURE_MISMATCH\",\"message\":\"第 3 页段落数量不匹配\",\"expected\":3,\"actual\":2}}"}"""));
            using AgentChatTabViewModel chat = CreateChat(service);
            chat.ReduceMotion = true;
            AgentChatPage page = new() { DataContext = chat };
            Window window = new() { Width = width, Height = 760, Content = page };
            window.Show();
            try
            {
                await chat.ActivateAsync();
                await chat.SelectSessionAsync(chat.HistorySessions.Single());
                await WaitForMarkdownAsync(window,
                    () => page.GetVisualDescendants().OfType<Expander>().Any(row => row.IsExpanded));
                window.UpdateLayout();
                page.FindControl<TextBox>("MessageBox")!.IsEnabled.Should().BeTrue();
                page.Classes.Should().NotContain("motion");
                chat.Messages.Single(row => row.EffectId == 3).IsExpanded.Should().BeTrue();
                SaveChatPreview(page, width, "tool-failure");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void SaveChatPreview(AgentChatPage page, int width, string state)
    {
        if (Environment.GetEnvironmentVariable("PATCHOULI_CHAT_PREVIEW_DIR") is not { Length: > 0 } directory)
        {
            return;
        }

        if (!Testing.RepositoryTestEnvironment.IsTemporaryPath(directory))
        {
            throw new InvalidOperationException("Chat preview output must be inside the repository .tmp directory.");
        }

        Directory.CreateDirectory(directory);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, 760), new Vector(96, 96));
        bitmap.Render(page);
        bitmap.Save(Path.Combine(directory, $"chat-{state}-{width}.png"));
    }

    public static class ChatRenderApp
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<UI.App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
    }

    [Fact]
    public async Task New_session_draft_survives_refresh_and_first_send_creates_a_selected_conversation()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("existing", AgentSessionStatus.Running));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();

        chat.NewSessionCommand.Execute(null);
        chat.MessageInput = "  帮我梳理文献  ";
        await chat.RefreshAsync();

        chat.IsNewSession.Should().BeTrue();
        chat.SelectedSession.Should().BeNull();
        chat.CanCompose.Should().BeTrue();
        chat.MessageInput.Should().Be("  帮我梳理文献  ");
        service.StopCalls.Should().BeEmpty();

        await chat.SendMessageAsync();

        service.CreatedPrompts.Should().Equal("帮我梳理文献");
        service.Sent.Should().BeEmpty();
        chat.SelectedSession!.SessionId.Should().Be("chat-new");
        chat.IsNewSession.Should().BeFalse();
        chat.MessageInput.Should().BeEmpty();
        chat.ActiveSessions.Should().HaveCount(2);
        chat.Messages.Should().ContainSingle(message => message.Text == "帮我梳理文献");
    }

    [Fact]
    public async Task Pending_echo_is_replaced_by_the_receipt_and_the_applied_event_is_not_duplicated()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run", AgentSessionStatus.Running));
        service.Log.Add(Entry(1, AgentLogKinds.Event,
            "{\"case\":\"ModelResult\",\"effectId\":1,\"text\":\"first answer\"}"));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.MessageInput = "follow up";
        await chat.SendMessageAsync();
        string payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @case = "UserMessage", messageId = service.Sent[0].MessageId, text =
                        "follow up"
                }
            )
            ;
        service
            .Log
            .Add
            (
                Entry
                (
                    2
                    ,
                    AgentLogKinds
                        .Inbox
                    ,
                    payload
                )
            )
            ;
        service
            .Log
            .Add
            (
                Entry
                (
                    3
                    ,
                    AgentLogKinds
                        .Event
                    ,
                    payload
                )
            )
            ;
        service
            .Log
            .Add
            (
                Entry
                (
                    4
                    ,
                    AgentLogKinds
                        .Event
                    ,
                    "{\"case\":\"ModelResult\",\"effectId\":2,\"text\":\"second answer\"}"
                )
            )
            ;
        await
            chat
                .RefreshAsync
                (
                )
            ;
        chat
            .Messages
            .Select
            (message
                =>
                message
                    .Text
            )
            .Should
            (
            )
            .Equal
            (
                "first answer"
                ,
                "follow up"
                ,
                "second answer"
            )
            ;
        chat
            .Messages
            .Select
            (message
                =>
                message
                    .Seq
            )
            .Should
            (
            )
            .Equal
            (
                1
                ,
                2
                ,
                4
            )
            ;
    }

    [
        Fact
    ]
    public
        async
        Task
        Failed_creation_keeps_the_prompt_and_allows_retry
        (
        )
    {
        StubSessionService
            service
                =
                new
                (
                )
                {
                    FailCreation
                        =
                        true
                }
            ;
        using
            AgentChatTabViewModel
            chat
                =
                CreateChat
                (
                    service
                )
            ;
        chat
                .MessageInput
            =
            "保留这条问题"
            ;
        await
            chat
                .SendMessageAsync
                (
                )
            ;

        chat.MessageInput.Should().Be("保留这条问题");
        chat.IsNewSession.Should().BeTrue();
        chat
            .IsSending
            .Should
            (
            )
            .BeFalse
            (
            )
            ;
        chat
            .CanCompose
            .Should
            (
            )
            .BeTrue
            (
            )
            ;
        chat
            .MessageInputNotice
            .Should
            (
            )
            .Contain
            (
                "请先配置 LLM"
            )
            ;
        service
            .CreatedPrompts
            .Should
            (
            )
            .BeEmpty
            (
            )
            ;
    }

    [
        Fact
    ]
    public
        async
        Task
        Switching_sessions_replaces_the_flow_even_when_their_sequence_numbers_overlap
        (
        )
    {
        StubSessionService
            service
                =
                new
                (
                )
            ;
        service
            .Sessions
            .Add
            (
                Snapshot
                (
                    "a"
                    ,
                    AgentSessionStatus
                        .Running
                )
            )
            ;
        service
            .Sessions
            .Add
            (
                Snapshot
                (
                    "b"
                    ,
                    AgentSessionStatus
                        .Finished
                )
            )
            ;
        service
            .Log
            .Add
            (
                Entry
                (
                    1
                    ,
                    AgentLogKinds
                        .Event
                    ,
                    "{\"case\":\"ModelResult\",\"effectId\":1,\"text\":\"first session\"}"
                )
            )
            ;
        using
            AgentChatTabViewModel
            chat
                =
                CreateChat
                (
                    service
                )
            ;
        await
            chat
                .ActivateAsync
                (
                )
            ;
        service
            .Log
            .Clear
            (
            )
            ;
        service
            .Log
            .Add
            (
                Entry
                (
                    1
                    ,
                    AgentLogKinds
                        .Event
                    ,
                    "{\"case\":\"ModelResult\",\"effectId\":1,\"text\":\"second session\"}"
                )
            )
            ;
        await
            chat
                .SelectSessionAsync
                (
                    chat
                        .HistorySessions
                        [
                            0
                        ]
                )
            ;
        await
            chat
                .RefreshAsync
                (
                )
            ;
        chat
            .Messages
            .Select
            (message
                =>
                message
                    .Text
            )
            .Should
            (
            )
            .Equal
            (
                "second session"
            )
            ;
        chat.CanCompose.Should().BeTrue();
    }

    [Fact]
    public async Task Session_list_groups_active_and_history_sessions()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        service.Sessions.Add(Snapshot("run-b", AgentSessionStatus.AwaitingEffect));
        service.Sessions.Add(Snapshot("run-c", AgentSessionStatus.Finished));
        service.Sessions.Add(Snapshot("run-d", AgentSessionStatus.Stopped));
        AgentChatTabViewModel chat = CreateChat(service);

        await chat.ActivateAsync();

        chat.ActiveSessions.Select(session => session.SessionId).Should().Equal("run-a", "run-b");
        chat.HistorySessions.Select(session => session.SessionId).Should().Equal("run-c", "run-d");
        chat.ActiveSessions.Should().OnlyContain(session => session.IsActive);
        chat.HistorySessions.Should().OnlyContain(session => !session.IsActive);
        chat.HasActiveSessionRows.Should().BeTrue();
        chat.NoHistorySessionRows.Should().BeFalse();
        chat.StatusSummary.Should().Contain("活跃 2 个").And.Contain("历史 2 个");
        chat.SelectedSession!.SessionId.Should().Be("run-a");
        service.OpenCalls.Should().Contain("run-a");
    }

    [Fact]
    public async Task Selecting_a_history_session_reopens_it()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-live", AgentSessionStatus.Running));
        service.Sessions.Add(Snapshot("run-old", AgentSessionStatus.Finished));
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        service.OpenCalls.Clear();

        chat.SelectedSession = chat.HistorySessions[0];
        await chat.OpenSessionAsync();

        service.OpenCalls.Should().NotBeEmpty().And.OnlyContain(call => call == "run-old");
        chat.SelectedSession!.SessionId.Should().Be("run-old");
        chat.SessionStatusText.Should().Be("已完成");
        chat.StatusSummary.Should().Contain("已重新打开会话 run-old");
        chat.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Message_flow_pairs_tool_activity_and_retains_each_conversation_turn()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        service.Log.Add(Entry(1, AgentLogKinds.Launch, "{\"workflowUri\":\"patchouli://workflows/x\"}"));
        service.Log.Add(Entry(2, AgentLogKinds.Event, "{\"case\":\"ModelResult\",\"effectId\":1,\"text\":\"第一步已开始\"}"));
        service.Log.Add(Entry(3, AgentLogKinds.EffectIssued,
            "{\"effectId\":2,\"kind\":\"McpToolCall\",\"name\":\"put\"}"));
        service.Log.Add(Entry(4, AgentLogKinds.EffectResult,
            "{\"effectId\":2,\"state\":\"completed\",\"summary\":\"已写入译文\"}"));
        service.Log.Add(Entry(5, AgentLogKinds.Event, "{\"case\":\"ScriptProgress\",\"message\":\"翻译第 3/10 页\"}"));
        service.Log.Add(Entry(6, AgentLogKinds.Event,
            "{\"case\":\"UserMessage\",\"messageId\":\"m1\",\"text\":\"请先处理第 3 页\"}"));
        service.Log.Add(Entry(7, AgentLogKinds.Inbox, "{\"messageId\":\"m2\",\"text\":\"补充要求\"}"));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.Messages.Select(message => message.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        chat.Messages.Select(message => message.Title).Should()
            .ContainInOrder("会话", "模型", "工具 · put", "进度", "用户", "用户 · 待处理");
        chat.Messages.Single(message => message.EffectId == 2).Text.Should().Be("已写入译文");
        chat.Messages.Single(message => message.EffectId == 2).StateLabel.Should().Be("完成");
        await chat.RefreshAsync();
        chat.Messages.Should().HaveCount(6);
    }

    [Fact]
    public async Task Failed_tool_retains_full_parameters_output_and_error_on_the_same_expanded_step()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run", AgentSessionStatus.Running));
        service.Log.Add(Entry(1, AgentLogKinds.EffectIssued,
            """{"effectId":7,"kind":"McpToolCall","name":"put","state":"running","input":"{\"uri\":\"patchouli://translations/doc/page-1.md\",\"content\":\"translation\"}"}"""));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        AgentChatMessageViewModel row = chat.Messages.Single();
        row.IsRunning.Should().BeTrue();
        service.Log.Add(Entry(2, AgentLogKinds.EffectResult,
            """{"effectId":7,"state":"failed","errorCode":"STRUCTURE_MISMATCH","elapsedMs":1250,"summary":"结构验证失败：段落数量不匹配","output":"{\"error\":{\"code\":\"STRUCTURE_MISMATCH\",\"message\":\"expected 3 blocks, got 2\"}}"}"""));
        service.Log.Add(Entry(3, AgentLogKinds.Event,
            """{"case":"ToolResult","effectId":7,"name":"put","payload":"{\"error\":{\"code\":\"STRUCTURE_MISMATCH\",\"message\":\"expected 3 blocks, got 2\"}}"}"""));
        await chat.RefreshAsync();
        chat.Messages.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.IsFailure.Should().BeTrue();
        row.IsRunning.Should().BeFalse();
        row.IsExpanded.Should().BeTrue();
        row.Input.Should().Contain("translation");
        row.Output.Should().Contain("expected 3 blocks, got 2");
        row.ErrorCode.Should().Be("STRUCTURE_MISMATCH");
        row.DurationText.Should().Be("1.3 s");
        row.DetailText.Should().Contain("patchouli://translations/doc/page-1.md").And.Contain("STRUCTURE_MISMATCH");
        row.IsExpanded = false;
        await chat.RefreshAsync();
        row.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public async Task Historical_failure_snapshot_is_visible_even_without_a_status_log_entry()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("failed", AgentSessionStatus.Failed) with
        {
            Detail = "LLM_UNAVAILABLE auth_failed: Missing API key."
        });
        service.Log.Add(Entry(1, AgentLogKinds.Launch, "{}"));
        using AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        await chat.SelectSessionAsync(chat.HistorySessions.Single());
        await chat.RefreshAsync();
        chat.Messages.Should().ContainSingle(row => row.IsFailure && row.Text.Contains("auth_failed"));
        chat.Messages.Single(row => row.IsFailure).IsExpanded.Should().BeTrue();
        chat.CanCompose.Should().BeTrue();
    }

    [Fact]
    public async Task Sending_a_message_queues_it_into_the_inbox_with_the_pending_semantics()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();

        chat.MessageInput = "请把目标语言改成日语";
        await chat.SendMessageAsync();

        service.Sent.Should().HaveCount(1);
        service.Sent[0].Text.Should().Be("请把目标语言改成日语");
        service.Sent[0].MessageId.Should().StartWith("chat-");
        chat.MessageInputNotice.Should().Be("已加入，将在当前步骤完成后生效。（会话 run-a）");
        chat.MessageInput.Should().BeEmpty();
        chat.Messages.Should().ContainSingle(message => message.Title == "用户（已加入）" &&
                                                        message.Text == "请把目标语言改成日语");
        chat.Messages[^1].Seq.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_deduplicated_send_reports_that_nothing_was_appended()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        service.Deduplicate = true;
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();

        chat.MessageInput = "重复的消息";
        await chat.SendMessageAsync();

        chat.MessageInputNotice.Should().Contain("该消息此前已加入，未重复追加");
        chat.Messages.Should().NotContain(message => message.Title == "用户（已加入）");
    }

    [Fact]
    public async Task A_backend_rejection_preserves_the_unsent_message()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-done", AgentSessionStatus.Finished));
        service.AcceptSends = false;
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        await chat.SelectSessionAsync(chat.HistorySessions.Single());

        chat.MessageInput = "太晚了";
        await chat.SendMessageAsync();

        service.SendAttempts.Should().Be(1);
        service.Sent.Should().BeEmpty();
        chat.MessageInputNotice.Should().Contain("消息未被接收");
        chat.MessageInput.Should().Be("太晚了");
    }

    [Theory]
    [InlineData(AgentSessionStatus.Failed)]
    [InlineData(AgentSessionStatus.Stopped)]
    [InlineData(AgentSessionStatus.Cancelled)]
    [InlineData(AgentSessionStatus.Finished)]
    public async Task Ended_automation_keeps_the_conversation_composer_available(AgentSessionStatus status)
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("conversation", status));
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        await chat.SelectSessionAsync(chat.HistorySessions.Single());
        chat.MessageInput = "继续";
        chat.CanCompose.Should().BeTrue();
        chat.SendMessageCommand.CanExecute(null).Should().BeTrue();
        await chat.SendMessageAsync();
        service.Sent.Should().ContainSingle();
        chat.MessageInput.Should().BeEmpty();
    }

    [Fact]
    public async Task The_send_command_requires_a_message_and_supports_new_and_live_sessions()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();

        chat.SendMessageCommand.CanExecute(null).Should().BeFalse();
        chat.MessageInput = "有内容";
        chat.SendMessageCommand.NotifyCanExecuteChanged();
        chat.SendMessageCommand.CanExecute(null).Should().BeTrue();

        chat.SelectedSession = null;
        chat.SendMessageCommand.NotifyCanExecuteChanged();
        chat.SendMessageCommand.CanExecute(null).Should().BeTrue();
        chat.MessageInput = "";
        chat.SendMessageCommand.NotifyCanExecuteChanged();
        chat.SendMessageCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Stop_and_resume_transition_the_selected_session()
    {
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        AgentChatTabViewModel chat = CreateChat(service);
        await chat.ActivateAsync();
        chat.CanStopSession.Should().BeTrue();
        chat.CanResumeSession.Should().BeFalse();

        await chat.StopSessionAsync();

        service.StopCalls.Should().Equal("run-a");
        chat.SelectedSession!.Status.Should().Be(AgentSessionStatus.Stopped);
        chat.SessionStatusText.Should().Be("已停止");
        chat.CanStopSession.Should().BeFalse();
        chat.CanResumeSession.Should().BeTrue();
        chat.HistorySessions.Select(session => session.SessionId).Should().Contain("run-a");
        chat.StatusSummary.Should().Contain("已停止会话 run-a");

        await chat.ResumeSessionAsync();

        service.ResumeCalls.Should().Equal("run-a");
        chat.SelectedSession!.Status.Should().Be(AgentSessionStatus.Running);
        chat.SessionStatusText.Should().Be("运行中");
        chat.StatusSummary.Should().Contain("已恢复会话 run-a");
    }

    [Fact]
    public async Task Closing_the_tab_removes_the_front_end_without_stopping_the_session()
    {
        using MainWindowViewModel window = new(settingsPath: _settings.Path);
        StubSessionService service = new();
        service.Sessions.Add(Snapshot("run-a", AgentSessionStatus.Running));
        AgentChatTabViewModel chat = CreateChat(service
            )
            ;
        await
            chat
                .ActivateAsync
                (
                )
            ;
        chat
            .IsActive
            (
            )
            .Should
            (
            )
            .BeTrue
            (
            )
            ;
        int stopCallsBeforeClose = service.StopCalls.Count;

        // The close path the workspace tab and the window's close command both use.
        chat.Deactivate();
        await window.CloseAgentChatTabCommand.ExecuteAsync();

        service.StopCalls.Should().HaveCount(stopCallsBeforeClose).And.BeEmpty();
        service.Sent.Should().BeEmpty();
        chat.IsActive().Should().BeFalse();
        chat.SelectedSession!.Status.Should().Be(AgentSessionStatus.Running);
        chat.SelectedSession.SessionId.Should().Be("run-a");
        chat.ActiveSessions.Should().ContainSingle(session => session.SessionId == "run-a");
    }

    private AgentChatTabViewModel CreateChat(StubSessionService service)
    {
        return new AgentChatTabViewModel(new StubChatHost(), _ => Task.FromResult<IAgentChatSessionService>(service));
    }

    private static AgentSessionSnapshot Snapshot(string sessionId, AgentSessionStatus status)
    {
        return new AgentSessionSnapshot(sessionId, status, 0, 0, 0, 0, status.ToString(),
            DateTimeOffset.UtcNow);
    }

    private static AgentSessionLogEntry Entry(long seq, string kind, string payload)
    {
        return new AgentSessionLogEntry(seq, kind, payload, DateTimeOffset.UtcNow);
    }

    /// <summary>In-memory session service: no directory, no model call and no network is involved.</summary>
    private sealed class StubSessionService : IAgentChatSessionService
    {
        public int SendAttempts { get; private set; }
        public List<string> CreatedPrompts { get; } = [];

        public bool FailCreation { get; set; }

        public Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default)
        {
            if (FailCreation)
            {
                throw new InvalidOperationException("请先配置 LLM");
            }

            CreatedPrompts.Add(text);
            AgentSessionSnapshot created = Snapshot("chat-new", AgentSessionStatus.Running);
            Sessions.Add(created);
            Log.Clear();
            Log.Add(Entry(1, AgentLogKinds.Event
                ,
                System
                    .Text
                    .Json
                    .JsonSerializer
                    .Serialize
                    (
                        new
                        {
                            @case
                                =
                                "UserMessage",
                            messageId = "launch", text
                        })));
            return Task.FromResult(created);
        }

        public List<AgentSessionSnapshot> Sessions { get; } =
        [
        ];

        public
            List
            <
                AgentSessionLogEntry
            >
            Log
        {
            get
            ;
        }
            =
            [
            ];

        /// <summary>Session ids passed to <see cref="TryOpenAsync" />, in call order.</summary>
        public List<string> OpenCalls { get; } = [];

        /// <summary>Session ids passed to <see cref="StopAsync" />; a close must never add one.</summary>
        public
            List
            <
                string
            >
            StopCalls
        {
            get
            ;
        }
            =
            [
            ];

        /// <summary>Session ids passed to <see cref="ResumeAsync" />.</summary>
        public List<string> ResumeCalls { get; } = [];

        public List<AgentInboxMessage> Sent { get; } = [];

        /// <summary>When true, a send is reported as already accepted; nothing is appended again.</summary>
        public
            bool
            Deduplicate
        {
            get
            ;
            set
            ;
        }

        /// <summary>When false, a send is rejected (a terminal run).</summary>
        public
            bool
            AcceptSends
        {
            get
            ;
            set
            ;
        }
            =
            true;

        public
            Task
            <
                IReadOnlyList
                <
                    AgentSessionSnapshot
                >
            >
            ListSessionsAsync
            (
                CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionSnapshot>>([.. Sessions]);
        }

        public
            Task
            <
                AgentSessionSnapshot
                ?
            >
            TryOpenAsync
            (
                string
                    sessionId
                ,
                CancellationToken
                    cancellationToken
                    =
                    default
            )
        {
            OpenCalls
                .Add
                (
                    sessionId
                )
                ;
            return Task.FromResult(Sessions.FirstOrDefault(session => session.SessionId == sessionId));
        }

        public Task<IReadOnlyList
                <
                    AgentSessionLogEntry
                >
            >
            ReadEventLogAsync
            (
                string
                    sessionId
                ,
                CancellationToken
                    cancellationToken
                    =
                    default
            )
        {
            return
                Task
                    .FromResult
                        <
                            IReadOnlyList
                            <
                                AgentSessionLogEntry
                            >
                        >
                        ([.. Log]);
        }

        public Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message
            ,
            CancellationToken
                cancellationToken = default)
        {
            SendAttempts++;
            if (!AcceptSends)
            {
                return
                    Task
                        .FromResult
                        (
                            new
                                AgentMessageSendResult
                                (
                                    false
                                    ,
                                    false
                                    ,
                                    sessionId
                                    ,
                                    message
                                        .MessageId
                                    ,
                                    0
                                )
                        )
                    ;
            }

            if
            (
                !
                Deduplicate
            )
            {
                Sent
                    .Add
                    (
                        message
                    )
                    ;
            }

            return
                Task.FromResult(new AgentMessageSendResult(true, Deduplicate, sessionId, message.MessageId, 0));
        }

        public Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            StopCalls.Add
                (
                    sessionId
                )
                ;
            SetStatus
            (
                sessionId, AgentSessionStatus.Stopped);
            return Task.FromResult(Sessions.First(session
                        =>
                        session
                            .SessionId
                        ==
                        sessionId
                    )
                )
                ;
        }

        public
            Task
            <
                AgentSessionSnapshot
            >
            ResumeAsync
            (
                string
                    sessionId
                ,
                CancellationToken
                    cancellationToken
                    =
                    default
            )
        {
            ResumeCalls
                .Add
                (
                    sessionId);

            SetStatus(sessionId, AgentSessionStatus.Running);
            return Task.FromResult(Sessions.First(session => session.SessionId == sessionId));
        }

        public Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        private void SetStatus(string sessionId, AgentSessionStatus status)
        {
            int index = Sessions.FindIndex(session => session.SessionId == sessionId);
            if (index >= 0)
            {
                Sessions[index] = Sessions[index] with { Status = status };
            }
        }
    }

    private sealed class StubChatHost : IAgentChatHost
    {
        public List<string> Errors { get; } = [];

        public AgentChatCommandContext CommandContext { get; } = new(null);

        public void ReportError(string message)
        {
            Errors.Add(message);
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            // No confirmation is wired in these tests: a dialog is closed without a result, which
            // never authorizes a destructive action.
            return Task.FromResult<TResult?>(default);
        }
    }
}
