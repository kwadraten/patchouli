using FluentAssertions;
using Patchouli.Core.Layout;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Issue #31, plan test items 1–3 (and item 12's fake-injection technique): the
/// register-before-start single-flight of <see cref="PdfiumSessionCache"/>.
/// A gated fake open factory (TaskCompletionSource barriers) makes the in-flight
/// window exact: concurrent callers share one open, a synchronously throwing
/// factory leaves no pending entry behind, and one waiter's cancellation never
/// disturbs the shared open or the other waiters.
/// </summary>
public sealed class PdfiumSessionCacheIssue31Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static string NewDocPath() =>
        Path.Combine(Path.GetTempPath(), $"issue31-{Guid.NewGuid():N}", "doc.pdf");

    [Fact]
    public async Task Twenty_concurrent_callers_share_exactly_one_open()
    {
        FakeFactory factory = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        string path = NewDocPath();
        factory.Handler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return new FakeSession(path);
        };
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 8, long.MaxValue);

        using ManualResetEventSlim start = new(false);
        Task<IPdfPageSession>[] callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await cache.GetOrOpenAsync(path);
            }))
            .ToArray();
        start.Set();

        await entered.Task.WaitAsync(Timeout);
        callers.Should().OnlyContain(task => !task.IsCompleted,
            "while the single shared open is blocked, every caller is still waiting on it");

        release.TrySetResult();
        IPdfPageSession[] sessions = await Task.WhenAll(callers).WaitAsync(Timeout);

        factory.CallCount.Should().Be(1, "twenty concurrent callers coalesce into one actual open");
        sessions.Should().OnlyContain(session => ReferenceEquals(session, sessions[0]));
        cache.Opens.Should().Be(1);
        cache.Count.Should().Be(1);
        foreach (IPdfPageSession session in sessions)
        {
            cache.Return(session);
        }
    }

    [Fact]
    public async Task Synchronously_throwing_factory_leaves_no_pending_entry_and_next_acquire_reopens()
    {
        FakeFactory factory = new();
        factory.Handler = _ => throw new InvalidOperationException("sync-open-failure");
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 8, long.MaxValue);
        string path = NewDocPath();

        Func<Task> first = () => cache.GetOrOpenAsync(path);
        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("sync-open-failure");
        factory.CallCount.Should().Be(1);
        cache.Count.Should().Be(0);

        factory.Handler = _ => Task.FromResult<IPdfPageSession>(new FakeSession(path));
        IPdfPageSession session = await cache.GetOrOpenAsync(path).WaitAsync(Timeout);

        session.Should().NotBeNull();
        factory.CallCount.Should().Be(2,
            "the failed open was not left registered: the next acquire starts a fresh open");
        cache.Count.Should().Be(1);
        cache.Return(session);
    }

    [Fact]
    public async Task Shared_open_failure_reaches_every_waiter_once_and_does_not_poison_the_cache()
    {
        FakeFactory factory = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        factory.Handler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("async-open-failure");
        };
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 8, long.MaxValue);
        string path = NewDocPath();

        Task<IPdfPageSession>[] waiters = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrOpenAsync(path))
            .ToArray();
        await entered.Task.WaitAsync(Timeout);
        waiters.Should().OnlyContain(task => !task.IsCompleted);

        release.TrySetResult();
        foreach (Task<IPdfPageSession> waiter in waiters)
        {
            Func<Task> act = () => waiter;
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("async-open-failure");
        }

        factory.CallCount.Should().Be(1, "all five waiters shared the same failing open");
        cache.Count.Should().Be(0);

        factory.Handler = _ => Task.FromResult<IPdfPageSession>(new FakeSession(path));
        IPdfPageSession session = await cache.GetOrOpenAsync(path).WaitAsync(Timeout);
        session.Should().NotBeNull();
        factory.CallCount.Should().Be(2);
        cache.Return(session);
    }

    [Fact]
    public async Task One_waiter_cancelling_neither_cancels_the_shared_open_nor_the_other_waiter()
    {
        FakeFactory factory = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        string path = NewDocPath();
        FakeSession opened = new(path);
        factory.Handler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return opened;
        };
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 8, long.MaxValue);

        using CancellationTokenSource cancelling = new();
        Task<IPdfPageSession> impatient = cache.GetOrOpenAsync(path, cancelling.Token);
        Task<IPdfPageSession> patient = cache.GetOrOpenAsync(path);
        await entered.Task.WaitAsync(Timeout);

        cancelling.Cancel();
        Func<Task> impatientAct = () => impatient;
        await impatientAct.Should().ThrowAsync<OperationCanceledException>();
        patient.IsCompleted.Should().BeFalse("the shared open is still running for the patient waiter");
        factory.CallCount.Should().Be(1);

        release.TrySetResult();
        IPdfPageSession session = await patient.WaitAsync(Timeout);
        session.Should().BeSameAs(opened);
        opened.DisposeCount.Should().Be(0, "a session claimed by a remaining waiter is not closed");
        cache.Count.Should().Be(1);
        cache.Return(session);
    }

    [Fact]
    public async Task Open_completing_after_every_waiter_left_is_closed_instead_of_leaked()
    {
        FakeFactory factory = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        string path = NewDocPath();
        FakeSession opened = new(path);
        factory.Handler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return opened;
        };
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 8, long.MaxValue);

        using CancellationTokenSource cancelling = new();
        Task<IPdfPageSession> only = cache.GetOrOpenAsync(path, cancelling.Token);
        await entered.Task.WaitAsync(Timeout);

        cancelling.Cancel();
        Func<Task> act = () => only;
        await act.Should().ThrowAsync<OperationCanceledException>();

        release.TrySetResult();
        await opened.DisposedSignal.Task.WaitAsync(Timeout);
        opened.DisposeCount.Should().Be(1,
            "a finished open nobody claimed is closed deterministically, not cached or leaked");
        cache.Count.Should().Be(0);

        factory.Handler = _ => Task.FromResult<IPdfPageSession>(new FakeSession(path));
        IPdfPageSession reopened = await cache.GetOrOpenAsync(path).WaitAsync(Timeout);
        reopened.Should().NotBeSameAs(opened);
        factory.CallCount.Should().Be(2);
        cache.Return(reopened);
    }

    [Fact]
    public async Task Evicted_session_failing_to_dispose_neither_faults_the_acquire_nor_leaks_its_lease()
    {
        FakeFactory factory = new();
        string pathA = NewDocPath();
        string pathB = NewDocPath();
        FakeSession sessionA = new(pathA) { ThrowOnDispose = true };
        FakeSession sessionB = new(pathB);
        factory.Handler = path => Task.FromResult<IPdfPageSession>(
            string.Equals(path, pathA, StringComparison.Ordinal) ? sessionA : sessionB);
        await using PdfiumSessionCache cache = new(factory.OpenAsync, 1, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync(pathA).WaitAsync(Timeout);
        cache.Return(a);
        cache.InUseCount.Should().Be(0);

        // Acquiring B evicts A (capacity 1); draining A's disposal throws. The drain
        // failure must be converged inside the cache: B's acquire still succeeds with
        // a balanced lease instead of faulting with A's dispose exception and
        // stranding B's in-use count forever.
        IPdfPageSession b = await cache.GetOrOpenAsync(pathB).WaitAsync(Timeout);
        b.Should().BeSameAs(sessionB);
        sessionA.DisposeCount.Should().Be(1, "closing the evicted session was still attempted");
        cache.Count.Should().Be(1);
        cache.InUseCount.Should().Be(1);

        cache.Return(b);
        cache.InUseCount.Should().Be(0, "the lease acquired across the failing drain returns normally");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeFactory
    {
        private int _callCount;

        public Func<string, Task<IPdfPageSession>> Handler { get; set; } =
            _ => throw new InvalidOperationException("No handler configured.");

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IPdfPageSession> OpenAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            // Deliberately not async: a handler that throws does so synchronously, which
            // is exactly the registration-order hazard of plan item 2 / 12.
            return Handler(path);
        }
    }

    private sealed class FakeSession(string path) : IPdfPageSession
    {
        private int _disposeCount;

        public string Path { get; } = path;

        public int PageCount => 3;

        public bool ThrowOnDispose { get; init; }

        public TaskCompletionSource DisposedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<PdfPagePixelBufferOutput> RenderPageAsync(int pageIndex, int dpi,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PdfPagePixelBufferOutput(new byte[16], 2, 2, 8, 0,
                CoordinateBasis.NormalizedPage, 2, 2, "fake-session-issue31"));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposedSignal.TrySetResult();
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("dispose-failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}
