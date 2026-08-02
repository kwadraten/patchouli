using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class LibraryRevisionServiceTests
{
    [Fact]
    public async Task New_library_starts_at_revision_one()
    {
        await using TestContext context = await CreateContextAsync();

        Result<long> revision = await context.Revisions.GetCurrentRevisionAsync();

        revision.IsSuccess.Should().BeTrue();
        revision.Value.Should().Be(1);
    }

    [Fact]
    public async Task Commit_increments_revision_and_publishes_the_typed_change_set()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Revisioned");
        LibraryRevisionCommittedEventArgs? observed = null;
        context.Revisions.ChangeCommitted += (_, args) => observed = args;

        Result<long> committed = await context.Revisions.CommitAsync(
            LibraryChangeSet.Empty with { ItemIds = [item.Value.ItemId] });

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Should().Be(2);
        observed.Should().NotBeNull();
        observed!.ChangeSet.NewRevision.Should().Be(2);
        observed.ChangeSet.ItemIds.Should().Contain(item.Value.ItemId);
        (await context.Revisions.GetCurrentRevisionAsync()).Value.Should().Be(2);
    }

    [Fact]
    public async Task Revision_survives_a_new_service_instance_over_the_same_database()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Revisions.CommitAsync(LibraryChangeSet.Empty);

        LibraryRevisionService reopened = new(context.Database.ConnectionFactory);
        Result<long> revision = await reopened.GetCurrentRevisionAsync();

        revision.Value.Should().Be(2);
    }

    [Fact]
    public async Task Failed_commit_does_not_publish_a_change()
    {
        await using TestContext context = await CreateContextAsync();
        int published = 0;
        context.Revisions.ChangeCommitted += (_, _) => published++;

        await context.Revisions.CommitAsync(LibraryChangeSet.Empty);

        published.Should().Be(1);
    }

    [Fact]
    public async Task Transactional_increment_publishes_only_after_the_host_transaction_commits()
    {
        await using TestContext context = await CreateContextAsync();
        int published = 0;
        context.Revisions.ChangeCommitted += (_, _) => published++;

        await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using DbTransaction transaction = await connection.BeginTransactionAsync();
            Result<LibraryChangeSet> incremented = await context.Revisions.IncrementInTransactionAsync(
                connection, transaction, LibraryChangeSet.Empty);

            incremented.IsSuccess.Should().BeTrue();
            published.Should().Be(0);
            await transaction.RollbackAsync();
        }

        (await context.Revisions.GetCurrentRevisionAsync()).Value.Should().Be(1);
        published.Should().Be(0);
    }

    [Fact]
    public async Task Revision_formatter_round_trips_the_protocol_text()
    {
        LibraryRevisionFormatter.Format(1).Should().Be("lib:1");
        LibraryRevisionFormatter.Format(42).Should().Be("lib:42");
        LibraryRevisionFormatter.TryParse("lib:7", out long revision).Should().BeTrue();
        revision.Should().Be(7);
        LibraryRevisionFormatter.TryParse("lib:", out _).Should().BeFalse();
        LibraryRevisionFormatter.TryParse("style:abc", out _).Should().BeFalse();
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Revision Test");
        return new TestContext(
            database,
            new ItemService(database.ConnectionFactory, library, clock),
            new LibraryRevisionService(database.ConnectionFactory));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, LibraryRevisionService revisions)
        {
            Database = database;
            Items = items;
            Revisions = revisions;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public LibraryRevisionService Revisions { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
