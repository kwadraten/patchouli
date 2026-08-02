using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

public sealed class SharedReadCacheTests
{
    private sealed record TestValue(string Payload);

    [Fact]
    public async Task Evicts_least_recently_used_entry_when_byte_limit_exceeded()
    {
        SharedReadCache<int, TestValue> cache = new(100, value => value.Payload.Length);
        int generations = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            return cache.GetOrAddAsync(key, _ =>
            {
                Interlocked.Increment(ref generations);
                return Task.FromResult(Result<TestValue>.Success(new TestValue(new string('x', 50))));
            }, CancellationToken.None);
        }

        await Produce(1);
        await Produce(2);
        await Produce(1);
        await Produce(3);

        generations.Should().Be(3, "key 1 is reused, key 2 is evicted to fit key 3");
        cache.Metrics.Evictions.Should().Be(1);
        cache.Metrics.CachedEntries.Should().Be(2);
        cache.Metrics.CachedBytes.Should().Be(100);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_key_share_one_generation()
    {
        SharedReadCache<int, TestValue> cache = new(1024);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int generations = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            return cache.GetOrAddAsync(key, async _ =>
            {
                Interlocked.Increment(ref generations);
                await release.Task;
                return Result<TestValue>.Success(new TestValue("shared"));
            }, CancellationToken.None);
        }

        Task<Result<TestValue>>[] requests = Enumerable.Range(0, 8).Select(_ => Produce(42)).ToArray();
        await Task.Delay(100);
        generations.Should().Be(1);
        release.SetResult();
        Result<TestValue>[] results = await Task.WhenAll(requests);
        results.Should().OnlyContain(result => result.IsSuccess && result.Value.Payload == "shared");
        generations.Should().Be(1);
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_shared_generation()
    {
        SharedReadCache<int, TestValue> cache = new(1024);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int generations = 0;

        Task<Result<TestValue>> Produce(int key, CancellationToken token = default)
        {
            return cache.GetOrAddAsync(key, async _ =>
            {
                Interlocked.Increment(ref generations);
                await release.Task;
                return Result<TestValue>.Success(new TestValue("shared"));
            }, token);
        }

        using CancellationTokenSource canceledWaiter = new();
        Task<Result<TestValue>> canceled = Produce(1, canceledWaiter.Token);
        Task<Result<TestValue>> surviving = Produce(1);
        await Task.Delay(100);
        canceledWaiter.Cancel();

        Func<Task> expectCancellation = async () => await canceled;
        await expectCancellation.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        (await surviving).Value.Payload.Should().Be("shared");
        generations.Should().Be(1, "only the first waiter's wait is canceled, not the shared work");
    }

    [Fact]
    public async Task Failed_generation_is_not_cached()
    {
        SharedReadCache<int, TestValue> cache = new(1024);
        int attempts = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            int attempt = Interlocked.Increment(ref attempts);
            return cache.GetOrAddAsync(key, _ => Task.FromResult(attempt == 1
                ? Result<TestValue>.Failure("boom", "first attempt failed")
                : Result<TestValue>.Success(new TestValue("recovered"))), CancellationToken.None);
        }

        (await Produce(1)).IsFailure.Should().BeTrue();
        (await Produce(1)).Value.Payload.Should().Be("recovered");
        attempts.Should().Be(2);
        cache.Metrics.Failed.Should().Be(1);
        cache.Metrics.CachedEntries.Should().Be(1);
    }

    [Fact]
    public async Task Factory_exception_is_not_cached_and_retry_succeeds()
    {
        SharedReadCache<int, TestValue> cache = new(1024);
        int attempts = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            int attempt = Interlocked.Increment(ref attempts);
            return cache.GetOrAddAsync(key, _ => attempt == 1
                ? Task.FromException<Result<TestValue>>(new InvalidOperationException("boom"))
                : Task.FromResult(Result<TestValue>.Success(new TestValue("recovered"))), CancellationToken.None);
        }

        InvalidOperationException? thrown = null;
        try
        {
            await Produce(1);
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        thrown.Should().NotBeNull();
        (await Produce(1)).Value.Payload.Should().Be("recovered");
        attempts.Should().Be(2, "a throwing generation is removed so callers can retry");
        cache.Metrics.Failed.Should().Be(1);
        cache.Metrics.CachedEntries.Should().Be(1);
    }

    [Fact]
    public async Task Oversized_value_is_not_cached()
    {
        SharedReadCache<int, TestValue> cache = new(10, value => value.Payload.Length);
        int generations = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            Interlocked.Increment(ref generations);
            return cache.GetOrAddAsync(key, _ =>
                    Task.FromResult(Result<TestValue>.Success(new TestValue(new string('y', 100)))),
                CancellationToken.None);
        }

        (await Produce(1)).Value.Payload.Length.Should().Be(100);
        (await Produce(1)).Value.Payload.Length.Should().Be(100);
        generations.Should().Be(2, "an entry larger than the budget is never cached");
        cache.Metrics.CachedEntries.Should().Be(0);
    }

    [Fact]
    public async Task Metrics_track_hits_misses_inserts_failures_and_evictions()
    {
        SharedReadCache<int, TestValue> cache = new(10, value => value.Payload.Length);

        Task<Result<TestValue>> Produce(int key, bool fail = false)
        {
            return cache.GetOrAddAsync(key, _ => Task.FromResult(fail
                ? Result<TestValue>.Failure("boom", "failed")
                : Result<TestValue>.Success(new TestValue("123456"))), CancellationToken.None);
        }

        await Produce(1);
        await Produce(1);
        await Produce(2);
        await Produce(3);
        await Produce(2, true);

        ReadCacheMetrics metrics = cache.Metrics;
        metrics.Hits.Should().Be(1);
        metrics.Misses.Should().Be(4);
        metrics.Inserted.Should().Be(3);
        metrics.Failed.Should().Be(1);
        metrics.Evictions.Should().Be(2);
        metrics.CachedEntries.Should().Be(1);
        metrics.CachedBytes.Should().Be(6);
    }

    [Fact]
    public async Task Invalidate_removes_only_the_keyed_entry()
    {
        SharedReadCache<int, TestValue> cache = new(1024);
        int generations = 0;

        Task<Result<TestValue>> Produce(int key)
        {
            return cache.GetOrAddAsync(key, _ =>
            {
                Interlocked.Increment(ref generations);
                return Task.FromResult(Result<TestValue>.Success(new TestValue($"value-{key}")));
            }, CancellationToken.None);
        }

        await Produce(1);
        await Produce(2);
        cache.Invalidate(1);
        (await Produce(1)).Value.Payload.Should().Be("value-1");
        (await Produce(2)).Value.Payload.Should().Be("value-2");
        generations.Should().Be(3, "key 1 is revalidated after invalidation, key 2 is still cached");
    }
}
