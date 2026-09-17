using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;

namespace Patchouli.Tests;

public sealed class ActivityTrackingCoverageTests
{
    [Fact]
    public async Task Search_index_rebuild_reports_indexing_activity_and_releases_it()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        using HostActivityTracker tracker = new(TimeSpan.Zero);
        ConcurrentQueue<HostActivityItem> observed = new();
        tracker.Changed += (_, snapshot) =>
        {
            foreach (HostActivityItem item in snapshot.Items)
            {
                observed.Enqueue(item);
            }
        };
        SearchIndexRebuilder rebuilder = new(database.ConnectionFactory, new FixedClock(DateTimeOffset.UtcNow),
            tracker);

        (await rebuilder.RebuildFtsForLibraryAsync()).IsSuccess.Should().BeTrue();

        observed.Should().ContainSingle(item =>
            item.Kind == HostActivityKind.Indexing && item.Detail == "整个资料库");
        tracker.Current.IsBusy.Should().BeFalse();
        tracker.Current.Items.Should().BeEmpty();
    }
}
