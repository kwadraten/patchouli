using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Covers the ADR 0036 append-only prefix invariant and the API misuse guards that keep it: these tests
/// never touch the network, they assert what would be sent.
/// </summary>
public sealed class LlmChatClientTests
{
    private static readonly LlmProviderCatalogEntry OpenAi = LlmProviderCatalog.Find("openai")!;

    [Fact]
    public async Task Native_tool_history_and_a_followup_message_reach_the_transport_without_losing_types()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatMessage[] messages =
        [
            LlmChatMessage.User("Translate page 1."),
            new(LlmChatRole.Assistant,
            [
                new LlmMessagePart.LlmAssistantMetadataPart("provider metadata"),
                new LlmMessagePart.LlmToolCallPart("fetch-1", "fetch", "{\"uris\":[\"page-1\"]}", "signature")
            ]),
            new(LlmChatRole.Tool, [new LlmMessagePart.LlmToolResultPart("fetch-1", "fetch", "source page", false)]),
            new(LlmChatRole.Assistant,
            [
                new LlmMessagePart.LlmTextPart("Saving the translation."),
                new LlmMessagePart.LlmToolCallPart("put-1", "put", "{\"uri\":\"translation-1\",\"content\":\"text\"}")
            ]),
            new(LlmChatRole.Tool, [new LlmMessagePart.LlmToolResultPart("put-1", "put", "validation failed", true)])
        ];
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append(messages);

