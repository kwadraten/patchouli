using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: contract tests for <see cref="ITranslationModelInvoker"/>. Every case uses a fake
/// <see cref="ILlmChatClient"/>, so no test ever reaches a real provider.
/// </summary>
public sealed class TranslationModelInvokerTests
{
    [Fact]
    public async Task Response_entries_are_mapped_by_id_regardless_of_their_order()
    {
        TranslationSegment first = NewSegment("one");
        TranslationSegment second = NewSegment("two");
        TranslationSegment third = NewSegment("three");
        TranslationBatch batch = NewBatch(first, second, third);
        FakeChatClient client = new(Json(
            (third.SegmentId, "tres"),
            (first.SegmentId, "uno"),
            (second.SegmentId, "dos")));

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(batch, Context());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.BatchIndex.Should().Be(0);
        result.Value.RequestedSegmentIds.Should().Equal(batch.SegmentIds);
        result.Value.Translations.Should().HaveCount(3);
        Dictionary<string, TranslationSegmentTranslation> byId =
            result.Value.Translations.ToDictionary(translation => translation.SegmentId);
        byId[first.SegmentId].TranslatedText.Should().Be("uno");
        byId[second.SegmentId].TranslatedText.Should().Be("dos");
        byId[third.SegmentId].TranslatedText.Should().Be("tres");
        byId[first.SegmentId].BoxId.Should().Be(first.BoxId);
        byId[first.SegmentId].PartIndex.Should().Be(first.PartIndex);
    }

    [Fact]
    public async Task Response_inside_a_markdown_json_fence_is_accepted()
    {
        TranslationSegment segment = NewSegment("hello");
        FakeChatClient client = new(
            "Here is the translation:\n```json\n" +
            Json((segment.SegmentId, "hola")) +
            "\n```\n");

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(NewBatch(segment), Context());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Translations.Should().ContainSingle()
            .Which.TranslatedText.Should().Be("hola");
    }

