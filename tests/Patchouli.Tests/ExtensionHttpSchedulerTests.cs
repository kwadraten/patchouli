using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Llm;
using Xunit;

namespace Patchouli.Tests;

public sealed class ExtensionHttpSchedulerTests
{
    [Fact]
    public void Normalization_strips_queries_fragments_and_normalizes_authority()
    {
        Uri uri1 = new("https://TRANSLATE.GoogleApis.com:443/translate_a/single?client=gtx&sl=auto&tl=fr&q=hello#frag");
        Uri uri2 = new("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=de&q=secret123");

        string key1 = ExtensionHttpScheduler.NormalizeQuotaKey(uri1);
        string key2 = ExtensionHttpScheduler.NormalizeQuotaKey(uri2);

        key1.Should().Be("https://translate.googleapis.com/translate_a/single");
        key2.Should().Be("https://translate.googleapis.com/translate_a/single");
        key1.Should().Be(key2, "normalized keys group by endpoint without leaking query parameters or secrets");
    }

    [Fact]
    public void Retry_after_parses_seconds_correctly()
    {
        ExtensionHttpScheduler scheduler = new();
        DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        TimeSpan delay = scheduler.ParseRetryAfter("120", now, TimeSpan.FromSeconds(5));

        delay.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData("Fri, 23 Oct 2026 12:02:30 GMT", 150)] // RFC 1123
    [InlineData("Friday, 23-Oct-26 12:02:30 GMT", 150)] // RFC 850
    [InlineData("Fri Oct 23 12:02:30 2026", 150)] // asctime
    public void Retry_after_parses_rfc_http_date_formats(string dateHeader, int expectedSeconds)
    {
        ExtensionHttpScheduler scheduler = new();
        DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        TimeSpan delay = scheduler.ParseRetryAfter(dateHeader, now, TimeSpan.FromSeconds(5));

        delay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("Fri, 23 Oct 2020 12:00:00 GMT")] // Past RFC 1123
    [InlineData("Friday, 23-Oct-20 12:00:00 GMT")] // Past RFC 850
    [InlineData("Fri Oct 23 12:00:00 2020")] // Past asctime
    [InlineData("0")] // Expired seconds
    [InlineData("-10")] // Invalid seconds
    public void Retry_after_applies_deterministic_fallback_on_expired_or_past_values(string header)
    {
        ExtensionHttpScheduler scheduler = new();
        DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        TimeSpan fallback = TimeSpan.FromSeconds(7);

        TimeSpan delay = scheduler.ParseRetryAfter(header, now, fallback);

        delay.Should().Be(fallback);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("malformed-header-content")]
    [InlineData("999999999999999999999999999999")]
    public void Retry_after_applies_deterministic_fallback_on_missing_or_malformed_headers(string? header)
    {
        ExtensionHttpScheduler scheduler = new();
        DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        TimeSpan fallback = TimeSpan.FromSeconds(4);

        TimeSpan delay = scheduler.ParseRetryAfter(header, now, fallback);

        delay.Should().Be(fallback);
    }

