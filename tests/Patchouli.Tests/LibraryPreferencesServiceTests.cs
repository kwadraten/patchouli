using FluentAssertions;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class LibraryPreferencesServiceTests
{
    [Fact]
    public async Task Column_order_visibility_and_width_round_trip()
    {
        await using TestContext context = await CreateContextAsync();

        Result<LibraryPreferences> saved = await context.Preferences.SavePreferencesAsync(
        [
            new LibraryColumnPreference("title", 0, true, 320),
            new LibraryColumnPreference("authors", 1, false, 180),
            new LibraryColumnPreference("page_count", 2, true, 96)
        ]);
        Result<LibraryPreferences> loaded = await context.Preferences.GetPreferencesAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Columns.Should().HaveCount(3);
        loaded.Value.Columns[1].ColumnKey.Should().Be("authors");
        loaded.Value.Columns[1].Visible.Should().BeFalse();
        loaded.Value.Columns[1].Width.Should().Be(180);
    }

    [Fact]
    public async Task Preferences_are_scoped_by_library_and_scope_name()
    {
        await using TestContext context = await CreateContextAsync();

        await context.Preferences.SavePreferencesAsync([new LibraryColumnPreference("title", 0, true)], "grid");
        await context.Preferences.SavePreferencesAsync([new LibraryColumnPreference("title", 0, false)], "compact");

        Result<LibraryPreferences> grid = await context.Preferences.GetPreferencesAsync("grid");
        Result<LibraryPreferences> compact = await context.Preferences.GetPreferencesAsync("compact");

        grid.Value.Columns.Single().Visible.Should().BeTrue();
        compact.Value.Columns.Single().Visible.Should().BeFalse();
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Preferences Test");
        return new TestContext(database, new LibraryPreferencesService(database.ConnectionFactory, library, clock));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, LibraryPreferencesService preferences)
        {
            Database = database;
            Preferences = preferences;
        }

        public TemporarySqliteDatabase Database { get; }
        public LibraryPreferencesService Preferences { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