    [Fact]
    public async Task Missing_ids_fail_with_batch_mismatch_details()
    {
        TranslationSegment first = NewSegment("one");
        TranslationSegment second = NewSegment("two");
        FakeChatClient client = new(Json((first.SegmentId, "uno")));

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(NewBatch(first, second), Context());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = result.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.MissingSegmentIds.Should().Equal(second.SegmentId);
        details.DuplicateSegmentIds.Should().BeEmpty();
        details.UnknownSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_ids_fail_with_batch_mismatch_details()
    {
        TranslationSegment first = NewSegment("one");
        TranslationSegment second = NewSegment("two");
        FakeChatClient client = new(Json(
            (first.SegmentId, "uno"),
            (first.SegmentId, "eins"),
            (second.SegmentId, "dos")));

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(NewBatch(first, second), Context());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = result.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.DuplicateSegmentIds.Should().Equal(first.SegmentId);
        details.MissingSegmentIds.Should().BeEmpty();
        details.UnknownSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_ids_fail_with_batch_mismatch_details()
    {
        TranslationSegment first = NewSegment("one");
        TranslationSegment second = NewSegment("two");
        string unknownId = TranslationSegmentIds.Create(DocumentBoxId.New(), 1);
        FakeChatClient client = new(Json(
            (first.SegmentId, "uno"),
            (second.SegmentId, "dos"),
            (unknownId, "extra")));

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(NewBatch(first, second), Context());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        TranslationBatchMismatchDetails details = result.Details
            .Should().BeOfType<TranslationBatchMismatchDetails>().Subject;
        details.UnknownSegmentIds.Should().Equal(unknownId);
        details.MissingSegmentIds.Should().BeEmpty();
        details.DuplicateSegmentIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON at all")]
    [InlineData("[{\"id\":\"a#1\",\"content\":\"x\"}")]
    [InlineData("[{oops}]")]
    [InlineData("{\"id\":\"a#1\",\"content\":\"x\"}")]
    [InlineData("[1,2]")]
    public async Task Unparseable_or_non_array_responses_fail_with_invalid_model_response(string response)
    {
        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(new FakeChatClient(response))
            .InvokeAsync(NewBatch(NewSegment()), Context());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TranslationErrorCodes.InvalidModelResponse);
    }

    [Fact]
    public async Task Every_invocation_uses_a_fresh_conversation_key()
    {
        TranslationSegment segment = NewSegment();
        FakeChatClient client = new(Json((segment.SegmentId, "uno")));
        LlmTranslationModelInvoker invoker = new(client);

        await invoker.InvokeAsync(NewBatch(segment), Context());
        await invoker.InvokeAsync(NewBatch(segment), Context());

        client.Calls.Should().HaveCount(2);
        client.Calls.Select(call => call.ConversationKey).Should().OnlyHaveUniqueItems();
        client.Calls.Should().OnlyContain(call =>
            call.ConversationKey.StartsWith(LlmTranslationModelInvoker.ConversationKeyPrefix,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reference_context_is_prompt_context_and_never_a_translatable_segment()
    {
        const string marker = "REFERENCE_MARKER_TEXT";
        TranslationSegment first = NewSegment("alpha");
        TranslationSegment second = NewSegment("beta");
        FakeChatClient client = new(Json(
            (first.SegmentId, "uno"),
            (second.SegmentId, "dos")));
        TranslationInvocationContext context = Context() with
        {
            ReferenceContexts =
            [
                new TranslationReferenceContext(
                    TranslationReferenceKind.PreviousPageSource,
                    marker,
                    "page 1")
            ]
        };

        Result<TranslationBatchResult> result = await new LlmTranslationModelInvoker(client)
            .InvokeAsync(NewBatch(first, second), context);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Translations.Should().HaveCount(2);
        result.Value.Translations.Select(translation => translation.TranslatedText)
            .Should().NotContain(text => text.Contains(marker, StringComparison.Ordinal));

        string prompt = UserPrompt(client.Calls.Single().Request);
        prompt.Should().Contain(marker, "reference material is supplied as context");
        string segmentsJson = SegmentsJson(prompt);
        segmentsJson.Should().NotContain(marker);
        using JsonDocument document = JsonDocument.Parse(segmentsJson);
        document.RootElement.GetArrayLength().Should().Be(2);
        document.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString())
            .Should().Equal(first.SegmentId, second.SegmentId);
    }

    [Fact]
    public async Task A_slow_model_call_times_out_with_a_network_timeout_failure()
    {
        TranslationSegment segment = NewSegment();
        FakeChatClient client = new(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Reply("unreachable");
        });
        LlmTranslationModelInvoker invoker = new(client);

        Result<TranslationBatchResult> result = await invoker
            .InvokeAsync(NewBatch(segment), Context(TimeSpan.FromMilliseconds(100)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LlmFailureCodes.NetworkTimeout);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_timeout()
    {
        TranslationSegment segment = NewSegment();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient client = new(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Reply("unreachable");
        });
        LlmTranslationModelInvoker invoker = new(client);
        using CancellationTokenSource cancellation = new();

        Task<Result<TranslationBatchResult>> invocation = invoker.InvokeAsync(
            NewBatch(segment),
            Context(),
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));

        using CancellationTokenSource alreadyCancelled = new();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.InvokeAsync(NewBatch(segment), Context(), alreadyCancelled.Token));
    }

    private static TranslationSegment NewSegment(string text = "source text")
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        return new TranslationSegment(TranslationSegmentIds.Create(boxId, 1), boxId, 1, text);
    }

    private static TranslationBatch NewBatch(params TranslationSegment[] segments)
    {
        return new TranslationBatch(0, segments);
    }

    private static TranslationInvocationContext Context(TimeSpan? timeout = null)
    {
        return new TranslationInvocationContext("en", timeout ?? TimeSpan.FromSeconds(30));
    }

    private static string Json(params (string Id, string Content)[] entries)
    {
        return string.Concat(
            "[",
            string.Join(",", entries.Select(entry =>
                $"{{\"id\":{JsonSerializer.Serialize(entry.Id)},\"content\":{JsonSerializer.Serialize(entry.Content)}}}")),
            "]");
    }

    private static Result<LlmChatCompletion> Reply(string text)
    {
        return Result<LlmChatCompletion>.Success(new LlmChatCompletion(
            text,
            "test-model",
            "test-provider",
            "stop",
            LlmUsage.Empty,
            false,
            null,
            "prefix"));
    }

    private static string UserPrompt(LlmChatRequest request)
    {
        LlmChatMessage message = request.History.Messages[^1];
        message.Role.Should().Be(LlmChatRole.User);
        return message.Parts.OfType<LlmMessagePart.LlmTextPart>().Single().Text;
    }

    private static string SegmentsJson(string prompt)
    {
        const string marker = "Segments to translate (JSON array):";
        int markerIndex = prompt.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0);
        int arrayStart = prompt.IndexOf('\n', markerIndex) + 1;
        int arrayEnd = prompt.IndexOf('\n', arrayStart);
        return prompt[arrayStart..arrayEnd].Trim();
    }

    private sealed class FakeChatClient : ILlmChatClient
    {
        private readonly Func<string, LlmChatRequest, CancellationToken, Task<Result<LlmChatCompletion>>> _handler;
        private readonly object _gate = new();
        private readonly List<(string ConversationKey, LlmChatRequest Request)> _calls = [];

        public FakeChatClient(string response)
            : this((_, _, _) => Task.FromResult(Reply(response)))
        {
        }

        public FakeChatClient(
            Func<string, LlmChatRequest, CancellationToken, Task<Result<LlmChatCompletion>>> handler)
        {
            _handler = handler;
        }

        public IReadOnlyList<(string ConversationKey, LlmChatRequest Request)> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public Task<Result<LlmChatCompletion>> CompleteAsync(
            string conversationKey,
            LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _calls.Add((conversationKey, request));
            }

            return _handler(conversationKey, request, cancellationToken);
        }

        public Task<Result<LlmChatCompletion>> CompleteVisionAsync(
            string conversationKey,
            LlmChatRequest request,
            LlmVisionInput vision,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Translation never uses vision calls.");
        }
    }
}