    [Fact]
    public async Task Shared_cooldown_and_concurrency_across_independent_for_run_invocations()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(10), timeProvider);

        ConcurrentQueue<string> events = new();
        TestHttpHandler handler = new(request =>
        {
            events.Enqueue($"send:{request.RequestUri!.PathAndQuery}");
            if (request.RequestUri.PathAndQuery.Contains("rate-limit"))
            {
                HttpResponseMessage limitResponse = new(HttpStatusCode.TooManyRequests);
                limitResponse.Headers.TryAddWithoutValidation("Retry-After", "10");
                return limitResponse;
            }

            return Response(HttpStatusCode.OK, "OK");
        });

        using HttpMessageInvoker client = new(handler, true);
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Translation = new TranslationAppSettings(Concurrency: 2)
        };
        HostExtensionResources root = new(() => settings, new EmptyCredentialStore(), client, scheduler);

        IExtensionResources run1 = root.ForRun(new ExtensionConfigSnapshot(new Dictionary<string, string>(), 1));
        IExtensionResources run2 = root.ForRun(new ExtensionConfigSnapshot(new Dictionary<string, string>(), 2));

        Uri quotaUri = new("https://unit.test/quota/rate-limit");
        // Run 1 triggers 429
        using HttpResponseMessage res1 = await run1.Http.SendAsync(Request(quotaUri));
        res1.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        scheduler.IsInCooldown(quotaUri, out TimeSpan remaining).Should().BeTrue();
        remaining.Should().Be(TimeSpan.FromSeconds(10));

        // Run 2 immediately attempts to call the same quota endpoint. It must observe the active cooldown.
        Task<HttpResponseMessage> run2Task = Task.Run(() => run2.Http.SendAsync(Request(quotaUri)));

        // Give task opportunity to enter cooldown wait
        await Task.Delay(50);
        run2Task.IsCompleted.Should().BeFalse("Run 2 must wait for the active cooldown from Run 1");

        // Advance time by 10 seconds to expire cooldown
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        using HttpResponseMessage res2 = await run2Task;
        res2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        events.Should().Equal("send:/quota/rate-limit", "send:/quota/rate-limit");
    }

    [Fact]
    public async Task Different_quota_endpoints_are_isolated_and_cancelling_one_does_not_cancel_another()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(30), timeProvider);

        ConcurrentQueue<string> invoked = new();
        TestHttpHandler handler = new(request =>
        {
            invoked.Enqueue(request.RequestUri!.ToString());
            if (request.RequestUri.ToString().Contains("endpoint-a"))
            {
                HttpResponseMessage limitResponse = new(HttpStatusCode.TooManyRequests);
                limitResponse.Headers.TryAddWithoutValidation("Retry-After", "30");
                return limitResponse;
            }

            return Response(HttpStatusCode.OK, "OK-B");
        });

        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources root = new(() => LlmAppSettings.Default(), new EmptyCredentialStore(), client,
            scheduler);

        Uri uriA = new("https://unit.test/endpoint-a/query");
        Uri uriB = new("https://unit.test/service-b/query");

        // Request A hits 429
        using HttpResponseMessage respA = await root.Http.SendAsync(Request(uriA));
        respA.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        scheduler.IsInCooldown(uriA, out _).Should().BeTrue();
        scheduler.IsInCooldown(uriB, out _).Should().BeFalse();

        // Request to Endpoint B must succeed immediately without waiting
        using HttpResponseMessage respB = await root.Http.SendAsync(Request(uriB));
        respB.StatusCode.Should().Be(HttpStatusCode.OK);

        // Run waiting on A with cancellation token
        using CancellationTokenSource ctsA = new();
        CancellationToken tokenA = ctsA.Token;
        Task<HttpResponseMessage> waitingTaskA = Task.Run(() =>
            root.Http.SendAsync(Request(uriA), tokenA));

        // Another independent run on A with non-cancelled token
        Task<HttpResponseMessage> nonCancelledTaskA = Task.Run(() =>
            root.Http.SendAsync(Request(uriA)));

        await Task.Delay(50);
        ctsA.Cancel();

        // Cancelled task should fail promptly
        Func<Task> actA = async () => await waitingTaskA;
        await actA.Should().ThrowAsync<OperationCanceledException>();

        // Non-cancelled task must still be waiting, and Endpoint A must still be in cooldown
        nonCancelledTaskA.IsCompleted.Should().BeFalse();
        scheduler.IsInCooldown(uriA, out _).Should().BeTrue();

        // When cooldown expires, non-cancelled task completes
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        using HttpResponseMessage resNonCancelled = await nonCancelledTaskA;
        resNonCancelled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Prompt_cancellation_during_active_backoff_emits_no_network_request()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(60), timeProvider);

        int networkCallCount = 0;
        TestHttpHandler handler = new(request =>
        {
            Interlocked.Increment(ref networkCallCount);
            HttpResponseMessage limit = new(HttpStatusCode.TooManyRequests);
            limit.Headers.TryAddWithoutValidation("Retry-After", "60");
            return limit;
        });

        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources capabilities =
            new(() => LlmAppSettings.Default(), new EmptyCredentialStore(), client, scheduler);

        Uri uri = new("https://unit.test/api/backoff");
        // First call puts endpoint into 60s cooldown
        using HttpResponseMessage initial = await capabilities.Http.SendAsync(Request(uri));
        initial.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        networkCallCount.Should().Be(1);

        // Second call with cancellation token
        using CancellationTokenSource cts = new();
        CancellationToken cancellationToken = cts.Token;
        Task<HttpResponseMessage> pending = Task.Run(() =>
            capabilities.Http.SendAsync(Request(uri), cancellationToken));

        await Task.Delay(50);
        cts.Cancel();

        Func<Task> act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // No second network request should ever have been dispatched
        networkCallCount.Should().Be(1, "cancelling during backoff must not dispatch any network request");
    }

    [Theory]
    [InlineData(503)]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Distinct_errors_are_not_confused_with_429_cooldowns(int errorStatus)
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(30), timeProvider);

        int calls = 0;
        TestHttpHandler handler = new(request =>
        {
            Interlocked.Increment(ref calls);
            return Response((HttpStatusCode)errorStatus, "Error response");
        });

        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources capabilities =
            new(() => LlmAppSettings.Default(), new EmptyCredentialStore(), client, scheduler);

        Uri uri = new("https://unit.test/api/status-test");
        using HttpResponseMessage res1 = await capabilities.Http.SendAsync(Request(uri));
        ((int)res1.StatusCode).Should().Be(errorStatus);

        scheduler.IsInCooldown(uri, out _).Should().BeFalse(
            $"HTTP {errorStatus} is not a 429 rate limit and must not set a cooldown");
        scheduler.GetConsecutiveRateLimits(uri).Should().Be(0);

        // Immediate subsequent request proceeds without any cooldown delay
        using HttpResponseMessage res2 = await capabilities.Http.SendAsync(Request(uri));
        ((int)res2.StatusCode).Should().Be(errorStatus);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Metadata_without_custom_policy_does_not_inherit_translation_concurrency_settings()
    {
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Translation = new TranslationAppSettings(Concurrency: 1, RequestIntervalSeconds: 10)
        };
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(timeProvider: clock);
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRequests = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        TestHttpHandler handler = new(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                bothEntered.TrySetResult();
            }

            await releaseRequests.Task.WaitAsync(token);
            return Response(HttpStatusCode.OK, "metadata");
        });
        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources root = new(() => settings, new EmptyCredentialStore(), client, scheduler);

        ExtensionConfigSnapshot metadataConfig = new(new Dictionary<string, string>(), 1);
        IExtensionResources metadata = root.ForDomain("metadata").ForRun(metadataConfig);
        Uri uri = new("https://metadata.unit.test/lookup");
        Task<HttpResponseMessage> first = metadata.Http.SendAsync(Request(uri));
        Task<HttpResponseMessage> second = metadata.Http.SendAsync(Request(uri));
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            calls.Should().Be(2, "metadata uses its default concurrency, not translation's limit of one");
        }
        finally
        {
            releaseRequests.TrySetResult();
        }

        using HttpResponseMessage firstResponse = await first;
        using HttpResponseMessage secondResponse = await second;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await firstResponse.Content.ReadAsStringAsync()).Should().Be("metadata");
        (await secondResponse.Content.ReadAsStringAsync()).Should().Be("metadata");
        clock.GetUtcNow().Should().Be(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero),
            "metadata dispatch must not wait for translation's ten-second interval");
    }

    [Fact]
    public async Task Scheduler_handles_runs_with_different_concurrency_without_over_releasing_slots()
    {
        ExtensionHttpScheduler scheduler = new();
        Uri uri = new("https://unit.test/concurrency");
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource queuedEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        Task<HttpStatusCode> Run(int limit, TaskCompletionSource entered, Task release)
        {
            return SendAndDisposeAsync(scheduler.ExecuteAsync(
                Request(uri), limit, null,
                async token =>
                {
                    int now = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximum, now);
                    entered.TrySetResult();
                    try
                    {
                        await release.WaitAsync(token);
                        return Response((HttpStatusCode)200, "ok");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }, deadline.Token));
        }

        Task<HttpStatusCode> highConcurrency = Run(32, firstEntered, releaseFirst.Task);
        Task<HttpStatusCode> lowerConcurrency = Run(2, secondEntered, releaseSecond.Task);
        Task<HttpStatusCode>? queued = null;
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(deadline.Token);
            maximum.Should().Be(2, "the concurrent policies allow the shared minimum of two slots");

            queued = Run(32, queuedEntered, releaseQueued.Task);
            await Task.Yield();
            queuedEntered.Task.IsCompleted.Should().BeFalse("a queued policy must not over-release occupied slots");

            releaseFirst.TrySetResult();
            await queuedEntered.Task.WaitAsync(deadline.Token);
            maximum.Should().BeLessThanOrEqualTo(2);

            using CancellationTokenSource cancellation = new();
            Task<HttpResponseMessage> cancelled = scheduler.ExecuteAsync(
                Request(uri), 2, null,
                _ => Task.FromResult(Response((HttpStatusCode)200, "unexpected")),
                cancellation.Token);
            cancellation.Cancel();
            await cancelled.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            releaseQueued.TrySetResult();
            await Task.WhenAll(highConcurrency, lowerConcurrency,
                    queued ?? Task.FromResult(HttpStatusCode.OK))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        (await Task.WhenAll(highConcurrency, lowerConcurrency,
                queued ?? Task.FromResult(HttpStatusCode.OK)))
            .Should().OnlyContain(status => status == HttpStatusCode.OK);
        active.Should().Be(0, "all entered sends must drain after their gates are released");
        maximum.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Low_concurrency_run_after_high_concurrency_warmup_is_limited_to_one()
    {
        ExtensionHttpScheduler scheduler = new();
        Uri uri = new("https://unit.test/warmup");
        int active = 0;
        int maximum = 0;

        Task<HttpStatusCode> Run()
        {
            return SendAndDisposeAsync(scheduler.ExecuteAsync(
                Request(uri), 1, null,
                async _ =>
                {
                    int now = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximum, now);
                    await Task.Delay(10);
                    Interlocked.Decrement(ref active);
                    return Response((HttpStatusCode)200, "ok");
                }));
        }

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => SendAndDisposeAsync(scheduler.ExecuteAsync(
            Request(uri), 32, null,
            _ => Task.FromResult(Response((HttpStatusCode)200, "warm"))))));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Run()));
        maximum.Should().Be(1);
    }

    [Fact]
    public async Task Stable_policy_keeps_same_endpoint_requests_from_two_runs_serial()
    {
        ExtensionHttpScheduler scheduler = new();
        Uri uri = new("https://unit.test/stable-translation");
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> dispatched = new();

        Task<HttpStatusCode> Run(string runId, bool block)
        {
            return SendAndDisposeAsync(scheduler.ExecuteAsync(Request(uri), 1, null,
                async token =>
                {
                    dispatched.Enqueue(runId);
                    if (block)
                    {
                        firstEntered.TrySetResult();
                        await releaseFirst.Task.WaitAsync(token);
                    }

                    return Response((HttpStatusCode)200, "ok");
                }));
        }

        Task<HttpStatusCode> first = Run("run-1", true);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<HttpStatusCode> second = Run("run-2", false);
        try
        {
            await Task.Yield();
            dispatched.Should().Equal(new[] { "run-1" }, "the second run must wait at the shared endpoint gate");
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        (await first).Should().Be(HttpStatusCode.OK);
        (await second).Should().Be(HttpStatusCode.OK);
        dispatched.Should().Equal("run-1", "run-2");
    }

    [Fact]
    public async Task Queued_requests_observe_rate_limit_budget_and_cooldown_allows_one_recovery_probe()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(5), clock, 1);
        Uri uri = new("https://unit.test/probe");
        TaskCompletionSource<HttpResponseMessage> firstResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        async Task<HttpResponseMessage> Probe(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            probeEntered.TrySetResult();
            await releaseProbe.Task.WaitAsync(token);
            return Response((HttpStatusCode)200, "recovered");
        }

        Task<HttpResponseMessage> first = scheduler.ExecuteAsync(Request(uri),
            1, null, _ => firstResponse.Task);
        Task<HttpResponseMessage> queuedProbe = scheduler.ExecuteAsync(
            Request(uri), 2, null, Probe);
        Task<HttpResponseMessage> queuedOther = scheduler.ExecuteAsync(
            Request(uri), 2, null, Probe);

        firstResponse.SetResult(Response((HttpStatusCode)429, "limited", ("Retry-After", "5")));
        using (HttpResponseMessage firstResult = await first)
        {
            firstResult.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        await Task.Delay(50);
        clock.Advance(TimeSpan.FromSeconds(5));
        Task<HttpResponseMessage> firstCompleted;
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstCompleted = await Task.WhenAny(queuedProbe, queuedOther).WaitAsync(TimeSpan.FromSeconds(2));
            using HttpResponseMessage rejected = await firstCompleted;
            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            releaseProbe.TrySetResult();
        }

        Task<HttpResponseMessage> recovered = firstCompleted == queuedProbe ? queuedOther : queuedProbe;
        using (HttpResponseMessage recoveredResponse = await recovered)
        {
            recoveredResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        calls.Should().Be(1, "only one request may probe recovery after the cooldown");

        using HttpResponseMessage finalResponse = await scheduler.ExecuteAsync(Request(uri), 1, null,
            _ => Task.FromResult(Response((HttpStatusCode)200, "ok")));
        finalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Configured_quota_endpoint_shares_resources_but_isolates_other_origins()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(8), clock);
        Uri configured = new("https://api.example.test/works");
        Uri firstResource = new("https://api.example.test/works/10.1000%2Fone");
        Uri secondResource = new("https://api.example.test/works/10.1000%2Ftwo");
        Uri otherOrigin = new("https://other.example.test/works/10.1000%2Ftwo");
        int secondCalls = 0;
        Task<HttpResponseMessage> rateLimited = scheduler.ExecuteAsync(
            Request(firstResource), 2, null,
            _ => Task.FromResult(Response(HttpStatusCode.TooManyRequests, "limited", ("Retry-After", "8"))),
            quotaEndpoint: configured);
        using (HttpResponseMessage rateLimitedResponse = await rateLimited)
        {
            rateLimitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        Task<HttpResponseMessage> shared = scheduler.ExecuteAsync(
            Request(secondResource), 2, null,
            _ =>
            {
                Interlocked.Increment(ref secondCalls);
                return Task.FromResult(Response((HttpStatusCode)200, "ok"));
            }, quotaEndpoint: configured);
        Task<HttpResponseMessage> isolated = scheduler.ExecuteAsync(
            Request(otherOrigin), 2, null,
            _ => Task.FromResult(Response((HttpStatusCode)200, "isolated")),
            quotaEndpoint: otherOrigin);

        using (HttpResponseMessage isolatedResponse = await isolated)
        {
            isolatedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await Task.Delay(50);
        secondCalls.Should().Be(0, "different resource paths under one configured endpoint share quota cooldown");
        clock.Advance(TimeSpan.FromSeconds(8));
        using (HttpResponseMessage sharedResponse = await shared)
        {
            sharedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        secondCalls.Should().Be(1);
    }

    [Fact]
    public async Task Request_with_zero_interval_cannot_bypass_existing_endpoint_pacing()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(timeProvider: clock);
        Uri uri = new("https://unit.test/pacing");
        int calls = 0;
        await SendAndDisposeAsync(scheduler.ExecuteAsync(Request(uri), 2,
            TimeSpan.FromSeconds(10), _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response((HttpStatusCode)200, "paced"));
            }));

        Task<HttpResponseMessage> unpacedRun = scheduler.ExecuteAsync(
            Request(uri), 2, TimeSpan.Zero, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response((HttpStatusCode)200, "still paced"));
            });
        await Task.Delay(50);
        calls.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        using HttpResponseMessage unpacedResponse = await unpacedRun;
        unpacedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Metadata_http_uses_credential_reference_as_a_quota_partition()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(10), clock);
        TaskCompletionSource secondRequestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        TestHttpHandler handler = new(request =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Response(HttpStatusCode.TooManyRequests, "limited", ("Retry-After", "10"));
            }

            secondRequestEntered.TrySetResult();
            return Response(HttpStatusCode.OK, "ok");
        });
        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources root = new(() => LlmAppSettings.Default(), new EmptyCredentialStore(), client,
            scheduler);
        ExtensionConfigSnapshot firstConfig = new(new Dictionary<string, string>
        {
            ["credential-reference"] = "provider-a"
        }, 1);
        ExtensionConfigSnapshot secondConfig = new(new Dictionary<string, string>
        {
            ["credential-reference"] = "provider-b"
        }, 2);
        HttpClient firstHttp = root.ForDomain("metadata").ForRun(firstConfig).Http;
        HttpClient secondHttp = root.ForDomain("metadata").ForRun(secondConfig).Http;
        Uri uri = new("https://unit.test/metadata");

        using HttpResponseMessage limited = await firstHttp.SendAsync(Request(uri));
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        Task<HttpResponseMessage> otherCredential = secondHttp.SendAsync(Request(uri));
        try
        {
            await secondRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        using HttpResponseMessage independentResponse = await otherCredential;
        independentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2, "different credential references use isolated quota partitions");
    }

    [Fact]
    public async Task Host_http_wrappers_preserve_content_headers_and_body()
    {
        int sends = 0;
        TestHttpHandler handler = new(_ =>
        {
            Interlocked.Increment(ref sends);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("wrapped response", System.Text.Encoding.UTF8, "application/json")
            };
            response.Content.Headers.TryAddWithoutValidation("X-Content-Marker", "preserved");
            return response;
        });
        using HttpMessageInvoker client = new(handler, true);
        HostExtensionResources resources = new(() => LlmAppSettings.Default(), new EmptyCredentialStore(), client);

        using HttpResponseMessage response = await resources.Http.SendAsync(Request(new Uri("https://unit.test/body")));

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.TryGetValues("X-Content-Marker", out IEnumerable<string>? values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("preserved");
        (await response.Content.ReadAsStringAsync()).Should().Be("wrapped response");
        sends.Should().Be(1, "the shared transport invoker must dispatch an HttpClient-sent request only once");
    }

    [Fact]
    public async Task Scheduler_concurrency_slot_is_held_until_response_content_is_disposed()
    {
        ExtensionHttpScheduler scheduler = new();
        Uri uri = new("https://unit.test/response-lifetime");
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> first = scheduler.ExecuteAsync(Request(uri), 1, null,
            _ => Task.FromResult(Response(HttpStatusCode.OK, "first body")));
        using HttpResponseMessage firstResponse = await first;
        Task<HttpResponseMessage> second = scheduler.ExecuteAsync(Request(uri), 1, null, _ =>
        {
            secondEntered.TrySetResult();
            return Task.FromResult(Response(HttpStatusCode.OK, "second body"));
        });

        try
        {
            await Task.Yield();
            secondEntered.Task.IsCompleted.Should().BeFalse(
                "the first response still owns the concurrency slot until its body is consumed or disposed");
        }
        finally
        {
            firstResponse.Dispose();
        }

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using HttpResponseMessage secondResponse = await second;
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await secondResponse.Content.ReadAsStringAsync()).Should().Be("second body");
    }

    [Fact]
    public async Task Queued_request_rechecks_cooldown_after_another_request_returns_429()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(10), clock);
        Uri uri = new("https://unit.test/queued");
        TaskCompletionSource<HttpResponseMessage> firstResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<HttpResponseMessage> first = scheduler.ExecuteAsync(Request(uri),
            1, null, _ => firstResponse.Task);
        Task<HttpResponseMessage> queued = scheduler.ExecuteAsync(
            Request(uri),
            1, null, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response((HttpStatusCode)200, "ok"));
            });

        firstResponse.SetResult(Response((HttpStatusCode)429, "limited", ("Retry-After", "10")));
        using (HttpResponseMessage limitedResponse = await first)
        {
            limitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        await Task.Delay(50);
        calls.Should().Be(0);
        clock.Advance(TimeSpan.FromSeconds(10));
        using HttpResponseMessage queuedResponse = await queued;
        queuedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(1);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        return new HttpRequestMessage(HttpMethod.Get, uri);
    }

    private static async Task<HttpStatusCode> SendAndDisposeAsync(Task<HttpResponseMessage> pending)
    {
        using HttpResponseMessage response = await pending;
        await response.Content.ReadAsByteArrayAsync();
        return response.StatusCode;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null,
        (string Name, string Value)? header = null)
    {
        HttpResponseMessage response = new(status);
        if (body is not null)
        {
            response.Content = new StringContent(body);
        }

        if (header is { } value)
        {
            response.Headers.TryAddWithoutValidation(value.Name, value.Value);
        }

        return response;
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref maximum)) &&
               Interlocked.CompareExchange(ref maximum, value, observed) != observed)
        {
        }
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public TestHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            _send = (request, _) => Task.FromResult(send(request));
        }

        public TestHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            _send = (request, _) => send(request);
        }

        public TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _send(request, cancellationToken);
        }
    }
}