        Result<LlmChatCompletion> first = await client.CompleteAsync("session-1", new LlmChatRequest(history));
        LlmChatHistory continued = history.Append([.. messages, LlmChatMessage.User("Continue translating.")]);
        Result<LlmChatCompletion> second = await client.CompleteAsync("session-1", new LlmChatRequest(continued));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        transport.Requests.Should().HaveCount(2);
        transport.Requests[0].Messages.Skip(1).Should().Equal(messages.Select(message =>
            new LlmTransportMessage(message.Role, message.Parts)));
        transport.Requests[1].Messages.Take(6).Select(Describe)
            .Should().Equal(transport.Requests[0].Messages.Select(Describe));
        transport.Requests[1].Messages.Last().Role.Should().Be(LlmChatRole.User);
    }

    [Fact]
    public async Task An_assistant_reply_with_only_text_and_provider_metadata_can_continue()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append(
        [
            LlmChatMessage.User("Hello."),
            new LlmChatMessage(LlmChatRole.Assistant,
            [
                new LlmMessagePart.LlmTextPart("Hello."),
                new LlmMessagePart.LlmAssistantMetadataPart("")
            ]),
            LlmChatMessage.User("Continue.")
        ]);

        Result<LlmChatCompletion> result = await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsSuccess.Should().BeTrue();
        transport.Requests.Should().ContainSingle();
        transport.Requests[0].Messages[2].Parts[1].Should().Be(new LlmMessagePart.LlmAssistantMetadataPart(""));
    }

    [Fact]
    public async Task Repeated_calls_with_appended_history_keep_the_prefix_and_send_the_suffix()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("You are a translator.", ["tool:fetch"])
            .Append(
            [
                LlmChatMessage.User("page 1"),
                LlmChatMessage.Assistant("translated 1")
            ]);

        Result<LlmChatCompletion> first = await client.CompleteAsync("session-1", new LlmChatRequest(history));
        LlmChatHistory extended = history.Append(
        [
            LlmChatMessage.User("page 1"),
            LlmChatMessage.Assistant("translated 1"),
            LlmChatMessage.User("page 2")
        ]);
        Result<LlmChatCompletion> second = await client.CompleteAsync("session-1", new LlmChatRequest(extended));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        transport.Requests.Should().HaveCount(2);
        transport.Requests[0].Messages.Should().HaveCount(3);
        transport.Requests[1].Messages.Should().HaveCount(4);
        transport.Requests[1].Messages.Take(3).Select(Describe)
            .Should().Equal(transport.Requests[0].Messages.Select(Describe));
        first.Value.PrefixSignature.Should().NotBe(second.Value.PrefixSignature);
    }

    [Fact]
    public async Task Replacing_a_recorded_message_is_refused_without_calling_the_provider()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("page 1")]);
        await client.CompleteAsync("session-1", new LlmChatRequest(history));

        // A brand-new history whose first message differs: same shape, different content.
        Result<LlmChatCompletion> replaced = await client.CompleteAsync("session-1",
            new LlmChatRequest(LlmChatHistory.Create("instructions")
                .Append([LlmChatMessage.User("page 1 changed"), LlmChatMessage.User("page 2")])));

        replaced.IsFailure.Should().BeTrue();
        replaced.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        LlmFailureClassifier.Classify(replaced.ErrorCode).Should().Be(LlmFailureClassification.NonRetryable);

        // Appending the same content as a new entry is legal: appended content is not a rewrite.
        Result<LlmChatCompletion> appended = await client.CompleteAsync("session-1",
            new LlmChatRequest(history.Append([LlmChatMessage.User("page 1")])));
        appended.IsSuccess.Should().BeTrue();
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Dropping_messages_between_calls_is_refused()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions")
            .Append(
            [
                LlmChatMessage.User("page 1"),
                LlmChatMessage.Assistant("translated 1")
            ]);
        await client.CompleteAsync("session-1", new LlmChatRequest(history));

        // The second call truncates the recorded prefix back to a single message.
        LlmChatHistory truncated = LlmChatHistory.Create("instructions")
            .Append([LlmChatMessage.User("page 1")]);
        Result<LlmChatCompletion> result = await client.CompleteAsync("session-1",
            new LlmChatRequest(truncated.Append([LlmChatMessage.User("page 1")])));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        result.ErrorMessage.Should().Contain("append-only");
        transport.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Changing_the_instructions_between_calls_is_refused()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        await client.CompleteAsync("session-1",
            new LlmChatRequest(LlmChatHistory.Create("instructions v1")
                .Append([LlmChatMessage.User("page 1")])));

        Result<LlmChatCompletion> changed = await client.CompleteAsync("session-1",
            new LlmChatRequest(LlmChatHistory.Create("instructions v2")
                .Append([LlmChatMessage.User("page 1")])));

        changed.IsFailure.Should().BeTrue();
        changed.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        changed.ErrorMessage.Should().Contain("append-only");
        transport.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Changing_tool_definitions_between_calls_is_refused()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("page 1")]);
        await client.CompleteAsync("session-1", new LlmChatRequest(history, ["tool:fetch"]));

        Result<LlmChatCompletion> changed = await client.CompleteAsync("session-1",
            new LlmChatRequest(history, ["tool:fetch", "tool:put"]));

        changed.IsFailure.Should().BeTrue();
        changed.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        changed.ErrorMessage.Should().Contain("Tool definitions");
        transport.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Separate_conversation_keys_track_their_own_prefix()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        await client.CompleteAsync("session-1",
            new LlmChatRequest(LlmChatHistory.Create("a").Append([LlmChatMessage.User("one")])));

        Result<LlmChatCompletion> other = await client.CompleteAsync("session-2",
            new LlmChatRequest(LlmChatHistory.Create("b").Append([LlmChatMessage.User("two")])));

        other.IsSuccess.Should().BeTrue();
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_blank_conversation_key_is_refused()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);

        Result<LlmChatCompletion> result = await client.CompleteAsync("  ",
            new LlmChatRequest(LlmChatHistory.Create("i").Append([LlmChatMessage.User("one")])));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Vision_input_is_appended_after_the_stable_prefix()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("OCR this page.").Append([LlmChatMessage.User("page 7")]);

        Result<LlmChatCompletion> result = await client.CompleteVisionAsync("ocr-page-7",
            new LlmChatRequest(history),
            new LlmVisionInput("data:image/png;base64,AAAA", "image/png", "Return Markdown.", "high"));

        result.IsSuccess.Should().BeTrue();
        LlmTransportRequest sent = transport.Requests.Single();
        sent.Messages.Should().HaveCount(3);
        sent.Messages[0].Parts.Single().Should().Be(new LlmMessagePart.LlmTextPart("OCR this page."));
        sent.Messages[1].Parts.Single().Should().Be(new LlmMessagePart.LlmTextPart("page 7"));
        sent.Messages[2].Parts.Should().Equal(
            new LlmMessagePart.LlmTextPart("Return Markdown."),
            new LlmMessagePart.LlmImagePart("data:image/png;base64,AAAA", "image/png", "high"));
    }

    [Fact]
    public async Task A_repeated_vision_call_on_the_same_history_is_accepted()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("page 7")]);

        Result<LlmChatCompletion> first = await client.CompleteVisionAsync("ocr-page-7",
            new LlmChatRequest(history), new LlmVisionInput("data:image/png;base64,AAAA", "image/png"));
        Result<LlmChatCompletion> second = await client.CompleteVisionAsync("ocr-page-7",
            new LlmChatRequest(history), new LlmVisionInput("data:image/png;base64,AAAA", "image/png"));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.PrefixSignature.Should().Be(first.Value.PrefixSignature);
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Request_options_reach_the_transport_and_defaults_come_from_the_provider()
    {
        RecordingTransport transport = new();
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        await client.CompleteAsync("session-1",
            new LlmChatRequest(history, null, new LlmChatRequestOptions("override-model", 0.2, 512),
                LlmCacheOptions.Ephemeral("session-1")));
        await client.CompleteAsync("session-1", new LlmChatRequest(history));

        transport.Requests[0].Model.Should().Be("override-model");
        transport.Requests[0].Temperature.Should().Be(0.2);
        transport.Requests[0].MaxTokens.Should().Be(512);
        transport.Requests[0].AutoCache.Should().BeTrue();
        transport.Requests[0].PromptCacheKey.Should().Be("session-1");
        transport.Requests[1].Model.Should().Be("gpt-4o-mini");
        transport.Requests[1].AutoCache.Should().BeFalse();
    }

    [Fact]
    public async Task Transport_failure_codes_survive_as_classified_results()
    {
        RecordingTransport transport = new()
        {
            Result = LlmTransportResult.Failure(LlmFailureCodes.RateLimited, "slow down")
        };
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result =
            await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.RateLimited);
        LlmFailureClassifier.IsRetryable(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task An_exception_from_the_transport_becomes_a_classified_failure()
    {
        RecordingTransport transport = new() { Throw = new TimeoutException("no response") };
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result =
            await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.NetworkTimeout);
        LlmFailureClassifier.IsRetryable(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_of_an_in_flight_call_is_reported_as_a_retryable_cancelled_failure()
    {
        RecordingTransport transport = new() { HonorCancellation = true };
        LlmChatClient client = CreateClient(transport);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result = await client.CompleteAsync("session-1",
            new LlmChatRequest(history), cancellation.Token);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.Cancelled);
        LlmFailureClassifier.Classify(result.ErrorCode).Should().Be(LlmFailureClassification.TransientRetryable);
    }

    [Fact]
    public async Task An_empty_completion_is_reported_as_manual_repair()
    {
        RecordingTransport transport = new()
        {
            Result = LlmTransportResult.Success("   ", "gpt-4o-mini", "stop", LlmUsage.Empty)
        };
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result =
            await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.EmptyResponse);
        LlmFailureClassifier.RequiresManualRepair(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task A_successful_completion_reports_usage_and_the_requested_cache_hint()
    {
        RecordingTransport transport = new()
        {
            Result = LlmTransportResult.Success("translated", "gpt-4o-2024-08-06", "stop",
                new LlmUsage(100, 20, 120, 80, null))
        };
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result = await client.CompleteAsync("session-1",
            new LlmChatRequest(history, null, null, LlmCacheOptions.Ephemeral("session-1")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("translated");
        result.Value.Model.Should().Be("gpt-4o-2024-08-06");
        result.Value.ProviderId.Should().Be("openai");
        result.Value.FinishReason.Should().Be("stop");
        result.Value.Usage.Should().Be(new LlmUsage(100, 20, 120, 80, null));
        result.Value.AutoCacheRequested.Should().BeTrue();
        result.Value.PromptCacheKey.Should().Be("session-1");
        result.Value.PrefixSignature.Should().HaveLength(64);
    }

    [Fact]
    public void Transport_failure_mapping_covers_status_codes_and_exception_kinds()
    {
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.Unauthorized)
            .Should().Be(LlmFailureCodes.AuthFailed);
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.Forbidden)
            .Should().Be(LlmFailureCodes.AuthFailed);
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.TooManyRequests)
            .Should().Be(LlmFailureCodes.RateLimited);
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.NotFound)
            .Should().Be(LlmFailureCodes.ModelNotFound);
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.RequestEntityTooLarge)
            .Should().Be(LlmFailureCodes.ContextLengthExceeded);
        LlmFailureMapper.FromStatusCode(System.Net.HttpStatusCode.BadGateway)
            .Should().Be(LlmFailureCodes.TemporaryProviderError);
        LlmFailureMapper.FromException(new TimeoutException())
            .Should().Be(LlmFailureCodes.NetworkTimeout);
        LlmFailureMapper.FromException(new TaskCanceledException())
            .Should().Be(LlmFailureCodes.NetworkTimeout);
        LlmFailureMapper.FromException(new HttpRequestException("tls"))
            .Should().Be(LlmFailureCodes.TemporaryProviderError);
        LlmFailureMapper.FromException(new HttpRequestException("throttled", null,
                System.Net.HttpStatusCode.TooManyRequests))
            .Should().Be(LlmFailureCodes.RateLimited);
        LlmFailureMapper.FromException(new InvalidOperationException("boom"))
            .Should().Be(LlmFailureCodes.UnknownProviderError);
        Action nullException = () => LlmFailureMapper.FromException(null!);
        nullException.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_non_cancellation_operation_cancel_exception_is_treated_as_a_timeout()
    {
        RecordingTransport transport = new() { Throw = new TaskCanceledException("timed out") };
        LlmChatClient client = CreateClient(transport);
        LlmChatHistory history = LlmChatHistory.Create("instructions").Append([LlmChatMessage.User("hi")]);

        Result<LlmChatCompletion> result =
            await client.CompleteAsync("session-1", new LlmChatRequest(history));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.NetworkTimeout);
    }

    private static LlmChatClient CreateClient(ILlmChatTransport transport)
    {
        return new LlmChatClient(
            new LlmProviderRuntimeSettings(OpenAi, "sk-test", "https://api.openai.com/{0}/{1}", "gpt-4o-mini", "", "",
                ""),
            transport);
    }

    private static string Describe(LlmTransportMessage message)
    {
        return $"{message.Role}:" + string.Join('|', message.Parts.Select(part => part.ToString()));
    }

    private sealed class RecordingTransport : ILlmChatTransport
    {
        private readonly Lock _gate = new();
        private readonly List<LlmTransportRequest> _requests = [];

        public IReadOnlyList<LlmTransportRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public LlmTransportResult? Result { get; init; }

        public Exception? Throw { get; init; }

        /// <summary>When true the transport awaits the token, so an in-flight cancellation is observable.</summary>
        public bool HonorCancellation { get; init; }

        public async Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _requests.Add(request);
            }

            if (HonorCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Result ?? LlmTransportResult.Success("ok", request.Model, "stop", LlmUsage.Empty);
        }
    }
}
