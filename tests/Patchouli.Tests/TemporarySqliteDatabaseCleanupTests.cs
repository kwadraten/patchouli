using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Patchouli.Tests;

public sealed class TemporarySqliteDatabaseCleanupTests
{
    [Fact]
    public async Task Dispose_removes_the_main_file_and_all_sqlite_sidecars()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = "create table t(id integer);";
            await seed.ExecuteNonQueryAsync();
        }

        // Create sidecars deterministically; this verifies file-name coverage, not WAL contents.
        string[] sidecars = [$"{database.Path}-wal", $"{database.Path}-shm", $"{database.Path}-journal"];
        foreach (string sidecar in sidecars)
        {
            await File.WriteAllTextAsync(sidecar, "sidecar");
        }

        await database.DisposeAsync();

        File.Exists(database.Path).Should().BeFalse();
        foreach (string sidecar in sidecars)
        {
            File.Exists(sidecar).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Dispose_releases_the_pool_of_a_raw_data_source_connection()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        // A pooled connection opened outside the factory uses a different pool key; cleanup must
        // still release it once the connection itself is closed.
        await using (SqliteConnection connection = new($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
        }

        await database.DisposeAsync();

        File.Exists(database.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_reports_a_visible_failure_when_a_connection_is_still_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            // POSIX permits unlinking an open SQLite file, so the sharing-violation path this
            // test exercises only exists on Windows.
            return;
        }

        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        SqliteConnection leaked = database.ConnectionFactory.CreateConnection();
        try
        {
            await leaked.OpenAsync();

            IOException failure = await Assert.ThrowsAsync<IOException>(() => database.DisposeAsync().AsTask());
            failure.Message.Should().Contain(database.Path);
            File.Exists(database.Path).Should().BeTrue();
        }
        finally
        {
            await leaked.DisposeAsync();
            await database.DisposeAsync();
        }

        File.Exists(database.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_still_cleans_after_a_faulted_operation()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        Func<Task> faulted = async () =>
        {
            await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "select * from missing_table;";
            await command.ExecuteScalarAsync();
        };

        await faulted.Should().ThrowAsync<SqliteException>();

        await database.DisposeAsync();

        File.Exists(database.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_still_cleans_after_a_cancelled_operation()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        // ReSharper disable once AccessToDisposedClosure -- the lambda is awaited before the
        // cancellation source and database leave scope.
        Func<Task> cancelledWork = async () =>
        {
            await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "create table t(id integer);";
            await command.ExecuteNonQueryAsync(cancelled.Token);
        };

        await cancelledWork.Should().ThrowAsync<OperationCanceledException>();

        await database.DisposeAsync();

        File.Exists(database.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_one_database_does_not_touch_a_live_database()
    {
        TemporarySqliteDatabase databaseA = TemporarySqliteDatabase.Create();
        TemporarySqliteDatabase databaseB = TemporarySqliteDatabase.Create();
        try
        {
            await using (SqliteConnection seed = databaseB.ConnectionFactory.CreateConnection())
            {
                await seed.OpenAsync();
                using SqliteCommand write = seed.CreateCommand();
                write.CommandText = "create table b(id integer); insert into b values (7);";
                await write.ExecuteNonQueryAsync();
            }

            await databaseA.DisposeAsync();

            File.Exists(databaseB.Path).Should().BeTrue();
            await using (SqliteConnection check = databaseB.ConnectionFactory.CreateConnection())
            {
                await check.OpenAsync();
                using SqliteCommand read = check.CreateCommand();
                read.CommandText = "select id from b;";
                (await read.ExecuteScalarAsync()).Should().Be(7L);
            }
        }
        finally
        {
            await databaseB.DisposeAsync();
        }
    }
}
