using FluentAssertions;
using Patchouli.Core.Layout;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class PdfiumSessionCacheTests
{
    [Fact]
    public async Task GetOrOpen_reuses_the_same_session_for_one_path()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 4, long.MaxValue);

        IPdfPageSession first = await cache.GetOrOpenAsync("a.pdf");
        IPdfPageSession second = await cache.GetOrOpenAsync("a.pdf");

        first.Should().BeSameAs(second);
        renderer.OpenCount.Should().Be(1);
        cache.Count.Should().Be(1);
        cache.Hits.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_getters_for_the_same_path_share_one_open()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 4, long.MaxValue);

        // ReSharper disable once AccessToDisposedClosure -- the concurrent getters are awaited
        // before the `await using` scope ends, so the cache is never accessed after disposal.
        IPdfPageSession[] sessions = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => cache.GetOrOpenAsync("a.pdf")));

        sessions.Should().OnlyContain(session => session == sessions[0]);
        renderer.OpenCount.Should().Be(1);
    }

    [Fact]
    public async Task Count_budget_evicts_oldest_idle_session_and_disposes_it()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 2, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        cache.Return(a);
        await cache.GetOrOpenAsync("b.pdf");
        await cache.GetOrOpenAsync("c.pdf");

        cache.Count.Should().Be(2);
        cache.Evictions.Should().Be(1);
        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("a.pdf"));
        renderer.OpenCount.Should().Be(3);
    }

    [Fact]
    public async Task Byte_budget_evicts_when_estimated_bytes_exceed_the_limit()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pfc-{Guid.NewGuid():N}")).FullName;
        try
        {
            string aPath = Path.Combine(dir, "a.pdf");
            string bPath = Path.Combine(dir, "b.pdf");
            await File.WriteAllBytesAsync(aPath, new byte[100]);
            await File.WriteAllBytesAsync(bPath, new byte[100]);

            FakeRenderer renderer = new();
            await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 100, 220);

            IPdfPageSession a = await cache.GetOrOpenAsync(aPath);
            cache.Return(a);
            await cache.GetOrOpenAsync(bPath);

            cache.Evictions.Should().Be(1);
            renderer.DisposedPaths.Should().Contain(Path.GetFullPath(aPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task In_use_session_is_never_evicted()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 1, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        // a is still in use; opening b must not evict it, so the budget stays exceeded.
        IPdfPageSession b = await cache.GetOrOpenAsync("b.pdf");

        cache.Count.Should().Be(2);
        cache.Evictions.Should().Be(0);
        renderer.DisposedPaths.Should().BeEmpty();
        cache.Return(a);
        cache.Return(b);
    }

    [Fact]
    public async Task Budget_eviction_skips_an_in_use_lru_entry_and_evicts_the_next_idle_entry()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 1, long.MaxValue);

        await cache.GetOrOpenAsync("a.pdf");
        IPdfPageSession b = await cache.GetOrOpenAsync("b.pdf");
        cache.Return(b);
        await cache.GetOrOpenAsync("c.pdf");

        cache.Count.Should().Be(2, "a remains in use while the idle b is the eviction candidate");
        cache.Evictions.Should().Be(1);
        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("b.pdf"));
    }

    [Fact]
    public async Task EvictPath_defers_an_in_use_session_until_the_render_returns()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 4, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        await cache.EvictPathAsync("a.pdf");

        cache.Count.Should().Be(1);
        renderer.DisposedPaths.Should().BeEmpty();

        cache.Return(a);
        await Task.Yield();
        cache.Count.Should().Be(0);
        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("a.pdf"));
    }

    [Fact]
    public async Task EvictPath_disposes_the_session_and_removes_it()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 4, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        cache.Return(a);
        await cache.EvictPathAsync("a.pdf");

        cache.Count.Should().Be(0);
        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("a.pdf"));
    }

    [Fact]
    public async Task DisposeAsync_closes_every_open_session_deterministically()
    {
        FakeRenderer renderer = new();
        PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 4, long.MaxValue);
        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        await cache.GetOrOpenAsync("b.pdf");

        await cache.DisposeAsync();
        await cache.DisposeAsync();

        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("a.pdf"))
            .And.Contain(Path.GetFullPath("b.pdf"));
    }

    [Fact]
    public async Task Evicted_session_that_was_returned_is_disposed_and_reopened_on_next_access()
    {
        FakeRenderer renderer = new();
        await using PdfiumSessionCache cache = new(renderer.OpenSessionAsync, 1, long.MaxValue);

        IPdfPageSession a = await cache.GetOrOpenAsync("a.pdf");
        cache.Return(a);
        await cache.GetOrOpenAsync("b.pdf");
        IPdfPageSession reopened = await cache.GetOrOpenAsync("a.pdf");

        reopened.Should().NotBeSameAs(a);
        renderer.OpenCount.Should().Be(3);
        renderer.DisposedPaths.Should().Contain(Path.GetFullPath("a.pdf"));
    }

    private sealed class FakeRenderer
    {
        public int OpenCount { get; private set; }
        public List<string> DisposedPaths { get; } = [];

        public async Task<IPdfPageSession> OpenSessionAsync(string pdfPath,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            OpenCount++;
            return new FakeSession(this, Path.GetFullPath(pdfPath));
        }

        private sealed class FakeSession : IPdfPageSession
        {
            private readonly FakeRenderer _owner;
            private bool _disposed;

            public FakeSession(FakeRenderer owner, string path)
            {
                _owner = owner;
                Path = path;
            }

            public string Path { get; }

            public int PageCount => 3;

            public Task<PdfPagePixelBufferOutput> RenderPageAsync(int pageIndex, int dpi,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new PdfPagePixelBufferOutput(new byte[16], 2, 2, 8, 0,
                    CoordinateBasis.NormalizedPage, 2, 2, "fake-session-v1"));
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _owner.DisposedPaths.Add(Path);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
