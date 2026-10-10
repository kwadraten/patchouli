using System.Text.Json;
using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class AgentContextCompactionTests
{
    [Fact]
    public async Task The_real_agent_loop_reads_compressed_history_and_settles_the_native_result()
    {
        using Fixture fixture = new();
        fixture.Transport.NormalTools.Enqueue([
            new LlmToolCall("history-1", "history", """{"query":"needle","limit":1}""")
        ]);
        AgentEffectInterpreter interpreter = new(new Provider(Client(fixture.Transport, 32768)),
            new UnusedGateway(), new PlaceholderAgentHostPrimitives(), compactor: fixture.Compactor);
        await using AgentSessionService sessions = new(fixture.Store, interpreter);
        AgentSessionSnapshot created = await sessions.CreateChatAsync("needle " + new string('x', 100000));
        await sessions.WakeChatAsync(created.SessionId);
        sessions.TryGetSnapshot(created.SessionId)!.Status.Should().Be(AgentSessionStatus.Idle);
        Context restored = (await fixture.Store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
        restored.History.OfType<HistoryEntry.NativeToolResult>().Should().ContainSingle()
            .Which.payload.Should().Contain("needle");
        restored.History.OfType<HistoryEntry.UserMessage>().Should().Contain(message => message.text.Length > 100000);
        fixture.Transport.Requests.Should().HaveCount(3);
        fixture.Transport.Requests[2].Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmToolResultPart>().Should().Contain(part => part.CallId == "history-1");
    }

    [Fact]
    public async Task Below_threshold_requests_keep_the_same_append_only_prefix()
    {
        using Fixture fixture = new();
        AgentEffectContext first = Scope(HistoryEntry.NewUserMessage("first task"));
        (await fixture.Compactor.CompleteAsync(first, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        AgentEffectContext next = first with
        {
            History =
            [
                .. first.History, HistoryEntry.NewModelResult("answer"), HistoryEntry.NewUserMessage("continue")
            ]
        };
        (await fixture.Compactor.CompleteAsync(next, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2);
        fixture.Transport.Requests[1].Messages.Take(fixture.Transport.Requests[0].Messages.Count)
            .Select(MessageSignature).Should().Equal(fixture.Transport.Requests[0].Messages.Select(MessageSignature));
        (await fixture.Store.ReadCompactionAsync("session", CancellationToken.None))!.Generation.Should().Be(0);
    }

    [Fact]
    public async Task Complete_prefix_is_summarized_then_continued_without_orphaned_tool_messages()
    {
        using Fixture fixture = new();
        AgentEffectContext original = LargeScope();
        (await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None)).Value.Text
            .Should().Be("continued");
        fixture.Transport.Requests.Should().HaveCount(2);
        LlmTransportRequest summary = fixture.Transport.Requests[0];
        summary.Messages.Should().Contain(message => message.Parts.OfType<LlmMessagePart.LlmToolResultPart>()
            .Any(part => part.Content == "original evidence"));
        summary.Messages.Should().Contain(message => message.Parts.OfType<LlmMessagePart.LlmToolCallPart>()
            .Any(part => part.Id == "call-1"));
        LlmTransportRequest continuation = fixture.Transport.Requests[1];
        continuation.Messages.Should().HaveCount(2, "the fixed system prefix and the whole-history summary");
        Text(continuation).Should().Contain("summary-1").And.NotContain(new string('x', 100));
        continuation.Messages.SelectMany(message => message.Parts).Should()
            .NotContain(part => part.GetType() == typeof(LlmMessagePart.LlmToolCallPart) ||
                                part.GetType() == typeof(LlmMessagePart.LlmToolResultPart));
        original.History.Should().HaveCount(3);
        AgentHistoryTool.Read(original, """{"query":"original evidence"}""").Payload.Should()
            .Contain("original evidence");
        (await fixture.Store.ReadCompactionAsync("session", CancellationToken.None))!.CoveredEntries.Should().Be(3);
    }

    [Fact]
    public async Task Repeated_compaction_includes_the_previous_summary_and_all_new_history()
    {
        using Fixture fixture = new();
        AgentEffectContext original = LargeScope();
        await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None);
        AgentEffectContext next = original with
        {
            History = [.. original.History, HistoryEntry.NewUserMessage(new string('y', 12800))]
        };
        (await fixture.Compactor.CompleteAsync(next, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(4);
        Text(fixture.Transport.Requests[2]).Should().Contain("summary-1").And.Contain(new string('y', 100))
            .And.NotContain(new string('x', 100));
        Text(fixture.Transport.Requests[3]).Should().Contain("summary-2");
        AgentContextCheckpoint checkpoint =
            (await fixture.Store.ReadCompactionAsync("session", CancellationToken.None))!;
        checkpoint.Generation.Should().Be(2);
        checkpoint.CoveredEntries.Should().Be(4);
    }

    [Fact]
    public async Task Reopening_reuses_the_exact_summary_and_subsequent_prefix_without_resummarizing()
    {
        using Fixture fixture = new();
        AgentEffectContext original = LargeScope();
        await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None);
        AgentEffectContext next = original with
        {
            History = [.. original.History, HistoryEntry.NewUserMessage("next task")]
        };
        AgentContextCompactor reopened = new(new AgentSessionStore(fixture.Store.SessionsRoot));
        (await reopened.CompleteAsync(next, fixture.Client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests.Should().HaveCount(3);
        fixture.Transport.Requests[2].Messages.Take(2).Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[1].Messages.Select(MessageSignature));
        Transport freshTransport = new();
        LlmChatClient freshClient = Client(freshTransport);
        await reopened.CompleteAsync(next, freshClient, CancellationToken.None);
        freshTransport.Requests.Should().ContainSingle();
        Text(freshTransport.Requests[0]).Should().Contain("summary-1").And.Contain("next task");
    }

    [Fact]
    public async Task Reported_prompt_usage_triggers_compaction_even_when_text_estimate_is_small()
    {
        using Fixture fixture = new();
        fixture.Transport.PromptTokens = 3300;
        AgentEffectContext first = Scope(HistoryEntry.NewUserMessage("small text, expensive model envelope"));
        await fixture.Compactor.CompleteAsync(first, fixture.Client, CancellationToken.None);
        fixture.Transport.PromptTokens = 100;
        AgentEffectContext next = first with { History = [.. first.History, HistoryEntry.NewUserMessage("continue")] };
        await fixture.Compactor.CompleteAsync(next, fixture.Client, CancellationToken.None);
        fixture.Transport.Requests.Should().HaveCount(3);
        Text(fixture.Transport.Requests[1]).Should().Contain("Create a concise continuation summary");
    }

    [Fact]
    public async Task Large_fsi_receipts_use_the_folded_length_and_keep_the_append_only_wire_prefix()
    {
        using Fixture fixture = new();
        LlmChatClient client = Client(fixture.Transport, 512000);
        fixture.Transport.PromptTokens = 386539;
        AgentEffectContext first = Scope(HistoryEntry.NewUserMessage("scan headings"));
        await fixture.Compactor.CompleteAsync(first, client, CancellationToken.None);
        string payload = JsonSerializer.Serialize(new
        {
            output = "selected headings",
            sdkOperations = Enumerable.Range(1, 11).Select(index => new
            {
                OperationId = "fetch/" + index, Payload = new string('x', 140000)
            })
        });
        AgentEffectContext next = first with
        {
            History =
            [
                .. first.History,
                HistoryEntry.NewAssistantReply(new AssistantReply("scan", "model", AssistantFinish.Tools,
                    ListModule.OfSeq(new[] { new NativeToolCall("fsi-1", "fsi", "{}", "") }), "")),
                HistoryEntry.NewNativeToolResult("fsi-1", "fsi", payload, false)
            ]
        };
        (await fixture.Compactor.CompleteAsync(next, client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2, "hidden raw receipts must not trigger needless compaction");
        LlmTransportRequest request = fixture.Transport.Requests[1];
        request.Messages.Take(fixture.Transport.Requests[0].Messages.Count).Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[0].Messages.Select(MessageSignature));
        request.Messages.SelectMany(message => message.Parts).OfType<LlmMessagePart.LlmToolResultPart>()
            .Single().Content.Should().Contain("OVERSIZED_TOOL_PAYLOAD").And.Contain("selected headings");
        ((HistoryEntry.NativeToolResult)next.History.Last()).payload.Should().Be(payload);
    }

    [Fact]
    public async Task Summary_requests_also_fold_oversized_original_tool_results()
    {
        using Fixture fixture = new();
        AgentEffectContext original = LargeScope() with
        {
            History =
            [
                HistoryEntry.NewUserMessage(new string('x', 24000)), .. LargeScope().History.Skip(1),
                HistoryEntry.NewToolResult("fetch", new string('z', 1585580))
            ]
        };
        (await fixture.Compactor.CompleteAsync(original, Client(fixture.Transport, 8192), CancellationToken.None))
            .IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2);
        Text(fixture.Transport.Requests[0]).Should().Contain("OVERSIZED_TOOL_PAYLOAD")
            .And.Contain("Create a concise continuation summary");
        Text(fixture.Transport.Requests[0]).Length.Should().BeLessThan(30000);
    }

    [Fact]
    public async Task Changing_the_live_limit_only_affects_new_entries_and_reopening_replays_the_same_prefix()
    {
        using Fixture fixture = new();
        int[] liveLimit = [4096];
        AgentContextCompactor compactor = new(fixture.Store, () => liveLimit[0]);
        AgentEffectContext context = Scope(HistoryEntry.NewToolResult("fetch", new string('x', 5000)));
        LlmChatClient client = Client(fixture.Transport, 32768);
        await compactor.CompleteAsync(context, client, CancellationToken.None);
        Text(fixture.Transport.Requests[0]).Should().Contain("maximum of 4096 characters");
        liveLimit[0] = 32768;
        AgentEffectContext next = context with
        {
            History = [.. context.History, HistoryEntry.NewToolResult("fetch", new string('y', 5000))]
        };
        (await compactor.CompleteAsync(next, client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2);
        fixture.Transport.Requests[1].Messages.Take(fixture.Transport.Requests[0].Messages.Count)
            .Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[0].Messages.Select(MessageSignature));
        Text(fixture.Transport.Requests[1]).Should().Contain("maximum of 4096 characters")
            .And.Contain(new string('y', 5000));
        AgentContextCompactor reopened = new(new AgentSessionStore(fixture.Store.SessionsRoot), () => liveLimit[0]);
        liveLimit[0] = 4096;
        (await reopened.CompleteAsync(next, client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests[2].Messages.Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[1].Messages.Select(MessageSignature));
        AgentEffectContext third = next with
        {
            History = [.. next.History, HistoryEntry.NewToolResult("fetch", new string('z', 5000))]
        };
        (await reopened.CompleteAsync(third, client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests[3].Messages.Take(fixture.Transport.Requests[2].Messages.Count)
            .Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[2].Messages.Select(MessageSignature));
        Text(fixture.Transport.Requests[3]).Should().Contain(new string('y', 5000));
        AgentToolPayloadCheckpoint saved =
            (await new AgentSessionStore(fixture.Store.SessionsRoot).ReadToolPayloadsAsync("session",
                CancellationToken.None))!;
        saved.Entries.Should().Be(3);
        saved.Payloads.Keys.Should().BeEquivalentTo(new[] { 0, 2 });
    }

    [Fact]
    public async Task A_failed_request_freezes_its_folded_prefix_before_settings_change_or_restart()
    {
        using Fixture fixture = new();
        fixture.Transport.FailNextNormal = true;
        AgentEffectContext context = Scope(HistoryEntry.NewToolResult("fetch", new string('x', 5000))) with
        {
            ToolResultMaxCharacters = 4096
        };
        (await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None)).IsFailure.Should()
            .BeTrue();
        AgentContextCompactor reopened = new(new AgentSessionStore(fixture.Store.SessionsRoot));
        (await reopened.CompleteAsync(context with { ToolResultMaxCharacters = 256000 }, fixture.Client,
            CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests[1].Messages.Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[0].Messages.Select(MessageSignature));
    }

    [Fact]
    public async Task Frozen_tool_payloads_reject_reordered_original_history_before_calling_the_model()
    {
        using Fixture fixture = new();
        AgentEffectContext context = Scope(HistoryEntry.NewUserMessage("first"), HistoryEntry.NewToolResult("fetch",
            new string('x', 300000)));
        await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None);
        AgentEffectContext changed = context with { History = [context.History[1], context.History[0]] };
        (await fixture.Compactor.CompleteAsync(changed, fixture.Client, CancellationToken.None)).ErrorCode.Should()
            .Be(LlmFailureCodes.HistoryInvariantViolated);
        fixture.Transport.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Remaining_request_budget_folds_a_result_below_the_configured_character_limit()
    {
        using Fixture fixture = new();
        LlmChatClient client = Client(fixture.Transport, 512000);
        fixture.Transport.PromptTokens = 386539;
        AgentEffectContext first = Scope(HistoryEntry.NewUserMessage("task"));
        await fixture.Compactor.CompleteAsync(first, client, CancellationToken.None);
        string payload = new('中', 60000);
        AgentEffectContext next = first with
        {
            ToolResultMaxCharacters = 256000,
            History = [.. first.History, HistoryEntry.NewToolResult("fetch", payload)]
        };
        (await fixture.Compactor.CompleteAsync(next, client, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2);
        Text(fixture.Transport.Requests[1]).Should().Contain("Configured maximum of 256000 characters")
            .And.Contain("budget of 4096 characters");
        fixture.Transport.Requests[1].MaxTokens.Should().Be(8192);
        ((HistoryEntry.ToolResult)next.History.Last()).payload.Should().Be(payload);
    }

    [Fact]
    public async Task Oversized_non_tool_input_is_rejected_before_sending_a_summary_or_normal_request()
    {
        using Fixture fixture = new();
        Result<LlmChatCompletion> result = await fixture.Compactor.CompleteAsync(
            Scope(HistoryEntry.NewUserMessage(new string('中', 20000))), fixture.Client, CancellationToken.None);
        result.ErrorCode.Should().Be(LlmFailureCodes.ContextLengthExceeded);
        result.ErrorMessage.Should().Contain("configured context capacity 4096").And.Contain("request was not sent");
        fixture.Transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_models_does_not_reuse_the_previous_models_prompt_usage()
    {
        using Fixture fixture = new();
        fixture.Transport.PromptTokens = 3300;
        AgentEffectContext context = Scope(HistoryEntry.NewUserMessage("short task"));
        await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None);
        fixture.Transport.PromptTokens = 100;
        LlmChatClient other = Client(fixture.Transport, model: "other-model");
        (await fixture.Compactor.CompleteAsync(context, other, CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2);
        fixture.Transport.Requests[1].Model.Should().Be("other-model");
        Text(fixture.Transport.Requests[1]).Should().NotContain("Create a concise continuation summary");
    }

    [Fact]
    public async Task Subscription_summary_and_continuation_do_not_send_unsupported_output_overrides()
    {
        using Fixture fixture = new();
        LlmChatClient client = Client(fixture.Transport, subscription: true);
        (await fixture.Compactor.CompleteAsync(LargeScope(), client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(2).And.OnlyContain(request => request.MaxTokens == null);
    }

    [Fact]
    public async Task Provider_confirmed_overflow_summarizes_then_retries_once_with_a_changed_prefix()
    {
        using Fixture fixture = new();
        fixture.Transport.FailNextNormal = true;
        fixture.Transport.NextNormalFailureCode = LlmFailureCodes.ContextLengthExceeded;
        AgentEffectContext context = Scope(HistoryEntry.NewUserMessage(new string('x', 10000)));
        (await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(3);
        Text(fixture.Transport.Requests[1]).Should().Contain("Create a concise continuation summary");
        Text(fixture.Transport.Requests[2]).Should().Contain("summary-1").And.NotContain(new string('x', 100));
        context.History.Should().ContainSingle();
    }

    [Fact]
    public async Task Append_only_journal_restores_frozen_messages_without_the_convenience_snapshots()
    {
        using Fixture fixture = new();
        AgentEffectContext context = Scope(HistoryEntry.NewToolResult("fetch", new string('x', 5000))) with
        {
            ToolResultMaxCharacters = 4096
        };
        await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None);
        string directory = fixture.Store.ResolveSessionDirectory("session");
        File.Delete(Path.Combine(directory, "tool-payloads.json"));
        File.Delete(Path.Combine(directory, "compaction.json"));
        AgentSessionStore reopenedStore = new(fixture.Store.SessionsRoot);
        AgentContextCompactor reopened = new(reopenedStore);
        (await reopened.CompleteAsync(context with { ToolResultMaxCharacters = 256000 }, fixture.Client,
            CancellationToken.None)).IsSuccess.Should().BeTrue();
        fixture.Transport.Requests[1].Messages.Select(MessageSignature).Should()
            .Equal(fixture.Transport.Requests[0].Messages.Select(MessageSignature));
        IReadOnlyList<AgentSessionLogEntry> log = await reopenedStore.ReadLogAsync("session", CancellationToken.None);
        log.Select(entry => entry.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        log.Count(entry => entry.Kind == "context/tool-payloads").Should().Be(1);
    }

    [Fact]
    public async Task History_pages_leave_room_for_the_next_summary_near_the_context_threshold()
    {
        using Fixture fixture = new();
        fixture.Transport.PromptTokens = 3300;
        AgentEffectContext context = Scope(HistoryEntry.NewUserMessage("needle " + new string('x', 2000)));
        await fixture.Compactor.CompleteAsync(context, fixture.Client, CancellationToken.None);
        int characters = await fixture.Compactor.HistoryPageBudgetAsync(context, 16000, CancellationToken.None);
        characters.Should().BeLessThan(16000).And.BeGreaterThanOrEqualTo(2);
        AgentToolOutcome page = AgentHistoryTool.Read(context with { HistoryPageCharacters = characters },
            """{"start":1,"limit":1}""");
        AgentEffectContext next = context with
        {
            History = [.. context.History, HistoryEntry.NewToolResult("history", page.Payload)]
        };
        fixture.Transport.PromptTokens = 100;
        (await fixture.Compactor.CompleteAsync(next, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        fixture.Transport.Requests.Should().HaveCount(3);
        Text(fixture.Transport.Requests[1]).Should().Contain("Create a concise continuation summary");
    }

    [Theory]
    [InlineData("length")]
    [InlineData("tool_calls")]
    public async Task Invalid_summary_keeps_original_history_and_can_be_retried(string finish)
    {
        using Fixture fixture = new();
        fixture.Transport.SummaryFinish = finish;
        AgentEffectContext original = LargeScope();
        (await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None)).IsFailure.Should()
            .BeTrue();
        (await fixture.Store.ReadCompactionAsync("session", CancellationToken.None)).Should().BeNull();
        fixture.Transport.SummaryFinish = "stop";
        (await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None)).IsSuccess.Should()
            .BeTrue();
        original.History.Should().HaveCount(3);
    }

    [Fact]
    public async Task Cancellation_during_summary_does_not_publish_a_replacement()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.BeforeSummary = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        Task<Result<LlmChatCompletion>> work = fixture.Compactor.CompleteAsync(
            LargeScope(), fixture.Client, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        (await work).ErrorCode.Should().Be(LlmFailureCodes.Cancelled);
        (await fixture.Store.ReadCompactionAsync("session", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_checkpoint_cannot_be_applied_to_changed_original_history()
    {
        using Fixture fixture = new();
        AgentEffectContext original = LargeScope();
        await fixture.Compactor.CompleteAsync(original, fixture.Client, CancellationToken.None);
        AgentEffectContext changed = original with
        {
            History = [HistoryEntry.NewUserMessage("changed"), .. original.History.Skip(1)]
        };
        Result<LlmChatCompletion> result = await fixture.Compactor.CompleteAsync(changed,
            fixture.Client, CancellationToken.None);
        result.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        fixture.Transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void History_search_and_long_entry_paging_are_session_local_and_lossless()
    {
        AgentEffectContext context = Scope(HistoryEntry.NewUserMessage("unrelated"),
            HistoryEntry.NewUserMessage("needle " + new string('z', 22000)));
        AgentToolOutcome first = AgentHistoryTool.Read(context, """{"query":"needle","limit":1}""");
        first.Succeeded.Should().BeTrue();
        using JsonDocument page = JsonDocument.Parse(first.Payload);
        page.RootElement.GetProperty("entries")[0].GetProperty("index").GetInt32().Should().Be(2);
        int start = page.RootElement.GetProperty("nextStart").GetInt32();
        int offset = page.RootElement.GetProperty("nextOffset").GetInt32();
        string head = page.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!;
        AgentToolOutcome second =
            AgentHistoryTool.Read(context, JsonSerializer.Serialize(new { start, offset, limit = 1 }));
        using JsonDocument tail = JsonDocument.Parse(second.Payload);
        string reconstructed = head + tail.RootElement.GetProperty("entries")[0].GetProperty("content").GetString();
        using JsonDocument entry = JsonDocument.Parse(reconstructed);
        entry.RootElement.GetProperty("text").GetString().Should().Be("needle " + new string('z', 22000));
        AgentHistoryTool.Read(Scope(HistoryEntry.NewUserMessage("another session")), """{"query":"needle"}""")
            .Payload.Should().NotContain("needle");
    }

    [Theory]
    [InlineData("{\"start\":\"1\"}")]
    [InlineData("{\"limit\":21}")]
    [InlineData("{\"sessionId\":\"other\"}")]
    [InlineData("{\"query\":null}")]
    [InlineData("{\"offset\":-1}")]
    [InlineData("{\"start\":1,\"start\":2}")]
    public void History_rejects_invalid_arguments_without_reading_another_session(string arguments)
    {
        AgentHistoryTool.Read(Scope(HistoryEntry.NewUserMessage("private")), arguments).ErrorCode.Should()
            .Be("INVALID_ARGUMENT");
    }

    [Fact]
    public void History_search_matches_unicode_text_before_json_escaping()
    {
        AgentToolOutcome result = AgentHistoryTool.Read(Scope(HistoryEntry.NewUserMessage("保留术语：上下文压缩")),
            """{"query":"上下文"}""");
        result.Succeeded.Should().BeTrue();
        using JsonDocument output = JsonDocument.Parse(result.Payload);
        output.RootElement.GetProperty("entries").GetArrayLength().Should().Be(1);
        output.RootElement.GetProperty("entries")[0].GetProperty("content").GetString().Should().Contain("上下文压缩");
    }

    private static AgentEffectContext Scope(params HistoryEntry[] history)
    {
        return new AgentEffectContext("session", "fixed system instructions", [], history);
    }

    private static AgentEffectContext LargeScope()
    {
        return Scope(HistoryEntry.NewUserMessage(new string('x', 12800)),
            HistoryEntry.NewAssistantReply(new AssistantReply("lookup", "model", AssistantFinish.Tools,
                ListModule.OfSeq(new[] { new NativeToolCall("call-1", "fetch", "{}", "") }), "")),
            HistoryEntry.NewNativeToolResult("call-1", "fetch", "original evidence", false));
    }

    private static LlmChatClient Client(Transport transport, int capacity = 4096, string model = "model",
        bool subscription = false)
    {
        return new LlmChatClient(new LlmProviderRuntimeSettings(
                LlmProviderCatalog.Find(subscription ? "openai-subscription" : "openai")!, "test",
                "https://example.invalid", model, "", "", "",
                subscription ? LlmAuthenticationModes.Subscription : LlmAuthenticationModes.ApiKey)
            { ContextWindowTokens = capacity }, transport);
    }

    private static string Text(LlmTransportRequest request)
    {
        return string.Join("\n", request.Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmTextPart>().Select(part => part.Text));
    }

    private static string MessageSignature(LlmTransportMessage message)
    {
        return message.Role + ":" + string.Join("\n", message.Parts.Select(part => part.ToString()));
    }

    private sealed class Transport : ILlmChatTransport
    {
        public List<LlmTransportRequest> Requests { get; } = [];
        public Queue<IReadOnlyList<LlmToolCall>> NormalTools { get; } = new();
        public int PromptTokens { get; set; } = 100;
        public string SummaryFinish { get; set; } = "stop";
        public Func<CancellationToken, Task>? BeforeSummary { get; set; }
        public bool FailNextNormal { get; set; }
        public string NextNormalFailureCode { get; set; } = LlmFailureCodes.TemporaryProviderError;
        private int _summaries;

        public async Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            bool summary = request.Messages.Last().Parts.OfType<LlmMessagePart.LlmTextPart>()
                .Any(part => part.Text.Contains("Create a concise continuation summary"));
            if (summary && BeforeSummary is { } before)
            {
                await before(cancellationToken);
            }

            if (!summary && FailNextNormal)
            {
                FailNextNormal = false;
                return LlmTransportResult.Failure(NextNormalFailureCode, "test provider failure");
            }

            return LlmTransportResult.Success(summary ? "summary-" + ++_summaries : "continued", "model",
                    summary ? SummaryFinish : "stop",
                    new LlmUsage(PromptTokens, 10, PromptTokens + 10, null, null)) with
                {
                    ToolCalls = summary ? SummaryFinish == "tool_calls" ? [new LlmToolCall("bad", "fetch", "{}")] : []
                    : NormalTools.Count > 0 ? NormalTools.Dequeue() : []
                };
        }
    }

    private sealed class Provider(ILlmChatClient client) : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        }
    }

    private sealed class UnusedGateway : IAgentMcpGateway
    {
        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("History must stay within the calling session.");
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("History is read-only.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AgentSessionStore Store { get; } = new(Path.Combine(Path.GetTempPath(), "patchouli-compaction-tests",
            Guid.NewGuid().ToString("N")));

        public AgentContextCompactor Compactor { get; }
        public Transport Transport { get; } = new();
        public LlmChatClient Client { get; }

        public Fixture()
        {
            Compactor = new AgentContextCompactor(Store);
            Client = AgentContextCompactionTests.Client(Transport);
        }

        public void Dispose()
        {
            if (Directory.Exists(Store.SessionsRoot))
            {
                Directory.Delete(Store.SessionsRoot, true);
            }
        }
    }
}
