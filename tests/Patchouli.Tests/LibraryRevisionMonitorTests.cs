using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.Caching;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using System.Data.Common;

namespace Patchouli.Tests;

public sealed class LibraryRevisionMonitorTests
{
    [Fact]
    public async Task Poll_detects_a_cross_process_write_and_refreshes_with_an_external_signal()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("book", "First");
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        using LibraryRevisionMonitor monitor = new(context.Revisions, context.Cache, TimeSpan.FromMilliseconds(50));
        SignalCounter externalSignals = new();
        SignalCounter refreshes = new();
        monitor.ExternalChangeDetected += (_, _) => externalSignals.Increment();
        monitor.CacheRefreshed += (_, _) => refreshes.Increment();
        monitor.Start();
        monitor.IsRunning.Should().BeTrue();

        // Let the first polls establish the revision baseline so the write below is a real change.
        await Task.Delay(300);

        // Simulate a second process sharing the SQLite file: a separate connection factory whose
        // services commit through their own LibraryRevisionService instance, so nothing publishes
        // on this process's ChangeCommitted event.
        SqliteConnectionFactory secondFactory = new(context.Database.Path);
        try
        {
            LibraryIdentityService secondLibrary = new(secondFactory, context.Clock);
            LibraryRevisionService secondRevisions = new(secondFactory);
            ItemService secondItems = new(secondFactory, secondLibrary, context.Clock, secondRevisions);
            Result<ItemMetadata> second = await secondItems.CreateItemAsync("book", "Second process");
            second.IsSuccess.Should().BeTrue(second.ErrorMessage);

            await WaitUntilAsync(() => externalSignals.Count > 0 && refreshes.Count > 0);
            externalSignals.Count.Should().BeGreaterThan(0);
            refreshes.Count.Should().BeGreaterThan(0);
            context.Cache.QueryByTags(null).Select(row => row.ItemId.ToString())
                .Should().Contain(second.Value.ItemId.ToString(),
                    "the poll-triggered refresh must reload the row written through the second connection");
        }
        finally
        {
            secondFactory.ClearPools();
        }

        monitor.Stop();
        monitor.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task In_process_commit_refreshes_the_cache_without_an_external_signal()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("book", "First");
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        // A long poll interval keeps the polling loop out of this scenario entirely: the refresh
        // must come from the ChangeCommitted subscription alone (Start is never called).
        using LibraryRevisionMonitor monitor = new(context.Revisions, context.Cache, TimeSpan.FromMinutes(1));
        SignalCounter externalSignals = new();
        SignalCounter refreshes = new();
        monitor.ExternalChangeDetected += (_, _) => externalSignals.Increment();
        monitor.CacheRefreshed += (_, _) => refreshes.Increment();

        Result<ItemMetadata> second = await context.Items.CreateItemAsync("book", "Committed in-process");
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);

        await WaitUntilAsync(() => refreshes.Count > 0);
        externalSignals.Count.Should().Be(0,
            "in-process commits arrive via ChangeCommitted and must not be reported as external changes");
        context.Cache.QueryByTags(null).Select(row => row.ItemId).Should()
            .BeEquivalentTo([first.Value.ItemId, second.Value.ItemId]);
    }

    [Fact]
    public async Task Stop_and_dispose_leave_no_running_poll_loop()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Items.CreateItemAsync("book", "First");
        await context.Cache.EnsureLoadedAsync();
        CountingRevisionService counting = new(context.Revisions);
        LibraryRevisionMonitor monitor = new(counting, context.Cache, TimeSpan.FromMilliseconds(50));
        try
        {
            monitor.Start();
            await WaitUntilAsync(() => counting.CurrentRevisionCallCount > 0);

            monitor.Stop();
            monitor.IsRunning.Should().BeFalse();
            long callsAfterStop = counting.CurrentRevisionCallCount;
            await Task.Delay(300);
            counting.CurrentRevisionCallCount.Should().Be(callsAfterStop, "the poll loop must stop polling");

            monitor.Dispose();
            await Task.Delay(100);
            counting.CurrentRevisionCallCount.Should().Be(callsAfterStop, "dispose must not restart the poll loop");
        }
        finally
        {
            monitor.Dispose();
        }
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Revision Monitor Test");
        LibraryRevisionService revisions = new(database.ConnectionFactory);
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        ItemTagService tags = new(database.ConnectionFactory, revisions);
        LibraryItemQueryService query = new(database.ConnectionFactory);
        return new TestContext(database, clock, items, tags, query, revisions,
            new LibraryItemCache(query, tags));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue("the revision monitor should react well within the timeout");
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, FixedClock clock, ItemService items,
            ItemTagService tags, LibraryItemQueryService query, LibraryRevisionService revisions,
            LibraryItemCache cache)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Tags = tags;
            Query = query;
            Revisions = revisions;
            Cache = cache;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public ItemTagService Tags { get; }
        public LibraryItemQueryService Query { get; }
        public LibraryRevisionService Revisions { get; }
        public LibraryItemCache Cache { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CountingRevisionService : ILibraryRevisionService
    {
        private readonly ILibraryRevisionService _inner;

        public CountingRevisionService(ILibraryRevisionService inner)
        {
            _inner = inner;
        }

        public long CurrentRevisionCallCount { get; private set; }

        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted
        {
            add => _inner.ChangeCommitted += value;
            remove => _inner.ChangeCommitted -= value;
        }

        public async Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
        {
            CurrentRevisionCallCount++;
            return await _inner.GetCurrentRevisionAsync(cancellationToken);
        }

        public Task<Result<long>> CommitAsync(LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            return _inner.CommitAsync(changeSet, cancellationToken);
        }

        public Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(DbConnection connection,
            DbTransaction transaction, LibraryChangeSet changeSet, CancellationToken cancellationToken = default)
        {
            return _inner.IncrementInTransactionAsync(connection, transaction, changeSet, cancellationToken);
        }

        public void PublishCommitted(LibraryChangeSet changeSet)
        {
            _inner.PublishCommitted(changeSet);
        }
    }

    private sealed class SignalCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment()
        {
            Interlocked.Increment(ref _count);
        }
    }
}
